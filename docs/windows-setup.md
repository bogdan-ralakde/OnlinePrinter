# Windows setup

Two roles, one installer.

| Role | Where | What it does |
| --- | --- | --- |
| **Host** | PC on the same network as the label printer | Receives jobs from the relay and prints them on the real printer |
| **Client** | any PC that needs to print labels | Provides a normal Windows printer and forwards jobs to the host |

The same installer is used everywhere; the role is chosen in the app on first run.

---

## 1. Prerequisites

### Zebra driver (both PCs, ZPL variant)

LabelBridge relies on the Zebra driver to turn a page (text, barcodes, images) into ZPL. It does
**not** include drivers.

1. Download the ZDesigner driver for the exact printer model:
   <https://www.zebra.com/us/en/support-downloads/printer-software/printer-driver.html>
2. Run the installer and add a printer, choosing the **ZPL** driver, e.g.
   `ZDesigner ZD420-300dpi ZPL`. Match the printer's DPI (203 / 300 / 600).
3. On the host PC it is enough that the driver is *installed* (a printer queue is handy for
   testing). On a client PC the driver is needed for the virtual printer.

> The **EPL**, **CPCL**, **v5** and **"ZDesigner … (EPL)"** variants will not work. If the app
> reports `EMF` instead of ZPL, the wrong driver is selected.

### Windows and permissions

- Windows 10/11 x64.
- Setup needs an administrator once (printer + Program Files). Day-to-day use does not.
- Printing users need write access to `%ProgramData%\LabelBridge`; the installer grants it to
  `BUILTIN\Users`. If you skip the installer and copy files manually, fix that yourself.

---

## 2. Install the host (printer PC)

1. Run **`Setup.cmd`** from the package folder → answer **n** to "Install the virtual printer"
   (the host does not need it; you can still install it later if you want to print locally).
2. Open LabelBridge and choose **Host**.
3. **Printer** tab:
   - *Printer address*: the printer's IP, e.g. `192.168.50.25`, *TCP port* `9100`.
   - **Check printer** — expects `Connected to 192.168.50.25:9100`.
   - **Send test label** — a label with a timestamp and barcode is printed. If this fails, fix the
     network/printer first; nothing else can work until it does.
   - Alternatively tick **Re-spool to an installed Windows print queue** and pick e.g.
     `Label Printer` — useful when the queue has special settings (speed/darkness) or the printer
     is shared through a print server.
4. **Connection** tab:
   - *Relay URL*: your relay, e.g. `https://print.example.com`
   - *Admin secret*: the value from the relay's `LB_ADMIN_SECRET`
   - **Test relay**, then **Create channel**.
   - Leave **Direct TLS** enabled if clients might be on the same LAN/VPN (port 9443; the installer
     added the firewall rule).
5. **Pairing** tab → **Copy code**.
6. Optional: **Overview** → tick *Start LabelBridge when I sign in* and *Start the bridge
   automatically*. On a dedicated print PC this is what you want.

The host stores what it has accepted on disk before printing, so closing the app or losing the
network does not lose labels.

---

## 3. Install a client (remote PC)

1. Run **`Setup.cmd`** → answer **Y** to "Install the virtual printer".
   This creates:
   - TCP/IP port `LabelBridge_Loopback` → `127.0.0.1:9191`
   - printer **LabelBridge** using the best installed `ZDesigner … ZPL` driver
   - optionally **LabelBridge Raw** (`Generic / Text Only`) if that driver is present
2. Open LabelBridge and choose **Client**.
3. **Pairing** tab → paste the code from the host → **Use this code**.
4. **Connection** tab → **Test connection** (expects "Relay … answered: ok …").
5. **Printer** tab → confirm *Installed: LabelBridge (driver: ZDesigner … ZPL)*.
6. Print something and choose the printer **LabelBridge**. A Windows test page also works:
   *Printer* tab → **Windows test page**.

### Printing from an application

- Choose the printer **LabelBridge** like any other Windows printer; set paper size/label format in
  the application or in *Printer properties*.
- The driver renders the page to ZPL; LabelBridge captures the byte stream — it does **not** try to
  interpret your document.
- If your application already sends raw ZPL (e.g. a warehouse app), install the raw printer too and
  print to **LabelBridge Raw**. That path passes bytes through unchanged.

### Multiple printers / multiple hosts

One host instance = one printer = one relay channel = one pairing code, and a client can be paired
with one host at a time. For two label printers, run two host PCs (a small VM is enough), each with
its own channel and code. To switch a client between printers, paste the other pairing code.

---

## 4. Verifying the whole path

**Host**

- **Printer** tab → *Check printer* + *Send test label* → label comes out.
- **Connection** tab → relay state shows polling or direct listening.

**Client**

- **Connection** tab → *Test connection* → relay answers.
- Print a test page; **Jobs** tab shows the job as *Delivered* within a few seconds.
- Select the job to see the captured ZPL (text) or bytes (hex). This is exactly what the printer
  receives — invaluable when a label looks wrong.

**Relay**

- `docker logs -f labelbridge-relay` prints `Relay accepted job …` and `Relay channel … created`.
- `https://print.example.com/` shows counters.

---

## 5. Updating

- Run the new `Setup.cmd`. It stops the app, replaces files, keeps settings and queued jobs.
- The virtual printer and port are reused; if the capture port changed in the app, run
  **Printer → Install / repair printer** afterwards.

## 6. Uninstalling

- **Settings → Apps → Installed apps → LabelBridge → Uninstall**
  (or run `Uninstall.cmd` from `%ProgramFiles%\LabelBridge`).
- This removes the virtual printer, the firewall rule, shortcuts and program files.
- `%ProgramData%\LabelBridge` (settings, logs, history, any queued jobs) is kept so you can
  reinstall later. Delete that folder to start completely fresh.

## 7. Service-like operation

There is no Windows service: the virtual printer needs a user session, and the host needs the
printer queue. Recommended:

- dedicated local account (or the print operator's account) with *auto sign-in* disabled,
  but *Start LabelBridge when I sign in* enabled;
- set the PC to never sleep;
- optional: *Task Scheduler* → "At log on" → `"C:\Program Files\LabelBridge\LabelBridge.exe" --tray`
  if you do not want the Run-key autostart.

For a headless relay/host, use `LabelBridge.exe --daemon --role host` in a scheduled task or as a
service wrapper (NSSM); the GUI and daemon share the same settings and queues.
