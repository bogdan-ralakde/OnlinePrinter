# Deploying the relay

The relay is a small .NET 8 service. It speaks plain HTTP and keeps everything on disk, so any
container host works. Put TLS in front of it for internet use.

- [Option A — Docker Compose (recommended, incl. TrueNAS SCALE)](#option-a--docker-compose)
- [Option B — Linux LXC with systemd](#option-b--linux-lxc-with-systemd)
- [Option C — plain docker run](#option-c--plain-docker-run)
- [HTTPS in front of the relay](#https-in-front-of-the-relay)
- [Verifying the deployment](#verifying-the-deployment)
- [Operations](#operations)

---

## Option A — Docker Compose

TrueNAS SCALE: **Apps → Discover Apps → Custom App** (or run compose from a shell inside a
dataset, e.g. `/mnt/tank/apps/labelbridge`). The host must reach a Docker daemon; TrueNAS SCALE
ships one.

1. Copy `src/`, `docker/` and `.dockerignore` from the build output/repository to the NAS, keeping
   the same layout (the compose file builds from the repository root):

   ```
   /mnt/tank/apps/labelbridge/
     src/LabelBridge.Core/
     src/LabelBridge.Relay/
     docker/Dockerfile
     docker/docker-compose.yml
     docker/entrypoint.sh
     .dockerignore
   ```

2. Create the secret:

   ```bash
   cd /mnt/tank/apps/labelbridge
   echo "LB_ADMIN_SECRET=$(openssl rand -base64 24 | tr -d '/+=' | head -c 32)" > .env
   chmod 600 .env
   ```

3. Start it:

   ```bash
   docker compose -f docker/docker-compose.yml up -d --build
   docker compose -f docker/docker-compose.yml logs -f
   ```

4. The container listens on `8080`, stores data in the named volume `labelbridge-data`
   (mounted at `/data`), runs as uid 10001, and has a healthcheck on `/health`.

> On TrueNAS SCALE the compose plugin may not be installed. You can also paste the service
> definition into **Apps → Custom App** using the YAML editor; keep `build:` out and instead build
> the image on a workstation and push it, or point `image:` at a locally built tag
> (`docker build -f docker/Dockerfile -t labelbridge/relay:1.0.0 .`).

---

## Option B — Linux LXC with systemd

Works on Proxmox LXC, Incus/LXD, or any minimal Debian/Ubuntu container. No Docker needed — the
relay ships as a self-contained `linux-x64` binary (`dist/relay-linux-x64` from `Build.ps1`).

```bash
# on the LXC (Debian/Ubuntu template), as root
useradd --system --home /opt/labelbridge --shell /usr/sbin/nologin labelbridge
install -d -o labelbridge -g labelbridge /opt/labelbridge /var/lib/labelbridge

# copy LabelBridge.Relay (and nothing else) from the build output
install -o labelbridge -g labelbridge -m 0755 LabelBridge.Relay /opt/labelbridge/

cat >/etc/systemd/system/labelbridge-relay.service <<'EOF'
[Unit]
Description=LabelBridge print relay
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=labelbridge
Group=labelbridge
WorkingDirectory=/opt/labelbridge
Environment=LB_LISTEN=http://+:8080/
Environment=LB_DATA=/var/lib/labelbridge
Environment=LB_ADMIN_SECRET=change-me-to-a-long-random-string
Environment=LB_RETENTION_HOURS=168
ExecStart=/opt/labelbridge/LabelBridge.Relay
Restart=always
RestartSec=3
# hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
ReadWritePaths=/var/lib/labelbridge

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now labelbridge-relay
systemctl status labelbridge-relay --no-pager
journalctl -u labelbridge-relay -f
```

LXC network notes: if the LXC is behind NAT or a bridge, make sure the reverse proxy (or the host)
can reach `http://<lxc-ip>:8080`. `HttpListener` on Linux has no `+` privilege requirement; the
service can run as an unprivileged user as shown.

---

## Option C — plain docker run

```bash
docker build -f docker/Dockerfile -t labelbridge/relay:1.0.0 .

docker run -d --name labelbridge-relay --restart unless-stopped \
  -p 8080:8080 \
  -v labelbridge-data:/data \
  -e LB_ADMIN_SECRET='a-long-random-string' \
  -e LB_RETENTION_HOURS=168 \
  --health-cmd 'dotnet /app/LabelBridge.Relay.dll --health-check http://127.0.0.1:8080/health' \
  --health-interval 30s \
  labelbridge/relay:1.0.0
```

If you bind-mount a host folder instead of a named volume, give it the container UID:

```bash
mkdir -p /srv/labelbridge && chown -R 10001:10001 /srv/labelbridge
docker run ... -v /srv/labelbridge:/data ...
```

---

## HTTPS in front of the relay

The relay must be reachable as `https://…` from clients in other countries, so terminate TLS in
front of it. Two properties matter for long polling:

- allow requests to idle for **more than 30 seconds** (the host polls with `wait=25`)
- do **not buffer** `/api/v1/...` responses

### Caddy (automatic Let's Encrypt) — recommended

```caddyfile
print.example.com {
    encode zstd gzip
    reverse_proxy 127.0.0.1:8080 {
        flush_interval -1
        transport http {
            read_timeout 120s
        }
    }
}
```

Caddy's `flush_interval -1` disables response buffering, which keeps long polls instant.

### nginx

```nginx
server {
    listen 443 ssl http2;
    server_name print.example.com;

    ssl_certificate     /etc/ssl/fullchain.pem;
    ssl_certificate_key /etc/ssl/privkey.pem;

    client_max_body_size 70m;          # must exceed LB_MAX_MB

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

        proxy_buffering off;           # important for long polls
        proxy_read_timeout 120s;
        proxy_send_timeout 120s;
    }
}
```

### Traefik (labels)

```yaml
labels:
  - traefik.enable=true
  - traefik.http.routers.labelbridge.rule=Host(`print.example.com`)
  - traefik.http.routers.labelbridge.entrypoints=websecure
  - traefik.http.routers.labelbridge.tls.certresolver=le
  - traefik.http.services.labelbridge.loadbalancer.server.port=8080
  - traefik.http.services.labelbridge.loadbalancer.responseforwarding.flushinterval=-1
```

### Cloudflare Tunnel (no open ports at all)

```bash
cloudflared tunnel create labelbridge
cloudflared tunnel route dns labelbridge print.example.com
cloudflared tunnel run --url http://127.0.0.1:8080 labelbridge
```

Cloudflare's proxy allows long-lived requests and does not break the 25 s poll. Remember that in
this setup Cloudflare terminates TLS, so the host's pairing code uses the
`https://print.example.com` URL.

> **Do not expose the relay over plain HTTP on the internet.** The client token travels in a
> header and label payloads are unencrypted without TLS.

---

## Verifying the deployment

```bash
curl -s http://127.0.0.1:8080/health
# ok channels=0 jobs=0 uptime=0h00m

curl -s -X POST -H "X-LB-Admin: $LB_ADMIN_SECRET" \
     -H 'Content-Type: application/json' -d '{"name":"zd420"}' \
     http://127.0.0.1:8080/api/v1/channels
# {"channelId":"…","hostToken":"…","clientToken":"…"}
```

Then from the Windows host: **Connection** tab → relay URL → admin secret → **Test relay** →
**Create channel**. The channel appears in the relay log and on the status page at `/`.

You can also browse `https://print.example.com/` for a small status page (channels, jobs accepted,
uptime, data directory).

---

## Operations

**What is on disk** (inside `/data`):

```
channels/<channelId>/channel.json   metadata + SHA-256 of both tokens
channels/<channelId>/jobs/*.job      waiting for the host
channels/<channelId>/inflight/*.job  handed to the host, waiting for ack
```

`inflight` jobs return to `jobs` automatically after a relay restart or 10 minutes, so a host
crash cannot lose a label. Unclaimed jobs older than `LB_RETENTION_HOURS` are deleted.

**Backup** — the volume is the whole state. Snapshot it or copy `/data`. There is no database.

**Upgrade** — pull the new image (or replace the binary) and restart; jobs are untouched:

```bash
docker compose -f docker/docker-compose.yml up -d --build
```

**Monitoring** — `GET /health` returns `200` with counters. Use the container healthcheck or your
NAS's uptime monitor. Errors are logged to stdout (visible via `docker logs` / `journalctl`).

**Resource use** — a few dozen MB of RAM; CPU is negligible. Only disk grows with the queue.

**Deleting a printer channel** — host app **Connection** tab (or
`DELETE /api/v1/channels/<id>` with the host token). This removes its queued jobs and invalidates
both tokens.

**Rate/abuse limits** — `LB_MAX_JOBS` (per channel), `LB_MAX_MB` (per job), `LB_MAX_CHANNELS`
(default 20000, code-only) and an admin secret on channel creation. There is no per-IP throttling;
keep the admin secret private and put the relay behind your usual edge protection if it is public.
