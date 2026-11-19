# LabelBridge

LabelBridge v1.0.0 is an all-in-one label print server with a web UI plus a Windows client.

- **Server (Linux LXC/systemd, Docker, or console)**: admin login/setup, client approval, printer configuration (manual IP:port, probe/scan/test), durable queue, and direct printing to a network printer over TCP/9100.
- **Windows client (Option A)**: installs a local virtual printer named `LabelBridge`. Any app can print to it; the client captures jobs, uploads them to the approved server, and the server forwards them directly to the configured network printer.
- **No Windows host-agent / relay pairing flow** in v1.0.0. Clients connect to the server URL and are approved in the web UI.

## Architecture

```
Windows app -> local "LabelBridge" virtual printer -> LabelBridge Windows client
    -> HTTPS/HTTP to LabelBridge server (web UI/API, admin-approved client)
    -> durable queue on server
    -> direct TCP/9100 to network label printer
```

## Supported capabilities

- **Server web UI/API**: `/health`, admin auth/password/setup, printer config, TCP/9100 probe/scan/test print, client register/status/claim/approve/revoke/delete, driver upload/select/download, job queue/status, and basic update info/apply endpoint.
- **Windows client**: local capture on `127.0.0.1:<capture port>`, durable local queue with retry, registration/approval polling and token claim, server profile/driver metadata fetch, driver package download (SHA256 checked when provided by server metadata), best-effort driver installer launch and virtual-printer install/repair, GitHub release update check and best-effort client update apply.
- **Generic printer/profile fields** (name/size/DPI/orientation/media/method/darkness/speed/driver hint/print language) are stored on the server and pushed to approved clients.

## Important limitations (v1.0.0)

- **No print-format conversion is bundled.** Jobs are forwarded as raw bytes over TCP/9100.
  - **Rejected:** empty jobs and Windows-rendered spool formats (`EMF`, `XPS`) with actionable errors.
  - **Accepted/pass-through:** `ZPL`, `EPL`, `ESC/POS`, `PostScript`, `PDF`, and unknown raw data (printer/profile must support the format).
  - `PDF` is passed through only; if your printer does not natively accept PDF, use a driver/profile that produces a printer-native language (for example ZPL/EPL).
- **Driver/profile auto-apply is best-effort.** The server can host an unmodified driver package and profile metadata, and the client can download and launch installers and install/repair the `LabelBridge` printer. Actual silent install, driver binding, and Printing Preferences/Defaults configuration depend on the provided driver package, permissions (admin/UAC), and driver capabilities; manual steps may still be required.
- **Auto-update apply is best-effort.**
  - **Server:** Linux/systemd installs can download the latest `labelbridge-relay-linux-x64.tar.gz`, verify checksum when a release checksum asset is available, replace the binary if writable, and request restart; otherwise a manual upgrade command is shown.
  - **Windows client:** checks GitHub releases, downloads a Windows asset (`LabelBridge-*-win-x64.zip` preferred), verifies checksum when available, stages and launches an elevated updater; manual install from release assets remains the fallback.
- **Not fully end-to-end validated in this environment:** real network printers, elevated Windows driver/virtual-printer install, LXC/Docker deployment, and production GitHub release publish/download flows.

## Server installation (Linux LXC/systemd, preserved contract)

Install latest release (public repo):

```bash
curl -fsSL https://raw.githubusercontent.com/bogdan-ralakde/OnlinePrinter/master/scripts/install-lxc.sh | sudo bash
```

Install specific version:

```bash
curl -fsSL https://raw.githubusercontent.com/bogdan-ralakde/OnlinePrinter/master/scripts/install-lxc.sh | sudo LABELBRIDGE_VERSION=v1.0.0 bash
```

Preserved install/runtime contract:
- Service: `labelbridge-relay`
- Binary: `/opt/labelbridge/LabelBridge.Relay`
- Config/env: `/etc/labelbridge/relay.env` (`LB_*` vars)
- Data: `/var/lib/labelbridge`
- Health: `GET /health` and `LabelBridge.Relay --health-check <url>`
- Release asset: `labelbridge-relay-linux-x64.tar.gz`

See [`docs/lxc-install.md`](docs/lxc-install.md) for details, reverse proxy/HTTPS guidance, upgrade, and uninstall.

## Docker

Docker assets are in [`docker/`](docker/). The container runs the server binary (`LabelBridge.Relay`) and serves the web UI/API on port 8080 with data in `/data`. Use a TLS reverse proxy for internet access.

## Windows client installation and flow

1. Build or download the Windows client package (`LabelBridge-1.0.0-win-x64.zip`) and run `Setup.cmd` / `Install.ps1` as administrator.
2. Start **LabelBridge Client**.
3. **Server & Approval** tab: set server base URL and device name, then **Request / Register**.
4. Approve the client in the server web UI (**Clients** tab).
5. After approval the client claims a token and fetches printer/profile/driver info. Download/install the server-provided driver package if required (admin/UAC may be needed).
6. **Virtual Printer** tab: install/repair the local `LabelBridge` printer (select a driver compatible with your printer/profile; e.g. ZPL-capable driver for a ZPL printer).
7. Print from any Windows application to printer **LabelBridge**. Jobs are captured locally, queued, uploaded to the server, queued server-side, and printed directly to the configured network printer (TCP/9100).

CLI helpers:

```powershell
LabelBridge.exe --help
LabelBridge.exe --paths
LabelBridge.exe --printer-status
LabelBridge.exe --printer-install [--driver <name>] [--port <n>]
LabelBridge.exe --printer-remove
LabelBridge.exe --selftest
LabelBridge.exe --send <file> [--server <url> --token <approved-client-token>]
```

## Server web UI (admin)

- **Setup/Login:** initial admin password comes from `LB_ADMIN_SECRET` on first setup; change it in **Settings**.
- **Printer:** configure name/host/port/profile, test connection, scan subnet, send test label, enable/disable printing.
- **Clients:** approve/decline/revoke/delete registered clients.
- **Drivers:** upload unmodified driver packages/installers, select active package for clients, delete.
- **Jobs:** view recent/queued/printed/failed jobs.
- **Settings:** change admin password, view client connection info, check/apply server updates (best-effort on Linux/systemd).

## Build

Requires .NET SDK 8.0+.

```powershell
# Build solution
dotnet build LabelBridge/LabelBridge.sln -c Release -m:1

# Windows client self-test
dotnet exec LabelBridge/src/LabelBridge.Windows/bin/Release/net8.0-windows/LabelBridge.dll --selftest

# Package Windows client + Linux server asset
powershell -ExecutionPolicy Bypass -File LabelBridge/installer/Build.ps1 -Version 1.0.0
```

`installer/Build.ps1` produces `dist/LabelBridge/`, `dist/LabelBridge-<version>-win-x64.zip`, and (unless skipped) `dist/labelbridge-relay-linux-x64.tar.gz`.

## Releases

- Version: `1.0.0`
- Expected release assets for installer/update flows:
  - `labelbridge-relay-linux-x64.tar.gz` (server/LXC install & server auto-update)
  - `LabelBridge-1.0.0-win-x64.zip` (Windows client package & client auto-update)
  - Optional checksum asset (for example `SHA256SUMS.txt`) for integrity verification

## Repository layout

```
LabelBridge/
  src/LabelBridge.Core/        server logic, storage, queue/print services, embedded web UI, shared utilities
  src/LabelBridge.Windows/     Windows client UI/services/capture/printer helpers/CLI
  src/LabelBridge.Relay/       server host/entrypoint (binary name kept for LXC contract)
  scripts/install-lxc.sh       Linux LXC/systemd install/upgrade/uninstall (auto-download release asset)
  installer/                   Windows package build/install/uninstall scripts
  docker/                      Dockerfile, compose, entrypoint
  docs/lxc-install.md          LXC/server deployment notes
  docs/troubleshooting.md      troubleshooting
```

## Troubleshooting

See [`docs/troubleshooting.md`](docs/troubleshooting.md).

## Security notes

- Use HTTPS (reverse proxy/TLS termination) when exposing the server over the internet.
- Protect admin credentials and approve only trusted clients.
- Client tokens are issued on approval/claim and required for client API access; revoke compromised clients in the web UI.
- Uploaded driver packages are stored on the server; only upload trusted, unmodified vendor packages.
