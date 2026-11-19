# Troubleshooting

## Fast checks

**Windows client**

```powershell
LabelBridge.exe --paths
LabelBridge.exe --printer-status
LabelBridge.exe --selftest
```

- `--selftest` validates local/client-server core logic (format policy, durable jobs, server store/approval/token claim, queue policy, test-label generation). It does **not** validate a real printer, Windows driver install, web UI flow, or network reachability.
- Logs: `%ProgramData%\LabelBridge\logs\labelbridge-YYYYMMDD.log` and the app **Log** tab.

**Server (Linux/systemd)**

```bash
systemctl status labelbridge-relay
journalctl -u labelbridge-relay -n 200 --no-pager
curl -s http://127.0.0.1:8080/health
```

- Web UI: `http://<server>:8080/` (or your HTTPS reverse-proxy URL)
- Config: `/etc/labelbridge/relay.env`
- Data/state/queue/drivers/jobs: `/var/lib/labelbridge` (default)

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
| `LabelBridge` printer missing | **Virtual Printer → Install / Repair printer** (admin). Check selected driver exists; install vendor driver first if needed. |
| Port/capture conflict (e.g. 9191 in use) | Change **Capture port**, then install/repair printer and restart client as needed. `--printer-status` shows port state. |
| Driver install needed | Download/install the server-provided driver package if available (may require admin/UAC). Silent install depends on package type/args; manual install may be required. |
| Jobs not captured | Client must be running, and the app must print to the local `LabelBridge` printer (correct queue). |
| Wrong label size/orientation/DPI | Set/verify driver **Printing Preferences** and **Printer Defaults** for the `LabelBridge` queue to match the server/profile. Auto-apply of all driver-specific options is best-effort. |

## Job format and printing

LabelBridge forwards captured bytes to the server; the server sends raw bytes to the network printer over TCP/9100. **No format conversion is bundled.**

| Symptom | Meaning / fix |
| --- | --- |
| `The virtual printer produced EMF/XPS...` / rejected job | Windows rendered a GDI/EMF or XPS spool instead of printer-native data. Install/select a driver that outputs a format your printer supports (e.g. ZPL for a Zebra ZPL printer), and configure the app/driver for raw/printer-language output where applicable. |
| Empty job rejected | Capture produced no data. Check driver/port/printer configuration and retry. |
| PDF/PostScript job sent but printer prints nothing/garbage | Printer may not natively support that format. Use a printer-native driver/profile (e.g. ZPL/EPL) or a printer that supports the format. |
| Server job failed: cannot connect to printer / timeout | Verify printer host/port in web UI (**Printer → Test connection**), printer power/network/VLAN, and that server can reach printer TCP/9100. Jobs are retried according to queue behavior. |
| Jobs queued but not printing | Ensure printer is enabled on server, printer is reachable, and jobs are in an accepted format. Check **Jobs** tab/logs on server web UI and server logs. |

## Updates

- **Server update info/apply:** web UI **Settings → Server update**. Apply is best-effort on Linux/systemd (download latest release asset, verify checksum when available, replace binary if writable, request restart). If permissions or environment prevent in-place apply, run the manual LXC install command for the target release.
- **Windows client update:** client **Check for updates** (and prompt on available update) downloads a Windows release asset, verifies checksum when available, stages an elevated updater, and exits to complete install. Manual fallback: download/install from the latest GitHub release.
- Update apply depends on release assets/checksums being published and network/GitHub access.

## Server install/operation (LXC/systemd)

- Service: `labelbridge-relay` (name kept for compatibility)
- Binary: `/opt/labelbridge/LabelBridge.Relay`
- Config: `/etc/labelbridge/relay.env`
- Data: `/var/lib/labelbridge`
- Install/upgrade: `scripts/install-lxc.sh` (auto-downloads `labelbridge-relay-linux-x64.tar.gz` from public releases)
- Expose over internet behind HTTPS reverse proxy; set strong admin credentials and approve only trusted clients.

## When reporting issues

Include:
- **Client:** `LabelBridge.exe --version`, `--paths`, `--printer-status`, `--selftest` output, recent client log lines, server URL (redact secrets/tokens), request/approval status, and job format/error shown.
- **Server:** `/health` output, `journalctl -u labelbridge-relay` excerpts, printer config (host/port, redact sensitive info), job status/error, and release/version info.
- Do not share client tokens, admin password, or sensitive label payloads.
