# Troubleshooting

Start here:

```powershell
LabelBridge.exe --paths            # data folders (settings, queues, logs, certs)
LabelBridge.exe --printer-status   # installed virtual printer? drivers? port free?
LabelBridge.exe --selftest         # relay + TLS + queue regression test, no hardware needed
```

Logs: `%ProgramData%\LabelBridge\logs\labelbridge-YYYYMMDD.log` and the app's **Log** tab
(NOT the Windows Event Log).

| I see | Meaning |
| --- | --- |
| `PASS relay end-to-end` | relay protocol, file queue and HTTP transport work |
| `PASS direct TLS end-to-end` | certificate pinning + HMAC envelope work |
| `PASS durable queue` | offline printer → queued → delivered after recovery works |

If `--selftest` fails, the problem is in the app/runtime, not in your printer or network.

---

## Printing / driver

### "The virtual printer produced EMF instead of ZPL"

The virtual printer is not using a ZPL renderer. Fix:

1. **Printer → Install / repair printer**, choose a driver named `ZDesigner … ZPL`.
2. If the list is empty, install the Zebra ZDesigner driver first
   (**Get the Zebra ZDesigner driver** button), then repair.
3. Avoid `… (EPL)`, `… (CPCL)`, `ZDesigner … v5` and `Generic / Text Only` — the last one sends
   text, not rendered labels.

EMF means Windows handed over a rendered page image, which a label printer cannot print.

### "The virtual printer is not installed"

Run **Printer → Install / repair printer** and approve the UAC prompt. The app logs the exact
PowerShell error in the Log tab (driver missing, spooler stopped, port in use).

`--printer-status` shows `installed: false` plus the port state as JSON if you prefer the CLI.

### Port 9191 already in use

`portFree: false` in `--printer-status`, or the app warns when installing. Something else (often
another printer monitor) owns the port.

1. **Printer** tab → *Capture port* = e.g. 9292.
2. **Install / repair printer** (recreates the port) → **Apply & restart**.

### Jobs stay "Queued" on the client

Delivered? No → transport problem; delivered but no label → printer problem. Check:

- Is the bridge **Running** (Overview shows *Running*, tray icon present)?
- **Connection → Test connection**: "Relay … answered" means the relay is fine; otherwise DNS/TLS
  /firewall/proxy.
- Job status *Retrying* with a detail message — read it; the same text is in the log.
- `401` / "pairing rejected": the channel was deleted or the code is stale. Create a new channel
  and re-pair.
- Behind a corporate proxy? LabelBridge uses the system proxy for HTTPS. If the proxy blocks
  long-lived requests, the poll fails every time — allow `print.example.com` or use direct mode
  over VPN.

### Jobs arrive at the host but nothing prints

- **Check printer** on the host: `Connected to 192.168.50.25:9100` expected.
- Printer powered on, same VLAN, static IP still correct (`ping`, then
  `Test-NetConnection 192.168.50.25 -Port 9100`).
- Host job status *Retrying* with `timed out` → printer unreachable or busy.
- Using a Windows queue? Verify the queue prints a Windows test page outside LabelBridge.
- Wrong label size/orientation is a driver setting, not a bridge problem (verify in
  *Printer properties → Preferences* on the client).

### Labels print but content is cut off / rotated / wrong DPI

The label format must be defined before LabelBridge sees it:

- Set the correct label size in the application / driver preferences (e.g. 4×6 in at 300 dpi).
- The ZDesigner driver settings are per printer queue: *Printer properties → Preferences →
  Stock*.
- A test label from LabelBridge uses 1200×1800 dots (4×6 in at 300 dpi); if your labels are
  smaller, that sample will be clipped. Use your own document for a real check.

---

## Relay / network

### Relay not reachable

```bash
curl -v https://print.example.com/health
```

- `connection refused` → container down or port not published.
- TLS error → certificate/hostname mismatch at the proxy.
- `404` on `/health` → proxy points at the wrong upstream/path.
- Timeout from a remote country → local firewall/ISP blocking, or the proxy only listens on the
  LAN interface.

Check the proxy specifically for long-poll friendliness: allow >30 s idle requests, and disable
response buffering for `/api/` (see `docs/relay-truenas.md`).

### `401 admin secret required` when creating a channel

The relay was started with `LB_ADMIN_SECRET` but the app's **Admin secret** field is empty or
different. Copy it exactly from the relay's environment (`.env` / compose / systemd unit).
If the relay has no admin secret, anyone who can reach it can create channels — set one.

### `507` queue full

The host is not collecting jobs (`pending` keeps growing). Start the host app, check its relay
settings, or raise `LB_MAX_JOBS`. Old unclaimed jobs expire after `LB_RETENTION_HOURS`.

### Relay restarted / power loss

Safe. Waiting jobs are on disk; claimed-but-unacked jobs are returned to the queue at startup.

### Host shows "Relay unreachable … retrying"

Expected during outages; the backoff caps at 60 s. Labels queue on the client in the meantime.
If it never recovers, see *Relay not reachable*.

---

## Direct TLS mode

| Symptom | Fix |
| --- | --- |
| `connection refused` | Host app not running, direct listener disabled, or firewall. Allow TCP 9443 (installer adds a Domain/Private rule). |
| Timeout | Client and host are not on the same network/VPN. Direct mode needs reachability; use the relay otherwise. |
| `fingerprint does not match` | Host's certificate was recreated (`certs\labelbridge-host.pfx` deleted or expired) → build a fresh pairing code. |
| Works on LAN, not from home | Direct mode requires a port forward or VPN. Prefer relay mode or Tailscale. |

Switching modes: a pairing code carries its mode. To move a client from direct to relay, send it a
new relay code; nothing else changes.

---

## Windows / installer

### SmartScreen blocks the installer or exe

Unsigned build. *More info → Run anyway*. For distribution, sign `LabelBridge.exe` (and the ZIP)
with an Authenticode certificate; then SmartScreen and most AV products stay quiet.

### Setup says "must run as administrator"

Right-click `Setup.cmd` → **Run as administrator** (it also self-elevates when double-clicked, but
some environments block the elevation helper).

### Setup succeeded but printing users get "Access denied"

`%ProgramData%\LabelBridge` permissions. Re-run `Setup.cmd` (it grants Modify to
`BUILTIN\Users`), or:

```powershell
icacls "$env:ProgramData\LabelBridge" /grant '*S-1-5-32-545:(OI)(CI)M' /T
```

### Uninstall left files behind

The spooler or the app still had a handle. Reboot and delete `%ProgramFiles%\LabelBridge`
manually. Removing the virtual printer afterwards: **Settings → Bluetooth & devices → Printers &
scanners**.

### Antivirus flags the self-extracting exe

Single-file bundles extract native libraries at startup. Whitelist
`%ProgramFiles%\LabelBridge`, or build with `-FrameworkDependent` (smaller, no extraction, requires
the .NET 8 Desktop Runtime).

### The app starts but the window is missing

It may have started minimised to the notification area. Double-click the LabelBridge tray icon.
Remove `%ProgramData%\LabelBridge\settings.json` to reset *Start minimised* / *Minimize to tray*.

---

## Diagnostics to attach to a bug report

1. `LabelBridge.exe --version`, `--paths`, `--printer-status` output
2. `LabelBridge.exe --selftest` output
3. The last ~200 lines of the log for each PC
4. Relay container log around a test print
5. Whether the job shows up at the relay (`/` status page counters) and at the host (Jobs tab)

Queue payloads are plain ZPL — the Jobs tab preview is safe to share after checking it does not
contain customer data.
