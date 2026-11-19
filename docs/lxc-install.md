# LXC / Linux installation (LabelBridge all-in-one server)

This installs the **LabelBridge server** on a Linux host (Proxmox LXC, Incus/LXD, Debian/Ubuntu VM, etc.) as a `systemd` service. The server is intended to run on the same LAN as the network printer (and can be exposed over the internet behind HTTPS for client access).

## What gets installed

- Self-contained `LabelBridge.Relay` binary in `/opt/labelbridge` (kept name for upgrade/install compatibility)
- Systemd service: `labelbridge-relay` (runs as unprivileged user `labelbridge`)
- Config: `/etc/labelbridge/relay.env` (listen address, data dir, initial admin secret, limits)
- Data: `/var/lib/labelbridge` (server state, queue, uploaded drivers, jobs)

**Provided by this build:** web UI + admin authentication, printer configuration (manual IP:port), TCP/9100 subnet scan/test/print, client registration with admin approve/decline/revoke, durable job queue and direct printing to the configured network printer, driver/profile hosting APIs for clients, and basic update info endpoint. `GET /health` remains available for checks.

## Quick install (latest release)

```bash
curl -fsSL https://raw.githubusercontent.com/bogdan-ralakde/OnlinePrinter/master/scripts/install-lxc.sh | sudo bash
```

Install a specific release:

```bash
curl -fsSL https://raw.githubusercontent.com/bogdan-ralakde/OnlinePrinter/master/scripts/install-lxc.sh | sudo LABELBRIDGE_VERSION=v1.0.0 bash
```

Local/bundle install:

```bash
sudo bash scripts/install-lxc.sh
# or
sudo bash scripts/install-lxc.sh --local /path/to/relay-linux-x64
```

## First run

1. Check service: `systemctl status labelbridge-relay` and `curl -s http://127.0.0.1:8080/health`
2. Open web UI: `http://<server-ip>:8080/`
3. Login as `admin`.
   - `LB_ADMIN_SECRET` in `/etc/labelbridge/relay.env` is used as the **initial admin password** on first setup (installer generates and prints it). Change it in **Settings** after login.
4. Configure printer in **Printer** tab (e.g. `192.168.50.25:9100`, profile like Zebra ZD420 300dpi ZPL as applicable), run **Test connection** / **Send test label**, and/or use **Scan**.
5. (Optional) Upload/select a Windows driver/installer package in **Drivers** so approved clients can download it.
6. Install/run LabelBridge Windows client, set server URL, submit connection request, approve in **Clients**, then install/repair the local "LabelBridge" virtual printer and print.

## Configure (environment file)

Edit `/etc/labelbridge/relay.env` then restart:

```bash
sudo nano /etc/labelbridge/relay.env
sudo systemctl restart labelbridge-relay
```

| Variable | Default | Meaning |
|---|---|---|
| `LB_LISTEN` | `http://+:8080/` | HTTP listener (web UI + API) |
| `LB_DATA` | `/var/lib/labelbridge` | Persistent storage (state, queue, drivers, jobs) |
| `LB_ADMIN_SECRET` | *(generated)* | Initial admin password for first setup/login |
| `LB_RETENTION_HOURS` | `168` | Retention window (hours) |
| `LB_MAX_MB` | `128` | Max upload/job size (MB) |
| `LB_MAX_JOBS` | `2000` | Max queued jobs |
| `LB_REPO` | `bogdan-ralakde/OnlinePrinter` | Repo used for update checks/info |

## HTTPS / reverse proxy (recommended for internet use)

Do not expose plain HTTP publicly if clients connect over the internet (credentials/tokens/job data). Put TLS reverse proxy in front (Caddy/nginx/Traefik/Cloudflare Tunnel) and proxy to `http://127.0.0.1:8080`.

Example (Caddy):

```caddyfile
print.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

## Upgrade

Re-run the installer (or install a newer release tag). It stops the service, replaces the binary, keeps `/etc/labelbridge/relay.env` and `/var/lib/labelbridge`, and restarts:

```bash
sudo bash install-lxc.sh
# or local: sudo bash install-lxc.sh --local /path/to/LabelBridge.Relay
```

## Uninstall

```bash
sudo bash install-lxc.sh --uninstall         # keep config and data
sudo bash install-lxc.sh --uninstall --purge # remove everything
```

## Notes

- The LXC/server must be able to reach the network printer over TCP (typically port 9100).
- Printing raw captured data depends on printer/driver compatibility (e.g. ZPL to a ZPL printer). If a Windows driver produces PDF/EMF/XPS, the target printer must support that format or an appropriate driver/profile/conversion must be used.
- For internet exposure, use HTTPS and strong admin credentials; approve only trusted clients.
