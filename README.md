# Online Printer

Online Printer v1.1.1 is an all-in-one label print server with a web UI plus a Windows client.

- **Online Printer Server (Linux/systemd, Docker, or console)**: admin login/setup, client approval, printer configuration (manual IP:port, probe/scan/test), durable queue, and direct printing to a network printer over TCP/9100.
- **Online Printer Client (Windows)**: installed by a single setup EXE. It installs a local virtual printer named `Online Printer`; any app can print to it, the client captures jobs, uploads them to the approved server, and the server forwards them directly to the configured network printer.
- Clients connect to the server URL and are approved in the web UI (no Windows host-agent/relay pairing).

## Architecture

```
Windows app -> local "Online Printer" virtual printer -> Online Printer Client
    -> HTTPS/HTTP to Online Printer Server (web UI/API, admin-approved client)
    -> durable queue on server
    -> direct TCP/9100 to network label printer
```

## Supported capabilities

- **Server web UI/API**: `/health`, admin auth/password/setup, printer config, TCP/9100 probe/scan/test print, client register/status/claim/approve/revoke/delete, driver upload/select/download, job queue/status, and basic update info/apply endpoint.
- **Windows client**: local capture on `127.0.0.1:<capture port>`, durable local queue with retry, registration/approval polling and token claim, server profile/driver metadata fetch, driver package download (SHA256 checked when provided by server metadata), best-effort driver installer launch and virtual-printer install/repair, GitHub release update check and best-effort client update apply.
- **Generic printer/profile fields** (name/size/DPI/orientation/media/method/darkness/speed/driver hint/print language) are stored on the server and pushed to approved clients.
- **Automatic printer identity detection:** when a printer host/port is saved or tested, the server queries the printer over TCP/9100 (ZPL/EPL/TSPL/CPCL/ESC-POS/PJL identity commands) and fills in **make**, **model**, print language, and a suggested Windows driver hint. The web UI also has a **Detect printer** button. Approved clients receive these fields and automatically pick a matching installed Windows driver by make/model/language and install/repair the `Online Printer` queue. Detection is best-effort and never overwrites admin-entered values.

## Important limitations (v1.1.1)

- **No print-format conversion is bundled.** Jobs are forwarded as raw bytes over TCP/9100.
  - **Rejected:** empty jobs and Windows-rendered spool formats (`EMF`, `XPS`) with actionable errors.
  - **Accepted/pass-through:** `ZPL`, `EPL`, `ESC/POS`, `PostScript`, `PDF`, and unknown raw data (printer/profile must support the format).
  - `PDF` is passed through only; if your printer does not natively accept PDF, use a driver/profile that produces a printer-native language (for example ZPL/EPL).
- **Driver/profile auto-apply is best-effort.** The server can host an unmodified driver package and profile metadata, and the client can download and launch installers and install/repair the `Online Printer` printer. Actual silent install, driver binding, and Printing Preferences/Defaults configuration depend on the provided driver package, permissions (admin/UAC), and driver capabilities; manual steps may still be required.
- **Auto-update apply is best-effort.**
  - **Server:** Linux/systemd installs can download the latest `labelbridge-relay-linux-x64.tar.gz`, verify checksum when a release checksum asset is available, replace the binary if writable, and request restart; otherwise a manual upgrade command is shown.
  - **Windows client:** checks GitHub releases, downloads the `OnlinePrinterClient-*-win-x64.zip` package, verifies checksum when available, and launches the elevated setup EXE directly (no PowerShell/CMD); manual install from release assets remains the fallback.
- **Not fully end-to-end validated in this environment:** real network printers, elevated Windows driver/virtual-printer install, Linux/Docker deployment, and production GitHub release publish/download flows.

## Server installation (Linux/systemd)

Install latest release (public repo):

```bash
curl -fsSL https://raw.githubusercontent.com/bogdan-ralakde/OnlinePrinter/master/scripts/install-linux.sh | sudo bash
```

Install specific version:

```bash
curl -fsSL https://raw.githubusercontent.com/bogdan-ralakde/OnlinePrinter/master/scripts/install-linux.sh | sudo LABELBRIDGE_VERSION=v1.1.1 bash
```

Install/runtime contract (kept for upgrades):
- Service: `labelbridge-relay`
- Binary: `/opt/labelbridge/LabelBridge.Relay`
- Config/env: `/etc/labelbridge/relay.env` (`LB_*` vars)
- Data: `/var/lib/labelbridge`
- Health: `GET /health` and `LabelBridge.Relay --health-check <url>`
- Release asset: `labelbridge-relay-linux-x64.tar.gz`

See [`docs/linux-install.md`](docs/linux-install.md) for details, reverse proxy/HTTPS guidance, upgrade, and uninstall.

## Docker

Docker assets are in [`docker/`](docker/). The container runs the server binary (`LabelBridge.Relay`) and serves the web UI/API on port 8080 with data in `/data`. Use a TLS reverse proxy for internet access.

## Windows client installation and flow

1. Download `OnlinePrinterClient-1.1.1-win-x64.zip` (or `OnlinePrinterClient-Setup.exe`) from the release and run **`OnlinePrinterClient-Setup.exe`**. It is a single EXE installer; UAC approval is requested automatically. No PowerShell, CMD, or batch files are needed.
2. Start **Online Printer Client** from the Start menu or desktop shortcut.
3. **Server & Approval** tab: set server base URL and device name, then **Request / Register**.
4. Approve the client in the server web UI (**Clients** tab).
5. After approval the client claims a token and fetches printer/profile/driver info. Download/install the server-provided driver package if required (admin/UAC may be needed).
6. **Virtual Printer** tab: install/repair the local `Online Printer` printer (select a driver compatible with your printer/profile; e.g. ZPL-capable driver for a ZPL printer).
7. Print from any Windows application to printer **Online Printer**. Jobs are captured locally, queued, uploaded to the server, queued server-side, and printed directly to the configured network printer (TCP/9100).

To uninstall, use **Apps & features / Add or remove programs -> Online Printer Client**, or run the installed `OnlinePrinterClient-Setup.exe --uninstall`.

CLI helpers (from the installed `OnlinePrinter.Client.exe`):

```
OnlinePrinter.Client.exe --help
OnlinePrinter.Client.exe --paths
OnlinePrinter.Client.exe --printer-status
OnlinePrinter.Client.exe --printer-install [--driver <name>] [--port <n>]
OnlinePrinter.Client.exe --printer-remove
OnlinePrinter.Client.exe --selftest
OnlinePrinter.Client.exe --send <file> [--server <url> --token <approved-client-token>]
```

## Server web UI (admin)

- **Initial setup (first run):** if no admin password is configured, opening the web UI prompts you to **create the admin password** (username defaults to `admin`). Optionally the password can be preconfigured via `LB_ADMIN_SECRET` / installer prompt for automated setups.
- **Login:** sign in as `admin` with the configured password; change it in **Settings** when needed.
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
dotnet exec LabelBridge/src/LabelBridge.Windows/bin/Release/net8.0-windows/OnlinePrinter.Client.dll --selftest

# Package Windows client setup EXE + Linux server asset (developer build tooling)
powershell -ExecutionPolicy Bypass -File LabelBridge/installer/Build.ps1 -Version 1.1.1
```

`installer/Build.ps1` produces `dist/OnlinePrinterClient-Setup.exe`, `dist/OnlinePrinterClient-<version>-win-x64.zip`, and (unless skipped) `dist/labelbridge-relay-linux-x64.tar.gz`.

## Releases

- Version: `1.1.1`
- Release assets:
  - `labelbridge-relay-linux-x64.tar.gz` (Linux server install & server auto-update)
  - `OnlinePrinterClient-Setup.exe` (single EXE Windows installer)
  - `OnlinePrinterClient-1.1.1-win-x64.zip` (same setup EXE, used by client auto-update)
  - `SHA256SUMS.txt` (checksums)

## Repository layout

```
LabelBridge/                        (source repository name kept for compatibility)
  src/LabelBridge.Core/             server logic, storage, queue/print services, embedded web UI, shared utilities
  src/LabelBridge.Windows/          Windows client UI/services/capture/printer helpers/CLI
  src/LabelBridge.Relay/            server host/entrypoint (binary/service name kept for install compatibility)
  scripts/install-linux.sh          Linux (systemd) install/upgrade/uninstall script
  installer/OnlinePrinter.Setup/    single-EXE Windows installer source
  installer/Build.ps1               developer packaging script (not shipped to users)
  docker/                           Dockerfile, compose, entrypoint
  docs/linux-install.md             Linux server deployment notes
  docs/troubleshooting.md           troubleshooting
```

## Troubleshooting

See [`docs/troubleshooting.md`](docs/troubleshooting.md).

## Security notes

- Use HTTPS (reverse proxy/TLS termination) when exposing the server over the internet.
- Protect admin credentials and approve only trusted clients.
- Client tokens are issued on approval/claim and required for client API access; revoke compromised clients in the web UI.
- Uploaded driver packages are stored on the server; only upload trusted, unmodified vendor packages.
