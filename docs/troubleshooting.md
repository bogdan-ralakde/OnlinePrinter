# Troubleshooting

## Fast checks

**Online Printer Client (Windows)**

```
OnlinePrinter.Client.exe --paths
OnlinePrinter.Client.exe --printer-status
OnlinePrinter.Client.exe --selftest
```

- `--selftest` validates local/client-server core logic (format policy, durable jobs, server store/approval/token claim, queue policy, test-label generation). It does **not** validate a real printer, Windows driver install, web UI flow, or network reachability.
- Logs: `%ProgramData%\LabelBridge\logs\` and the app **Log** tab. (Data folder name is kept for upgrade compatibility.)

**Online Printer Server (Linux/systemd)**

```bash
systemctl status labelbridge-relay
journalctl -u labelbridge-relay -n 200 --no-pager
curl -s http://127.0.0.1:8080/health
```

- Web UI: `http://<server>:8080/` (or your HTTPS reverse-proxy URL)
- Config: `/etc/labelbridge/relay.env`
- Data/state/queue/drivers/jobs: `/var/lib/labelbridge` (default)

## Admin access and first-run setup

| Symptom | What to check |
| --- | --- |
| First run asks for password in web UI | Expected when no admin password is configured: the web UI shows **Initial setup** to create the admin password (username `admin`). |
| Need to preconfigure password (automation/scripted install) | Set `LB_ADMIN_SECRET` before first start or provide it when running `scripts/install-linux.sh` (interactive prompt or env). It is used as the initial admin password if no admin account exists yet. |
| Forgot admin password | Stop the server and reset admin state in the data dir (default `/var/lib/labelbridge`, e.g. backup/remove `server.json` if you can lose stored state) or set `LB_ADMIN_SECRET` for a fresh data dir, then start again and complete setup/login. After login, change password in **Settings**. |

## Windows client installation

| Symptom | What to check |
| --- | --- |
| No installer scripts | The client installs from a single `OnlinePrinterClient-Setup.exe`. Run it (UAC prompt is expected); no PowerShell/CMD is involved. |
| Want to uninstall | Use **Apps & features / Add or remove programs -> Online Printer Client**, or run the installed `OnlinePrinterClient-Setup.exe --uninstall`. |
| Setup reports missing payload | Re-download `OnlinePrinterClient-Setup.exe` from the release; the file must be the full published EXE. |

## Client registration and approval

| Symptom | What to check |
| --- | --- |
| Cannot connect to server | Verify server URL in client, server is running, firewall/routing allows access, and HTTPS proxy/cert is valid if used. `GET /health` should return ok. |
| Request stays Pending | Approve the client in server web UI (**Clients**). Keep the client running so it can poll/claim the token after approval. |
| Unauthorized / revoked / token invalid | Client was declined/revoked or token no longer valid. Request/register again and have admin approve. |
| Client shows no server profile/driver | Client must be approved and token claimed. Use **Refresh status/config** in the client; confirm printer/driver are configured in server web UI. |

## Virtual printer and capture (Windows)

| Symptom | Likely cause / fix |
| --- | --- |
| `Online Printer` printer missing | **Virtual Printer -> Install / Repair printer** (admin). Check selected driver exists; install vendor driver first if needed. |
| Port/capture conflict (e.g. 9191 in use) | Change **Capture port**, then install/repair printer and restart client as needed. `--printer-status` shows port state. |
| Driver install needed | Download/install the server-provided driver package if available (may require admin/UAC). Silent install depends on package type/args; manual install may be required. |
| Jobs not captured | Client must be running, and the app must print to the local `Online Printer` printer (correct queue). |
| Wrong label size/orientation/DPI | Set/verify driver **Printing Preferences** and **Printer Defaults** for the `Online Printer` queue to match the server/profile. Auto-apply of all driver-specific options is best-effort. |

## Job format and printing

Online Printer Client forwards captured bytes to the server; the server sends raw bytes to the network printer over TCP/9100. **No format conversion is bundled.**

| Symptom | Meaning / fix |
| --- | --- |
| `The virtual printer produced EMF/XPS...` / rejected job | Windows rendered a GDI/EMF or XPS spool instead of printer-native data. Install/select a driver that outputs a format your printer supports (e.g. ZPL for a Zebra ZPL printer), and configure the app/driver for raw/printer-language output where applicable. |
| Empty job rejected | Capture produced no data. Check driver/port/printer configuration and retry. |
| PDF/PostScript job sent but printer prints nothing/garbage | Printer may not natively support that format. Use a printer-native driver/profile (e.g. ZPL/EPL) or a printer that supports the format. |
| Server job failed: cannot connect to printer / timeout | Verify printer host/port in web UI (**Printer -> Test connection**), printer power/network/VLAN, and that server can reach printer TCP/9100. Jobs are retried according to queue behavior. |
| Jobs queued but not printing | Ensure printer is enabled on server, printer is reachable, and jobs are in an accepted format. Check **Jobs** tab/logs on server web UI and server logs. |

## Updates

- **Server update info/apply:** web UI **Settings -> Server update**. Apply is best-effort on Linux/systemd (download latest release asset, verify checksum when available, replace binary if writable, request restart). If permissions or environment prevent in-place apply, run the server install script manually for the target release.
- **Windows client update:** client **Check for updates** downloads the release package, verifies checksum when available, and launches the elevated `OnlinePrinterClient-Setup.exe` directly (no PowerShell/CMD); manual install from the latest GitHub release is the fallback.
- Update apply depends on release assets/checksums being published and network/GitHub access.

## Server install/operation (Linux/systemd)

- Service: `labelbridge-relay` (name kept for compatibility)
- Binary: `/opt/labelbridge/LabelBridge.Relay`
- Config: `/etc/labelbridge/relay.env`
- Data: `/var/lib/labelbridge`
- Install/upgrade: `scripts/install-linux.sh` (auto-downloads `labelbridge-relay-linux-x64.tar.gz` from public releases)
- Expose over internet behind HTTPS reverse proxy; set strong admin credentials and approve only trusted clients.

## When reporting issues

Include:
- **Client:** `OnlinePrinter.Client.exe --version`, `--paths`, `--printer-status`, `--selftest` output, recent client log lines, server URL (redact secrets/tokens), request/approval status, and job format/error shown.
- **Server:** `/health` output, `journalctl -u labelbridge-relay` excerpts, printer config (host/port, redact sensitive info), job status/error, and release/version info.
- Do not share client tokens, admin password, or sensitive label payloads.

**Note:** Reinstall/upgrade preserves existing config (`/etc/labelbridge/relay.env`) and data (`/var/lib/labelbridge`). If an admin password is already configured, the web UI will ask you to log in, not to create a new password. The first-run 'create admin password' screen appears only when no admin password is configured (fresh data/state, or initial setup with no pre-set `LB_ADMIN_SECRET`). To force first-run setup again on a fresh state, stop the service and reset server data (e.g. backup/remove `server.json` in the data dir if you can lose stored state) or reinstall with `--purge`, then start again.
