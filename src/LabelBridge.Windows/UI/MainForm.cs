using System.Diagnostics;
using System.Text.Json;
using LabelBridge.Core;
using LabelBridge.Services;

namespace LabelBridge.UI;

internal sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly ClientAgent _agent;
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };

    private Panel _header = null!; private Pill _pill = null!; private Label _subtitle = null!;
    private Button _btnStartStop = null!;
    private TabControl _tabs = null!;
    private TabPage _tabOverview = null!, _tabServer = null!, _tabPrinter = null!, _tabJobs = null!, _tabLog = null!;

    private StatCard _statConn = null!, _statPrinter = null!, _statQueue = null!, _statSent = null!;
    private Label _lblStatus = null!, _lblProfile = null!, _lblUpdate = null!;
    private TextBox _txtServerUrl = null!, _txtDevice = null!;
    private Button _btnRegister = null!, _btnRefresh = null!, _btnOpenServer = null!, _btnCheckUpdates = null!;
    private Label _lblReq = null!;

    private ComboBox _cmbDriver = null!; private NumericUpDown _numPort = null!; private TextBox _txtPrinterName = null!;
    private Button _btnInstallPrinter = null!, _btnRemovePrinter = null!, _btnProps = null!, _btnWinPrinters = null!, _btnDriverDownload = null!;

    private ListView _lvJobs = null!; private Button _btnSendFile = null!;
    private ListBox _log = null!;

    private ServerClientConfig? _lastConfig;
    private bool _allowClose;

    public MainForm(AppSettings settings)
    {
        _settings = settings;
        _agent = new ClientAgent(settings);
        _agent.StatusChanged += s => BeginInvokeSafe(() => { _lblStatus.Text = s; AppendLog(s); RefreshUi(); });
        _agent.JobEvent += (j, m) => BeginInvokeSafe(() => { AppendLog($"{m}: {j.Name} ({j.Size} bytes)"); RefreshUi(); });
        _agent.ConfigUpdated += cfg => BeginInvokeSafe(() => { _lastConfig = cfg; ApplyServerConfig(cfg); RefreshUi(); });
        _agent.ClientUpdateInfo += (msg, hasUpdate) => BeginInvokeSafe(() => HandleClientUpdateInfo(msg, hasUpdate));

        Text = "Online Printer Client";
        Icon = IconFactory.AppIcon(32);
        MinimumSize = new Size(980, 700);
        Size = new Size(1120, 760);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.Body;

        BuildUi();
        BuildTray();
        LoadFromSettings();
        RefreshUi();

        _timer.Tick += (_, _) => RefreshUi();
        _timer.Start();

        FormClosing += MainForm_FormClosing;
        Load += async (_, _) =>
        {
            try { AutoStart.Set(_settings.AutoStart); } catch { }
            _ = Task.Run(async () => { try { await _agent.CheckForUpdatesDetailedAsync(); } catch { } });
            if (_settings.AutoStartBridge) await StartAgentAsync();
        };
    }

    private void BuildUi()
    {
        _header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Theme.Card, Padding = new Padding(18, 14, 18, 10) };
        var title = new Label { Text = "Online Printer Client", Font = Theme.H1, AutoSize = true, Location = new Point(18, 10), ForeColor = Theme.Text };
        _subtitle = new Label { Text = "Send print jobs to the Online Printer Server", Font = Theme.Small, AutoSize = true, Location = new Point(20, 42), ForeColor = Theme.Muted };
        _pill = new Pill { Location = new Point(360, 18) }; _pill.Set("Stopped", Theme.GraySoft, Theme.Muted);
        _btnStartStop = Ui.Button("Start", primary: true); _btnStartStop.Location = new Point(760, 18); _btnStartStop.Anchor = AnchorStyles.Top | AnchorStyles.Right; _btnStartStop.Click += async (_, _) => await ToggleAgentAsync();
        _header.Controls.AddRange(new Control[] { title, _subtitle, _pill, _btnStartStop });
        _header.Resize += (_, _) => { _btnStartStop.Left = _header.ClientSize.Width - _btnStartStop.Width - 18; };
        Controls.Add(_header);

        _tabs = new TabControl { Dock = DockStyle.Fill, Font = Theme.Body };
        _tabOverview = new TabPage("Overview"); _tabServer = new TabPage("Server & Approval"); _tabPrinter = new TabPage("Virtual Printer"); _tabJobs = new TabPage("Jobs"); _tabLog = new TabPage("Log");
        foreach (var t in new[] { _tabOverview, _tabServer, _tabPrinter, _tabJobs, _tabLog }) { t.BackColor = Theme.Bg; _tabs.TabPages.Add(t); }
        Controls.Add(_tabs);
        Controls.SetChildIndex(_tabs, 0);

        BuildOverview(); BuildServerTab(); BuildPrinterTab(); BuildJobsTab(); BuildLogTab();
    }

    private void BuildOverview()
    {
        var stack = new CardsStack();
        var stats = new TableLayoutPanel { Dock = DockStyle.Top, Height = 110, ColumnCount = 4, RowCount = 1, Margin = new Padding(0,0,0,12) };
        for (int i=0;i<4;i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
        _statConn = new StatCard { Caption="Connection", Value="Not configured" };
        _statPrinter = new StatCard { Caption="Printer/Profile", Value="Unknown" };
        _statQueue = new StatCard { Caption="Queued jobs", Value="0" };
        _statSent = new StatCard { Caption="Jobs sent", Value="0" };
        stats.Controls.Add(_statConn,0,0); stats.Controls.Add(_statPrinter,1,0); stats.Controls.Add(_statQueue,2,0); stats.Controls.Add(_statSent,3,0);
        var card = new Card("Status");
        _lblStatus = Ui.Text("Stopped", muted:false, font: Theme.BodyMedium);
        _lblProfile = Ui.Wrap("No server profile loaded yet.");
        _lblUpdate = Ui.Wrap("Client update: checking...");
        _lblUpdate.ForeColor = Theme.Muted;
        var hint = Ui.Wrap("Flow: connect to the Online Printer Server -> request approval -> admin approves in web UI -> client downloads printer/profile/driver info -> install/repair the 'Online Printer' virtual printer -> print PDFs from any app to it; jobs upload to server and print directly on the network printer.");
        card.AddRow(_lblStatus);
        card.AddRow(_lblProfile);
        card.AddRow(_lblUpdate);
        card.AddRow(new Panel{Height=8});
        card.AddRow(hint);
        stack.Add(stats);
        stack.Add(card);
        _tabOverview.Controls.Add(stack);
    }

    private void BuildServerTab()
    {
        var stack = new CardsStack();
        var card = new Card("Server connection & approval");
        _txtServerUrl = Ui.Input(_settings.ServerUrl, 520);
        _txtDevice = Ui.Input(_settings.DeviceName, 260);
        _btnRegister = Ui.Button("Request / Register", primary:true); _btnRegister.Click += async (_,_) => await RegisterAsync();
        _btnRefresh = Ui.Button("Refresh status/config"); _btnRefresh.Click += async (_,_) => await RefreshServerAsync();
        _btnOpenServer = Ui.Button("Open server web UI"); _btnOpenServer.Click += (_,_) => OpenServer();
        _btnCheckUpdates = Ui.Button("Check for updates"); _btnCheckUpdates.Click += async (_,_) => await CheckUpdatesAsync();
        _lblReq = Ui.Wrap("Not registered.");
        card.AddRow(Ui.Field("Server URL", _txtServerUrl));
        card.AddRow(Ui.Field("Device name", _txtDevice));
        card.AddRow(Ui.Row(_btnRegister, _btnRefresh, _btnOpenServer, _btnCheckUpdates));
        card.AddRow(_lblReq);

        var card2 = new Card("Server-provided printer/profile & driver");
        var txt = Ui.Wrap("After approval, the server provides printer/profile settings and optional driver package metadata. Download/install the driver if provided (admin/UAC may be required), then install/repair the local 'Online Printer' virtual printer in the Virtual Printer tab.");
        card2.AddRow(txt);
        var btnDownload = Ui.Button("Download server driver"); btnDownload.Click += async (_,_) => await DownloadDriverAsync();
        var btnInstallDriver = Ui.Button("Run/install downloaded driver"); btnInstallDriver.Click += (_,_) => InstallDriver();
        card2.AddRow(Ui.Row(btnDownload, btnInstallDriver));
        stack.Add(card); stack.Add(card2);
        _tabServer.Controls.Add(stack);
    }

    private void BuildPrinterTab()
    {
        var stack = new CardsStack();
        var card = new Card("Local virtual printer");
        _txtPrinterName = Ui.Input(_settings.VirtualPrinterName, 260);
        _numPort = Ui.Number(_settings.CapturePort, 1024, 65535, 120);
        _cmbDriver = Ui.Combo(520); _cmbDriver.DropDownStyle = ComboBoxStyle.DropDown;
        _btnInstallPrinter = Ui.Button("Install / Repair printer", primary:true); _btnInstallPrinter.Click += (_,_) => InstallOrRepairPrinter();
        _btnRemovePrinter = Ui.Button("Remove printer"); _btnRemovePrinter.Click += (_,_) => RemovePrinter();
        _btnProps = Ui.Button("Printer properties"); _btnProps.Click += (_,_) => PrinterManager.OpenPrinterProperties(_txtPrinterName.Text.Trim());
        _btnWinPrinters = Ui.Button("Windows Printers"); _btnWinPrinters.Click += (_,_) => PrinterManager.OpenWindowsPrinters();
        _btnDriverDownload = Ui.Button("Zebra driver download"); _btnDriverDownload.Click += (_,_) => PrinterManager.OpenDriverDownload();
        card.AddRow(Ui.Wrap("Install a local virtual printer so any Windows app can print to 'Online Printer'. Jobs are captured and uploaded to the approved server, which forwards them to the configured network printer. Use the driver/profile provided by your server admin for your printer model and label format."));
        card.AddRow(Ui.Field("Printer name", _txtPrinterName));
        card.AddRow(Ui.Field("Capture port", _numPort));
        card.AddRow(Ui.Field("Driver", _cmbDriver));
        card.AddRow(Ui.Row(_btnInstallPrinter, _btnRemovePrinter, _btnProps, _btnWinPrinters, _btnDriverDownload));

        var card2 = new Card("Server label/profile (auto-applied where possible)");
        card2.AddRow(Ui.Wrap("The server pushes printer/profile settings to approved clients. Driver selection is applied automatically when a driver hint is provided; label size/DPI/options may need to be set in Printing Preferences/Printer Defaults depending on the driver."));
        var lbl = new Label { Name="lblSrvProfile", AutoSize=true, ForeColor=Theme.Text, Font=Theme.Body, MaximumSize=new Size(900,0) };
        lbl.Text = "No server profile yet.";
        card2.AddRow(lbl);

        stack.Add(card); stack.Add(card2);
        _tabPrinter.Controls.Add(stack);
    }

    private void BuildJobsTab()
    {
        var stack = new CardsStack();
        var card = new Card("Local queue / activity");
        _lvJobs = new ListView { View=View.Details, FullRowSelect=true, GridLines=true, Height=320, Dock=DockStyle.Top };
        _lvJobs.Columns.Add("Time", 140); _lvJobs.Columns.Add("Name", 320); _lvJobs.Columns.Add("Size", 90); _lvJobs.Columns.Add("Status", 280);
        _btnSendFile = Ui.Button("Send file to server now"); _btnSendFile.Click += (_,_) => SendFileDialog();
        var openDataFolder = Ui.Button("Open data folder", width:0, primary:false);
        openDataFolder.Click += (_,_) => { try { Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Root){UseShellExecute=true}); } catch{} };
        card.AddRow(Ui.Row(_btnSendFile, openDataFolder));
        card.AddRow(_lvJobs);
        card.AddRow(Ui.Wrap("Captured print jobs are queued locally and uploaded to the server when approved/connected. Server queues and prints directly to the configured network printer."));
        stack.Add(card);
        _tabJobs.Controls.Add(stack);
    }

    private void BuildLogTab()
    {
        var stack = new CardsStack();
        var card = new Card("Log");
        _log = new ListBox { Dock=DockStyle.Top, Height=420, Font=Theme.Mono, IntegralHeight=false };
        card.AddRow(_log);
        stack.Add(card);
        _tabLog.Controls.Add(stack);
    }

    private void BuildTray()
    {
        _tray.Icon = IconFactory.AppIcon(16);
        _tray.Text = "Online Printer Client";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_,_) => ShowFromTray());
        menu.Items.Add("Start/Stop", null, async (_,_) => await ToggleAgentAsync());
        menu.Items.Add("Exit", null, (_,_) => { _allowClose=true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_,_) => ShowFromTray();
    }

    private void LoadFromSettings()
    {
        _txtServerUrl.Text = _settings.ServerUrl;
        _txtDevice.Text = string.IsNullOrWhiteSpace(_settings.DeviceName) ? Environment.MachineName : _settings.DeviceName;
        _txtPrinterName.Text = _settings.VirtualPrinterName;
        _numPort.Value = Math.Clamp(_settings.CapturePort, 1024, 65535);
        RefreshDriverList();
        UpdateReqLabel();
        AppendLog("Ready. Configure server and request approval.");
    }

    private void RefreshDriverList()
    {
        try
        {
            var catalog = PrinterManager.LoadCatalog(force:false);
            _cmbDriver.Items.Clear();
            foreach (var d in catalog.Drivers.OrderBy(d=>d)) _cmbDriver.Items.Add(d);
            var pick = PrinterManager.PickDriver(catalog, _settings.VirtualPrinterDriver);
            if (!string.IsNullOrWhiteSpace(pick))
            {
                if (!_cmbDriver.Items.Contains(pick)) _cmbDriver.Items.Insert(0, pick);
                _cmbDriver.Text = pick;
            }
            else if (!string.IsNullOrWhiteSpace(_settings.VirtualPrinterDriver)) _cmbDriver.Text = _settings.VirtualPrinterDriver;
        }
        catch (Exception ex) { AppendLog("Driver list: " + ex.Message); }
    }

    private async Task StartAgentAsync()
    {
        PersistSettingsFromUi();
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl)) { AppendLog("Set server URL in Server & Approval tab."); }
        await _agent.StartAsync();
        _btnStartStop.Text = "Stop"; _pill.Set("Running", Theme.GreenSoft, Theme.Green);
        RefreshUi();
        if (!string.IsNullOrWhiteSpace(_settings.ClientToken)) await _agent.FetchConfigAsync();
    }

    private async Task StopAgentAsync()
    {
        await _agent.StopAsync();
        _btnStartStop.Text = "Start"; _pill.Set("Stopped", Theme.GraySoft, Theme.Muted);
        RefreshUi();
    }

    private async Task ToggleAgentAsync()
    {
        if (_agent.IsRunning) await StopAgentAsync(); else await StartAgentAsync();
    }

    private async Task RegisterAsync()
    {
        PersistSettingsFromUi();
        var (ok, msg) = await _agent.RegisterAsync();
        AppendLog(msg);
        Ui.Info(this, msg);
        UpdateReqLabel(); RefreshUi();
        if (ok && !_agent.IsRunning) await StartAgentAsync();
    }

    private async Task RefreshServerAsync()
    {
        PersistSettingsFromUi();
        var cfg = await _agent.FetchConfigAsync();
        if (cfg == null)
        {
            if (!string.IsNullOrWhiteSpace(_settings.ClientRequestId) && string.IsNullOrWhiteSpace(_settings.ClientToken))
                AppendLog("Not approved yet or token not claimed. Keep the client running to auto-refresh after approval.");
            else
                AppendLog("Could not fetch server config (check URL/token/approval). ");
        }
        else if (cfg.Ok)
        {
            _lastConfig = cfg; ApplyServerConfig(cfg); AppendLog($"Loaded server config. Printer: {cfg.PrinterName} {cfg.PrinterHost}:{cfg.PrinterPort}. Profile {cfg.Profile.Width}x{cfg.Profile.Height} {cfg.Profile.Dpi}dpi.");
        }
        else AppendLog("Server config error: " + (cfg.Error ?? "unknown"));
        UpdateReqLabel(); RefreshUi();
    }

    private async Task DownloadDriverAsync()
    {
        if (_lastConfig?.Driver == null) { Ui.Info(this, "No driver package info from server. Refresh server config first."); return; }
        var (ok,msg,_) = await _agent.DownloadDriverAsync(_lastConfig.Driver);
        AppendLog(msg); if (ok) Ui.Info(this, msg); else Ui.Error(this, msg);
    }

    private void InstallDriver()
    {
        var dir = Path.Combine(AppPaths.Root, "drivers");
        if (!Directory.Exists(dir)) { Ui.Info(this, "No downloaded drivers yet."); return; }
        using var dlg = new OpenFileDialog { InitialDirectory = dir, Filter = "Installers/packages|*.exe;*.msi;*.inf;*.zip;*.*|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var args = _lastConfig?.Driver?.SilentArgs;
        var (ok,msg) = _agent.InstallDownloadedDriver(dlg.FileName, args);
        AppendLog(msg); if (ok) Ui.Info(this, msg); else Ui.Error(this, msg);
    }

    private async Task CheckUpdatesAsync()
    {
        try
        {
            var (ok, msg, hasUpdate, _) = await _agent.CheckForUpdatesDetailedAsync();
            AppendLog(msg);
            if (!ok)
            {
                Ui.Error(this, msg);
                return;
            }

            if (hasUpdate)
            {
                var prompt = msg + "\n\nDownload and install the Windows client update now? (Admin/UAC may be required. Online Printer Client will close to complete the update.)\n\nServer updates can be applied from the server web UI (Settings) on Linux/systemd installs, or by running the server install script for the target release.";
                if (Ui.Confirm(this, prompt, "Update available"))
                {
                    AppendLog("Downloading and applying client update...");
                    var (uok, umsg) = await _agent.DownloadAndApplyClientUpdateAsync();
                    AppendLog(umsg);
                    if (!uok) Ui.Error(this, umsg);
                }
            }
            else
            {
                Ui.Info(this, msg + "\n\nServer updates can be applied from the server web UI (Settings) on Linux/systemd installs, or by running the server install script for the target release.");
            }
        }
        catch (Exception ex) { Ui.Error(this, ex.Message); }
    }

    private void ApplyServerConfig(ServerClientConfig cfg)
    {
        _lastConfig = cfg;

        var profileParts = new List<string>();
        void add(string label, string? v) { if (!string.IsNullOrWhiteSpace(v)) profileParts.Add($"{label}: {v}"); }
        add("Profile", cfg.Profile.Name);
        add("Make", cfg.Profile.Make);
        add("Model", cfg.Profile.Model);
        if (!string.IsNullOrWhiteSpace(cfg.Profile.DetectedUtc)) add("Detected", cfg.Profile.DetectedUtc);
        var size = string.Join(" x ", new[] { cfg.Profile.Width, cfg.Profile.Height }.Where(s => !string.IsNullOrWhiteSpace(s)));
        add("Size", size);
        add("DPI", cfg.Profile.Dpi);
        add("Orientation", cfg.Profile.Orientation);
        add("Media", cfg.Profile.MediaType);
        add("Method", cfg.Profile.PrintMethod);
        add("Darkness", cfg.Profile.Darkness);
        add("Speed", cfg.Profile.Speed);
        add("Language", cfg.Profile.PrintLanguage);
        add("Driver hint", cfg.Profile.DriverHint);

        var profileText = $"Server printer: {cfg.PrinterName} ({cfg.PrinterHost}:{cfg.PrinterPort}, {(cfg.PrinterEnabled ? "enabled" : "disabled")})\n" +
                          $"Server/profile: {string.Join(", ", profileParts.DefaultIfEmpty("not set"))}\n" +
                          (cfg.Driver != null
                              ? $"Driver package: {cfg.Driver.DisplayName} v{cfg.Driver.Version} ({cfg.Driver.Size:N0} bytes, id={cfg.Driver.Id})"
                              : "Driver package: (none selected on server)");

        _lblProfile.Text = profileText;
        var lbl = _tabPrinter.Controls.Find("lblSrvProfile", true).FirstOrDefault() as Label;
        if (lbl != null) lbl.Text = profileText + "\n\nNote: configure Printing Preferences/Defaults for the local 'Online Printer' printer to match this profile if your driver requires it.";

        if (!string.IsNullOrWhiteSpace(cfg.Profile.DriverHint))
        {
            var hint = cfg.Profile.DriverHint.Trim();
            if (!_cmbDriver.Items.Contains(hint)) _cmbDriver.Items.Add(hint);
            _cmbDriver.Text = hint; // auto-apply server driver hint selection
            _settings.VirtualPrinterDriver = hint;
        }

        if (cfg.Driver != null) _settings.LastServerDriverId = cfg.Driver.Id;
        _settings.Save();
        UpdateReqLabel();
    }

    private void PersistSettingsFromUi()
    {
        _settings.ServerUrl = (_txtServerUrl.Text ?? "").Trim().TrimEnd('/');
        _settings.DeviceName = string.IsNullOrWhiteSpace(_txtDevice.Text) ? Environment.MachineName : _txtDevice.Text.Trim();
        _settings.VirtualPrinterName = string.IsNullOrWhiteSpace(_txtPrinterName.Text) ? "Online Printer" : _txtPrinterName.Text.Trim();
        _settings.CapturePort = (int)_numPort.Value;
        if (!string.IsNullOrWhiteSpace(_cmbDriver.Text)) _settings.VirtualPrinterDriver = _cmbDriver.Text.Trim();
        _settings.Save();
    }

    private ServerPrinterProfile cfgProfile() =>
        _lastConfig?.Profile ?? new ServerPrinterProfile();

    private void InstallOrRepairPrinter()
    {
        PersistSettingsFromUi();
        try
        {
            var p = cfgProfile();
            var res = PrinterManager.Install(_settings, includeRawPrinter:false, driverOverride: _cmbDriver.Text,
                make: p.Make, model: p.Model, language: p.PrintLanguage, allowAnyDriver: true);
            AppendLog(res.Output);
            if (res.Ok) Ui.Info(this, res.Output.Length>0?res.Output:"Printer installed."); else Ui.Error(this, res.Output.Length>0?res.Output:"Printer install failed.");
            RefreshDriverList();
        }
        catch (Exception ex) { Ui.Error(this, ex.Message); }
    }

    private void RemovePrinter()
    {
        PersistSettingsFromUi();
        var res = PrinterManager.Remove(_settings.VirtualPrinterName);
        AppendLog(res.Output);
        if (res.Ok) Ui.Info(this, res.Output); else Ui.Error(this, res.Output);
    }

    private void OpenServer()
    {
        var url = (_txtServerUrl.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) { Ui.Info(this, "Set server URL first."); return; }
        try { Process.Start(new ProcessStartInfo(url){UseShellExecute=true}); } catch (Exception ex){ Ui.Error(this, ex.Message); }
    }

    private void SendFileDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "Printable files|*.pdf;*.zpl;*.prn;*.txt;*.*|All files|*.*" };
        if (dlg.ShowDialog(this)!=DialogResult.OK) return;
        var (ok,msg) = _agent.QueueFile(dlg.FileName);
        AppendLog(msg); RefreshUi(); if(!ok) Ui.Error(this,msg);
    }

    private void RefreshUi()
    {
        try
        {
            _statQueue.Set(_agent.PendingCount.ToString());
            _statSent.Set(_settings.LifetimeJobsSent.ToString());
            var connected = !string.IsNullOrWhiteSpace(_settings.ServerUrl);
            var approved = string.Equals(_settings.ClientStatus, "Approved", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_settings.ClientToken);
            _statConn.Set(approved ? "Approved" : (string.IsNullOrWhiteSpace(_settings.ClientRequestId) ? "Not registered" : (_settings.ClientStatus ?? "Pending")));
            _statPrinter.Set(string.IsNullOrWhiteSpace(_settings.LastServerPrinterName) ? "No profile" : _settings.LastServerPrinterName);
            if (_agent.IsRunning) _pill.Set(approved?"Running (approved)":(_settings.ClientToken=="" && !string.IsNullOrWhiteSpace(_settings.ClientRequestId)?"Running (pending approval)":"Running"), approved?Theme.GreenSoft:Theme.AmberSoft, approved?Theme.Green:Theme.Amber);
            UpdateReqLabel();
            UpdateUpdateLabel();
        }
        catch { }
    }

    private bool _clientUpdatePromptShown;

    private void HandleClientUpdateInfo(string message, bool hasUpdate)
    {
        try
        {
            UpdateUpdateLabel(message, hasUpdate);
            if (hasUpdate && !_clientUpdatePromptShown)
            {
                _clientUpdatePromptShown = true;
                _tray.ShowBalloonTip(5000, "Online Printer Client", "A new client update is available. Open Settings/Server & Approval and click 'Check for updates' to install it.", ToolTipIcon.Info);
            }
        }
        catch { }
    }

    private void UpdateUpdateLabel(string? message = null, bool? hasUpdate = null)
    {
        try
        {
            if (_lblUpdate == null) return;
            var has = hasUpdate ?? _agent.ClientUpdateAvailable;
            var msg = message;
            if (string.IsNullOrWhiteSpace(msg))
            {
                var latest = _agent.LatestVersion;
                msg = string.IsNullOrWhiteSpace(latest)
                    ? $"Client v{ClientAgent.AppVersion} (update not checked yet)"
                    : has
                        ? $"Client v{ClientAgent.AppVersion} · Update available: {latest}"
                        : $"Client v{ClientAgent.AppVersion} · Up to date ({latest})";
            }
            else if (!msg.StartsWith("Client", StringComparison.OrdinalIgnoreCase))
            {
                msg = $"Client v{ClientAgent.AppVersion}: {msg}";
            }

            _lblUpdate.Text = msg;
            _lblUpdate.ForeColor = has ? Theme.Amber : (msg.Contains("failed", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(200, 80, 80) : Theme.Muted);
        }
        catch { }
    }

    private void UpdateReqLabel()
    {
        var s = $"Server: {(_settings.ServerUrl==""?"(not set)":_settings.ServerUrl)}\nRequest ID: {(_settings.ClientRequestId==""?"(none)":_settings.ClientRequestId)}\nStatus: {(_settings.ClientStatus==""?"(unknown)":_settings.ClientStatus)}\nToken: {(string.IsNullOrWhiteSpace(_settings.ClientToken)?"(not claimed)":"present")}";
        if (_lblReq != null) _lblReq.Text = s;
    }

    private void AppendLog(string line)
    {
        try
        {
            if (_log == null || _log.IsDisposed) return;
            _log.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_log.Items.Count > 500) _log.Items.RemoveAt(_log.Items.Count-1);
        }
        catch { }
    }

    private void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide(); _tray.ShowBalloonTip(1500, "Online Printer Client", "Still running in the system tray.", ToolTipIcon.Info); return;
        }
        _timer.Stop(); _tray.Visible = false;
        PersistSettingsFromUi();
        try { await _agent.StopAsync(); } catch { }
    }

    private void BeginInvokeSafe(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { } }
}
