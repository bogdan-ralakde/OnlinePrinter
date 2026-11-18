# LabelBridge

Print to a label printer that is somewhere else.

LabelBridge installs a **normal Windows printer** on a remote PC. Anything printed to it is
captured, queued, sent over the internet, and printed on the real label printer — typically a
Zebra ZD420 running ZPL. No port forwarding is needed on either side; both Windows PCs only make
outbound connections.

```
 Remote PC (any country)                    Your server / NAS                 Printer LAN
┌──────────────────────────────┐          ┌────────────────────┐          ┌─────────────────────┐
│ LabelBridge client (GUI)     │  HTTPS   │ LabelBridge relay  │          │ LabelBridge host    │
│ virtual printer "LabelBridge"│ ───────► │ channels + durable │ ◄─────── │ agent (GUI)         │
│ (ZDesigner ZPL driver)       │ outbound │ job queues         │ outbound │ 127.0.0.1:9100 ──►  │
└──────────────────────────────┘          └────────────────────┘          │ Zebra ZD420 (ZPL)   │
                                                                          └─────────────────────┘
```

- **Client PC** — has a virtual printer. Printing there produces ZPL through the normal Windows
  GDI path, which LabelBridge captures and delivers.
- **Relay** — a small container/LXC on a server you control. Stores jobs on disk until the host
  acknowledges them. No printer credentials are stored there.
- **Host PC** — next to the printer. Long-polls the relay and forwards jobs to the printer over
  TCP 9100 (or through an installed Windows queue).

Everything is retried with exponential backoff and survives restarts, app updates, and network
outages on both sides.

---

## Quick start

### 1. Start the relay (once)

On TrueNAS SCALE, any Docker host, or a Linux LXC:

```bash
mkdir -p /opt/labelbridge && cd /opt/labelbridge
# copy docker/docker-compose.yml and docker/Dockerfile's parent folder here (see docs/relay-truenas.md)
echo "LB_ADMIN_SECRET=$(head -c 32 /dev/urandom | base64 | tr -d '/+=')" > .env
docker compose up -d --build
curl http://127.0.0.1:8080/health     # -> ok channels=0 jobs=0 uptime=0h00m
```

Put HTTPS in front of it (Caddy, Traefik, nginx, or Cloudflare Tunnel). Full instructions:
[`docs/relay-truenas.md`](docs/relay-truenas.md).

### 2. Install on the printer PC — choose **Host**

1. Install the **Zebra ZDesigner driver for your exact model, ZPL version**
   (here: `ZDesigner ZD420-300dpi ZPL`). LabelBridge does not ship drivers.
2. Run `Setup.cmd` from the package and approve the administrator prompt.
3. Open LabelBridge → **Overview** → select **Host** → **Apply & restart**.
4. **Printer** tab → enter the printer's IP (e.g. `192.168.50.25`, port `9100`) →
   **Check printer** → **Send test label**.
5. **Connection** tab → enter your relay URL (e.g. `https://print.example.com`) and the admin
   secret → **Create channel**.
6. **Pairing** tab → **Copy code** and send it to the remote users (chat/e-mail is fine, but treat
   it as a password — see [Security](#security)).

### 3. Install on each remote PC — choose **Client**

1. Run `Setup.cmd` (answer **Y** to install the virtual printer). If the ZDesigner driver is
   missing, install it from Zebra first.
2. Open LabelBridge → **Overview** → select **Client** → **Apply & restart**.
3. **Pairing** tab → paste the code from the host → **Use this code**.
4. Print from Notepad, Word, your ERP… choose the printer **LabelBridge**.
5. Watch the job in the **Jobs** tab (with a text/hex preview of the captured ZPL).

That's it. If the printer or the internet is temporarily down, jobs wait in the queue and print
when it comes back.

---

## What gets installed

| Thing | Detail |
| --- | --- |
| Program | `%ProgramFiles%\LabelBridge` (single-file exe, self-contained, no .NET needed) |
| Data | `%ProgramData%\LabelBridge` — settings, TLS cert, queues, history, logs |
| Virtual printer (client) | queue `LabelBridge`, TCP/IP port `LabelBridge_Loopback` → `127.0.0.1:9191`, driver `ZDesigner … ZPL` |
| Optional raw printer (client) | queue `LabelBridge Raw` using `Generic / Text Only`, for apps that already emit ZPL |
| Shortcuts | Start Menu + Desktop |
| Uninstall | "Apps & features" → LabelBridge (data is kept unless you delete it) |
| Firewall | inbound rule "LabelBridge direct TLS" for TCP 9443 (host/direct mode only) |

The installer never touches your real printer queue; it only adds the virtual one.

---

## Two transport modes

**Relay mode (recommended, works across the internet).**

Both sides connect *out* to your relay. Survives CGNAT, changing IPs, hotel Wi-Fi. Jobs are stored
on disk at the relay until the host acknowledges printing.

**Direct TLS mode (LAN, VPN, Tailscale).**

The host listens on port 9443 with a self-signed certificate; clients pin its SHA-256 fingerprint
from the pairing code and authenticate each job with HMAC-SHA256. No relay needed, but the client
must be able to reach the host (same LAN, VPN, or a port forward — not recommended on the open
internet).

You can enable both; the pairing code decides which one a client uses.

---

## Requirements

**Client PC / Host PC**

- Windows 10 or 11, x64
- Zebra **ZDesigner driver, ZPL variant**, matching the printer's model and DPI
  ([Zebra driver downloads](https://www.zebra.com/us/en/support-downloads/printer-software/printer-driver.html))
- Outbound HTTPS (443) for relay mode; outbound TCP 9100 to the printer on the host

**Relay**

- Anything that runs a container: TrueNAS SCALE, Docker, Podman, or a Linux LXC
- ~50 MB RAM, disk proportional to queue depth (a label is typically 1–20 KB)
- A TLS-terminating reverse proxy for internet use

---

## Build from source

```powershell
# Windows package + ZIP + optional linux-x64 relay binary
powershell -ExecutionPolicy Bypass -File installer\Build.ps1
```

Output:

```
dist\LabelBridge\                 app + installer scripts (copy this whole folder, or the ZIP)
dist\LabelBridge-1.0.0-win-x64.zip
dist\relay-linux-x64\             single-file relay for LXC/non-Docker hosts
```

Useful flags: `-SkipLinuxRelay`, `-SkipZip`, `-FrameworkDependent`, `-Version 1.2.3`.

Container image:

```bash
docker build -f docker/Dockerfile -t labelbridge/relay:1.0.0 .
docker compose -f docker/docker-compose.yml up -d
```

Regression tests (no hardware, no admin needed):

```powershell
src\LabelBridge.Windows\bin\Release\net8.0-windows\LabelBridge.exe --selftest
```

---

## Command line

```
LabelBridge                            open the GUI
LabelBridge --role host|client         open the GUI in a specific role
LabelBridge --send <file> --pair <code|@file>
                                      send a raw ZPL/EPL file with a pairing code
LabelBridge --printer-install [--driver <name>] [--port <n>] [--raw]
                                      create/repair the virtual printer (needs admin)
LabelBridge --printer-remove [--name <n>]
                                      delete the virtual printer
LabelBridge --printer-status           printers/drivers/ports as JSON
LabelBridge --pairing                  print this host's pairing code
LabelBridge --daemon [--role host]      run headless (no GUI)
LabelBridge --paths                    show data folders
LabelBridge --relay [--listen …] …      run the relay (Windows/Linux)
LabelBridge --selftest                 end-to-end self-test
```

Relay: `--listen http://+:8080/`, `--data <dir>`, `--admin <secret>`, `--max-jobs`, `--max-mb`,
`--retention-hours`, `--health-check <url>`. Environment equivalents: `LB_LISTEN`, `LB_DATA`,
`LB_ADMIN_SECRET`, `LB_MAX_JOBS`, `LB_MAX_MB`, `LB_RETENTION_HOURS`.

The relay HTTP API is documented in [`docs/protocol.md`](docs/protocol.md).

---

## Security

- **Tokens are stored hashed** at the relay (SHA-256). Each channel has a separate host token and
  client token, so a leaked client code cannot drain or delete the channel.
- **The pairing code is a secret.** It contains the relay URL, channel id and client token. Anyone
  who has it can print to your printer. Rotate by deleting the channel (host) and creating a new
  one — existing clients must re-pair.
- **The relay never learns the printer address**, and it cannot initiate connections to your LAN.
- **The relay does see label payloads.** That is unavoidable for a store-and-forward relay. Host it
  yourself (as here) and use HTTPS. For highly sensitive data use direct TLS over Tailscale/VPN
  instead.
- **Always use HTTPS** for a relay reachable from the internet. The relay itself speaks HTTP so a
  reverse proxy can own the certificate; see the deployment guide.
- Direct mode pins the self-signed certificate fingerprint, and each job is HMAC-signed. The
  certificate is created on first run (`%ProgramData%\LabelBridge\certs`).

---

## Limits and behaviour

- Max job size 64 MB at the relay (configurable with `--max-mb`); default 1000 queued jobs per
  channel; unclaimed jobs expire after 7 days (`--retention-hours`).
- The client queue and the host queue keep up to 2000/2000 jobs on disk and retry with backoff
  (1 s → 120 s).
- Jobs already printed are archived under `%ProgramData%\LabelBridge\history` (last 600, with
  payloads, so the Jobs tab can preview them).
- Updating the app never loses queued jobs — they are plain files in the data folder.
- The client must be running (or use **Start when I sign in**) for jobs to be captured. If
  LabelBridge is closed, Windows will keep the job in the spooler and print it once LabelBridge
  starts.

---

## Troubleshooting

Fast checks:

```powershell
LabelBridge.exe --paths           # where settings, queues and logs live
LabelBridge.exe --printer-status  # is the virtual printer installed? which driver?
LabelBridge.exe --selftest        # full relay/TLS/queue regression test
```

| Symptom | Likely cause / fix |
| --- | --- |
| `The virtual printer produced EMF instead of ZPL` | Wrong driver. Install/repair the printer and pick a `ZDesigner … ZPL` driver (not the "EPL"/"CPCL"/"v5" one). |
| Jobs stay "Queued" on a client | Host app not running, wrong pairing code, or relay unreachable. **Check connection** on the Connection tab. |
| `401` / "pairing rejected" | Channel was deleted or the code is stale. Create a new channel and re-pair. |
| Relay unreachable | Reverse proxy not forwarding, HTTPS certificate invalid, or client PC has no outbound 443. |
| Printer offline → jobs pending on host | Expected. Jobs print automatically when the printer answers again. |
| Print job hangs in the Windows queue on the client | LabelBridge is not running; start it (it captures on `127.0.0.1:9191`). |
| Port 9191 already in use | Change **Capture port** in the Printer tab, reinstall/repair the printer, apply. |
| Direct mode connection refused | Firewall (allow TCP 9443), wrong IP in the code, or client not on the same LAN/VPN. |
| Fingerprint mismatch error | Host certificate was recreated; build a fresh pairing code. |

Logs: `%ProgramData%\LabelBridge\logs\labelbridge-YYYYMMDD.log`, and the in-app **Log** tab.

More: [`docs/troubleshooting.md`](docs/troubleshooting.md).

---

## Repository layout

```
LabelBridge/
  src/LabelBridge.Core/       engine, transports, relay, storage (net8.0, no UI)
  src/LabelBridge.Windows/    WinForms GUI + printer installer + CLI (net8.0-windows)
  src/LabelBridge.Relay/      relay console host (Linux/Docker/Windows)
  installer/                  Build.ps1, Install.ps1, Uninstall.ps1, Setup.cmd
  docker/                     Dockerfile, docker-compose.yml, entrypoint
  docs/                       deployment, Windows setup, protocol, troubleshooting
  dist/                       build output (generated)
```

Third-party libraries: none. Only the .NET BCL and WinForms.

---

## Licence / disclaimer

Provided as-is. Test the complete path with a real label before relying on it in production,
especially after changing drivers, printer firmware, or the relay host.
