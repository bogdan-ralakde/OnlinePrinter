# LXC / Linux installation (LabelBridge relay)

This installs the **LabelBridge relay** on a Linux host (Proxmox LXC, Incus/LXD, Debian/Ubuntu VM, etc.) as a `systemd` service.

## What gets installed (current build)

- Self-contained `LabelBridge.Relay` binary in `/opt/labelbridge` (no .NET runtime required)
- Systemd service: `labelbridge-relay.service` (runs as unprivileged user `labelbridge`)
- Config: `/etc/labelbridge/relay.env` (listen address, data dir, admin secret, limits)
- Data: `/var/lib/labelbridge` (channels and queued jobs, persisted)

**Exposed by this build:** HTTP relay API, `GET /health`, and a basic status page at `/`.

**Not yet included:** a full admin web UI and server-side printer discovery/selection. Today, printer selection/host configuration is done in the Windows LabelBridge app (Host/Client). See **Architecture note** below.

## Quick install

1. Copy the relay payload to the LXC, e.g. the contents of `dist/relay-linux-x64/` (must include `LabelBridge.Relay` and `install-lxc.sh`).
2. Run the installer as root:

```bash
cd /path/to/relay-linux-x64
sudo bash install-lxc.sh
# or, from a repo checkout:
# sudo bash scripts/install-lxc.sh
```

3. Check status:

```bash
systemctl status labelbridge-relay
curl -s http://127.0.0.1:8080/health
# -> ok channels=0 jobs=0 uptime=...
```

Open the basic status page from your network: `http://<lxc-ip>:8080/`

The installer prints the **admin secret** (also stored as `LB_ADMIN_SECRET` in `/etc/labelbridge/relay.env`). You need it to create a printer channel from the Windows host app.

## Configure (environment file)

Edit `/etc/labelbridge/relay.env` then restart:

```bash
sudo nano /etc/labelbridge/relay.env
sudo systemctl restart labelbridge-relay
```

Important variables:

| Variable | Default | Meaning |
|---|---|---|
| `LB_LISTEN` | `http://+:8080/` | HTTP listener (put TLS/reverse proxy in front for internet) |
| `LB_DATA` | `/var/lib/labelbridge` | Persistent storage (channels, queued jobs) |
| `LB_ADMIN_SECRET` | *(generated)* | Required in `X-LB-Admin` when creating channels |
| `LB_RETENTION_HOURS` | `168` | Delete unclaimed jobs after N hours |
| `LB_MAX_MB` | `64` | Max single job size (MB) |
| `LB_MAX_JOBS` | `1000` | Max queued jobs per channel |

## HTTPS / reverse proxy (recommended for internet use)

Do **not** expose the relay over plain HTTP on the public internet (tokens and label payloads would be unencrypted). Terminate TLS in front (Caddy/nginx/Traefik/Cloudflare Tunnel) and proxy to `http://127.0.0.1:8080`.

Long-polling requirements (host polls with `wait=25`):
- disable response buffering for `/api/` (nginx `proxy_buffering off;`, Caddy `flush_interval -1`)
- allow read timeout **>= 120s**
- allow request body >= `LB_MAX_MB`

Example (Caddy):

```caddyfile
print.example.com {
    reverse_proxy 127.0.0.1:8080 {
        flush_interval -1
        transport http { read_timeout 120s }
    }
}
```

More examples: `docs/relay-truenas.md`.

## Connect Windows host/client

1. **Printer-side Windows PC (Host):** install/run LabelBridge → Connection tab → set Relay URL (`https://print.example.com` or `http://<lxc-ip>:8080` on a trusted LAN) + admin secret → **Create channel** → copy pairing code.
2. **Remote Windows PC (Client):** install/run LabelBridge → paste pairing code → print to the virtual "LabelBridge" printer.
3. The Windows host agent (on the printer LAN) forwards jobs to the real Zebra printer (e.g. `192.168.50.25:9100`).

## Upgrade

Re-run the installer with the new binary; it stops the service, replaces the binary, keeps config/data, and restarts:

```bash
sudo bash install-lxc.sh ./LabelBridge.Relay
```

## Uninstall

```bash
sudo bash install-lxc.sh --uninstall         # keep /etc/labelbridge and /var/lib/labelbridge
sudo bash install-lxc.sh --uninstall --purge # remove everything (including queued jobs)
```

## Architecture note (printer discovery/selection)

- The relay/server is currently a **store-and-forward** service: it queues jobs between Windows clients and a Windows host agent. It does **not** connect to printers and cannot discover printers on a remote LAN.
- **If the LXC is on the same LAN as the printer**, a future/direct-print mode could let the server discover and print to the printer itself (feature not implemented yet).
- **If the LXC is remote from the printer LAN** (common), "find/select printer from server web UI" must be implemented as **remote configuration of the Windows host agent** (host discovers local printers and reports them; server UI selects and pushes config) — also not implemented yet.

To implement a full admin web UI and server-side printer discovery/selection, clarify: (1) is the LXC on the same network as the printer? (2) LAN-only or internet-facing UI, and desired authentication?
