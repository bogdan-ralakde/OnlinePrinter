using System.Diagnostics;
using LabelBridge.Core;
using LabelBridge.Services;

namespace LabelBridge.UI;

internal sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private BridgeEngine? _engine;
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private Panel _header = null!;
    private Pill _pill = null!;
    private Label _subtitle = null!;
    private Button _btnStartStop = null!;
    private Button _btnApply = null!;
    private TabControl _tabs = null!;
    private TabPage _tabOverview = null!, _tabPrinter = null!, _tabConnection = null!,
                     _tabPairing = null!, _tabJobs = null!, _tabLog = null!;

    // overview
    private StatCard _statMode = null!, _statState = null!, _statQueue = null!, _statDelivered = null!;
    private RadioButton _rbHost = null!, _rbClient = null!;
    private Label _lblStatusDetail = null!, _lblHow = null!;
    private Button _btnTestLabel = null!, _btnWindowsTest = null!, _btnImport = null!,
                   _btnCheck = null!, _btnOpenData = null!;
    private CheckBox _chkAutoStart = null!, _chkAutoBridge = null!, _chkTray = null!;

    // printer tab
    private Control _cardVirtualPrinter = null!, _cardRealPrinter = null!;
    private Label _lblVirtualState = null!, _lblVirtualHint = null!, _lblPrinterCheck = null!;
    private ComboBox _cmbDrivers = null!, _cmbWindowsQueue = null!;
    private NumericUpDown _numCapturePort = null!;
    private CheckBox _chkRaw = null!;
    private TextBox _txtTargetHost = null!;
    private NumericUpDown _numTargetPort = null!;
    private RadioButton _rbSocket = null!, _rbQueue = null!;

    // connection tab
    private Control _cardRelay = null!, _cardDirect = null!, _cardClientConn = null!;
    private TextBox _txtRelayUrl = null!, _txtAdminSecret = null!;
    private Label _lblChannelInfo = null!, _lblRelayState = null!, _lblClientConn = null!;
    private CheckBox _chkDirect = null!;
    private NumericUpDown _numDirectPort = null!;
    private TextBox _txtFingerprint = null!;

    // pairing tab
    private TextBox _txtPairCode = null!;
    private Label _lblPairSummary = null!, _lblPairHelp = null!;
    private Button _btnUsePair = null!;

    // jobs
    private ListView _lvJobs = null!;
    private RichTextBox _rtbPreview = null!;
    private RadioButton _rbText = null!, _rbHex = null!;
    private Label _lblPreviewInfo = null!;
    private string? _selectedJobId;

    // log
    private RichTextBox _rtbLog = null!;

    // status bar
    private ToolStripStatusLabel _barStatus = null!, _barQueue = null!, _barPath = null!;

    private bool _loading;
    private bool _dirty;
    private bool _reallyExit;
    private bool _printerBusy;
    private bool _trayHintShown;
    private DateTime _lastErrorBalloon = DateTime.MinValue;
    private int _ticks;

    public MainForm(AppSettings settings)
    {
        _settings = settings;

        Text = "LabelBridge";
        ClientSize = new Size(1160, 780);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.Body;
        Icon = IconFactory.AppIcon(32);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildHeader();
        BuildTabs();
        BuildStatusBar();
        BuildTray();

        LoadUi();
        ApplyRoleVisibility();
        RefreshPairingCode();
        RefreshDashboard();

        _timer.Tick += OnTick;
        _timer.Start();

        Log.Written += OnLogLine;
        Log.Minimum = LogLevel.Info;

        FormClosing += OnFormClosing;
        Shown += OnShown;
    }

    // ==================================================================== UI

    private void BuildHeader()
    {
        _header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Theme.Card };
        _header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.HeaderLine);
            e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        };

        var logo = new PictureBox
        {
            Image = IconFactory.Bitmap(44),
            Size = new Size(42, 42),
            Location = new Point(20, 18),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        };

        var title = new Label
        {
            Text = "LabelBridge",
            Font = Theme.H1,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(74, 12),
            BackColor = Color.Transparent
        };

        _subtitle = new Label
        {
            Text = "Virtual label printer bridge",
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(76, 44),
            BackColor = Color.Transparent
        };

        _pill = new Pill { Location = new Point(600, 25) };

        _btnStartStop = Ui.Button("Stop", width: 92);
        _btnStartStop.Height = 32;
        _btnStartStop.Click += async (_, _) => await ToggleEngineAsync();

        _btnApply = Ui.Button("Apply & restart", primary: true, width: 132);
        _btnApply.Height = 32;
        _btnApply.Enabled = false;
        _btnApply.Click += OnApply;

        _header.Controls.AddRange(new Control[] { logo, title, _subtitle, _pill, _btnApply, _btnStartStop });
        _header.Resize += (_, _) => LayoutHeader();
        Controls.Add(_header);
    }

    private void LayoutHeader()
    {
        int right = _header.Width - 22;
        _btnStartStop.Location = new Point(right - _btnStartStop.Width, 23);
        _btnApply.Location = new Point(_btnStartStop.Left - 10 - _btnApply.Width, 23);
        _pill.Location = new Point(_btnApply.Left - 16 - _pill.Width, 26);
    }

    private void BuildTabs()
    {
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Theme.BodyMedium,
            Padding = new Point(18, 6)
        };

        _tabOverview = Page("Overview");
        _tabPrinter = Page("Printer");
        _tabConnection = Page("Connection");
        _tabPairing = Page("Pairing");
        _tabJobs = Page("Jobs");
        _tabLog = Page("Log");

        BuildOverviewTab();
        BuildPrinterTab();
        BuildConnectionTab();
        BuildPairingTab();
        BuildJobsTab();
        BuildLogTab();

        _tabs.TabPages.AddRange(new[] { _tabOverview, _tabPrinter, _tabConnection, _tabPairing, _tabJobs, _tabLog });
        Controls.Add(_tabs);
    }

    private static TabPage Page(string title) =>
        new(title) { BackColor = Theme.Bg, Padding = new Padding(0) };

    private void BuildOverviewTab()
    {
        var stack = new CardsStack();
        stack.Add(BuildRoleCard());
        stack.Add(BuildStatsRow());
        stack.Add(BuildStatusCard());
        stack.Add(BuildStartupCard());
        _tabOverview.Controls.Add(stack);
    }

    private Control BuildRoleCard()
    {
        var card = new Card("This computer's role");

        _rbHost = Ui.Radio("Host - this PC is next to the printer and prints the jobs it receives");
        _rbClient = Ui.Radio("Client - this PC prints to a remote printer through the virtual printer");

        card.AddRow(_rbHost);
        card.AddRow(_rbClient);
        card.AddRow(Ui.Text("Changing the role restarts the bridge. Settings are kept.", muted: true, font: Theme.Small));
        return card;
    }

    private Control BuildStatsRow()
    {
        _statMode = new StatCard { Caption = "MODE", Accent = Theme.Accent };
        _statState = new StatCard { Caption = "BRIDGE", Accent = Theme.Green };
        _statQueue = new StatCard { Caption = "QUEUED JOBS", Accent = Theme.Amber };
        _statDelivered = new StatCard { Caption = "DELIVERED", Accent = Color.FromArgb(0x7C, 0x3A, 0xED) };

        var row = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 1,
            Dock = DockStyle.Top,
            Height = 100,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 14)
        };
        for (int i = 0; i < 4; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        var cards = new[] { _statMode, _statState, _statQueue, _statDelivered };
        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].Margin = new Padding(0, 0, i == cards.Length - 1 ? 0 : 12, 0);
            row.Controls.Add(cards[i], i, 0);
        }

        return row;
    }

    private Control BuildStatusCard()
    {
        var card = new Card("Status");
        _lblStatusDetail = Ui.Wrap("Bridge is stopped.", muted: false);
        _lblStatusDetail.MaximumSize = new Size(1400, 0);
        card.AddRow(_lblStatusDetail);

        _btnTestLabel = Ui.Button("Send test label", primary: true);
        _btnTestLabel.Click += async (_, _) => await SendTestLabelAsync();

        _btnWindowsTest = Ui.Button("Windows test page");
        _btnWindowsTest.Click += (_, _) => PrintWindowsTestPage();

        _btnImport = Ui.Button("Import .prn / .zpl file");
        _btnImport.Click += async (_, _) => await ImportFileAsync();

        _btnCheck = Ui.Button("Check printer");
        _btnCheck.Click += async (_, _) => await CheckConnectionAsync();

        _btnOpenData = Ui.Button("Open data folder");
        _btnOpenData.Click += (_, _) => OpenFolder(AppPaths.Root);

        card.AddRow(Ui.Row(_btnTestLabel, _btnWindowsTest, _btnImport, _btnCheck, _btnOpenData));

        _lblHow = Ui.Text("", muted: true, font: Theme.Small);
        _lblHow.MaximumSize = new Size(1400, 0);
        card.AddRow(_lblHow);
        return card;
    }

    private Control BuildStartupCard()
    {
        var card = new Card("Automatic start");
        _chkAutoStart = Ui.Check("Start LabelBridge when I sign in to Windows");
        _chkAutoBridge = Ui.Check("Start the bridge automatically when the app opens");
        _chkTray = Ui.Check("Keep running in the notification area when the window is closed");
        card.AddRow(_chkAutoStart);
        card.AddRow(_chkAutoBridge);
        card.AddRow(_chkTray);
        return card;
    }

    private void BuildPrinterTab()
    {
        var stack = new CardsStack();
        _cardVirtualPrinter = BuildVirtualPrinterCard();
        _cardRealPrinter = BuildRealPrinterCard();
        stack.Add(_cardVirtualPrinter);
        stack.Add(_cardRealPrinter);
        _tabPrinter.Controls.Add(stack);
    }

    private Control BuildVirtualPrinterCard()
    {
        var card = new Card("Virtual printer");

        _lblVirtualState = Ui.Text("Checking...", muted: true);
        card.AddRow(_lblVirtualState);

        _cmbDrivers = Ui.Combo();
        card.AddRow(Ui.Field("Printer driver", _cmbDrivers));

        _numCapturePort = Ui.Number(_settings.CapturePort, 1024, 65535, 110);
        card.AddRow(Ui.Field("Capture port (127.0.0.1)", _numCapturePort));

        _chkRaw = Ui.Check("Also install a raw pass-through printer (for apps that already send ZPL/EPL)");
        card.AddRow(_chkRaw);

        var install = Ui.Button("Install / repair printer", primary: true);
        install.Click += async (_, _) => await InstallPrinterAsync();

        var remove = Ui.Button("Remove printer");
        remove.Click += async (_, _) => await RemovePrinterAsync();

        var props = Ui.Button("Printer properties");
        props.Click += (_, _) => PrinterManager.OpenPrinterProperties(_settings.VirtualPrinterName);

        var testPage = Ui.Button("Windows test page");
        testPage.Click += (_, _) => PrintWindowsTestPage();

        var windows = Ui.Button("Windows printers");
        windows.Click += (_, _) => PrinterManager.OpenWindowsPrinters();

        card.AddRow(Ui.Row(install, remove, props, testPage, windows));

        _lblVirtualHint = Ui.Text("", muted: true, font: Theme.Small);
        _lblVirtualHint.MaximumSize = new Size(1400, 0);
        card.AddRow(_lblVirtualHint);

        var zebra = Ui.Button("Get the Zebra ZDesigner driver");
        zebra.Click += (_, _) => PrinterManager.OpenDriverDownload();
        card.AddRow(Ui.Row(zebra));

        return card;
    }

    private Control BuildRealPrinterCard()
    {
        var card = new Card("Real label printer");
        card.AddRow(Ui.Text("Where LabelBridge sends every job it receives.", muted: true, font: Theme.Small));

        _txtTargetHost = Ui.Input(_settings.TargetHost, 300);
        card.AddRow(Ui.Field("Printer address", _txtTargetHost));

        _numTargetPort = Ui.Number(_settings.TargetPort, 1, 65535, 110);
        card.AddRow(Ui.Field("TCP port", _numTargetPort));

        _rbSocket = Ui.Radio("Send to the printer over TCP (JetDirect / RAW, usually 9100)");
        _rbQueue = Ui.Radio("Re-spool to an installed Windows print queue");
        card.AddRow(_rbSocket);
        card.AddRow(_rbQueue);

        _cmbWindowsQueue = Ui.Combo();
        card.AddRow(Ui.Field("Windows queue", _cmbWindowsQueue));

        var check = Ui.Button("Check printer");
        check.Click += async (_, _) => await CheckConnectionAsync();

        var test = Ui.Button("Send test label", primary: true);
        test.Click += async (_, _) => await SendTestLabelAsync();

        var detect = Ui.Button("Detect from Windows");
        detect.Click += (_, _) => DetectPrinterFromWindows();

        card.AddRow(Ui.Row(check, test, detect));

        _lblPrinterCheck = Ui.Text("", muted: true, font: Theme.Small);
        _lblPrinterCheck.MaximumSize = new Size(1400, 0);
        card.AddRow(_lblPrinterCheck);

        return card;
    }

    private void BuildConnectionTab()
    {
        var stack = new CardsStack();

        // ---- host: relay
        var relay = new Card("Internet relay (recommended)");
        relay.AddRow(Ui.Text("Both this PC and the remote users connect out to the relay, so no port forwarding is needed anywhere.",
            muted: true, font: Theme.Small));

        _txtRelayUrl = Ui.Input(_settings.RelayUrl, 380);
        relay.AddRow(Ui.Field("Relay URL", _txtRelayUrl));

        _txtAdminSecret = Ui.Input("", 380);
        _txtAdminSecret.UseSystemPasswordChar = true;
        relay.AddRow(Ui.Field("Admin secret", _txtAdminSecret));

        var create = Ui.Button("Create channel", primary: true);
        create.Click += async (_, _) => await CreateChannelAsync();

        var testRelay = Ui.Button("Test relay");
        testRelay.Click += async (_, _) => await TestRelayAsync();

        relay.AddRow(Ui.Row(create, testRelay));

        _lblChannelInfo = Ui.Wrap("No channel yet.", muted: true);
        relay.AddRow(_lblChannelInfo);

        _lblRelayState = Ui.Text("", muted: true, font: Theme.Small);
        relay.AddRow(_lblRelayState);
        _cardRelay = relay;

        // ---- host: direct
        var direct = new Card("Direct TLS (LAN, VPN, Tailscale)");
        _chkDirect = Ui.Check("Enable the direct listener on this PC");
        direct.AddRow(_chkDirect);

        _numDirectPort = Ui.Number(_settings.DirectPort, 1024, 65535, 110);
        direct.AddRow(Ui.Field("Listen port", _numDirectPort));

        _txtFingerprint = Ui.Input("", 520);
        _txtFingerprint.ReadOnly = true;
        _txtFingerprint.Font = Theme.Mono;
        direct.AddRow(Ui.Field("Certificate", _txtFingerprint));

        direct.AddRow(Ui.Text("Clients pin this certificate fingerprint, so the pairing code also protects against " +
                              "someone intercepting the connection.", muted: true, font: Theme.Small));
        _cardDirect = direct;

        // ---- client: connection
        var clientConn = new Card("Connection to the host");
        _lblClientConn = Ui.Wrap("No pairing code imported yet.", muted: true);
        clientConn.AddRow(_lblClientConn);

        var testConn = Ui.Button("Test connection", primary: true);
        testConn.Click += async (_, _) => await CheckConnectionAsync();

        var goPairing = Ui.Button("Open pairing tab");
        goPairing.Click += (_, _) => _tabs.SelectedTab = _tabPairing;

        clientConn.AddRow(Ui.Row(testConn, goPairing));
        _cardClientConn = clientConn;

        stack.Add(relay);
        stack.Add(direct);
        stack.Add(clientConn);
        _tabConnection.Controls.Add(stack);
    }

    private void BuildPairingTab()
    {
        var stack = new CardsStack();
        var card = new Card("Pairing code");

        _lblPairHelp = Ui.Wrap("", muted: true);
        card.AddRow(_lblPairHelp);

        _txtPairCode = new TextBox
        {
            Multiline = true,
            Height = 132,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.Mono,
            BorderStyle = BorderStyle.FixedSingle,
            WordWrap = true,
            BackColor = Color.FromArgb(0xFA, 0xFB, 0xFE)
        };
        card.AddRow(_txtPairCode);

        var copy = Ui.Button("Copy code", primary: true);
        copy.Click += (_, _) => CopyPairCode();

        var save = Ui.Button("Save to file...");
        save.Click += (_, _) => SavePairCode();

        _btnUsePair = Ui.Button("Use this code");
        _btnUsePair.Click += async (_, _) => await UsePairCodeAsync();

        card.AddRow(Ui.Row(copy, save, _btnUsePair));

        _lblPairSummary = Ui.Text("", muted: true, font: Theme.Small);
        _lblPairSummary.MaximumSize = new Size(1400, 0);
        card.AddRow(_lblPairSummary);

        stack.Add(card);
        _tabPairing.Controls.Add(stack);
    }

    private void BuildJobsTab()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(18, 14, 18, 16) };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Bg
        };

        var refresh = Ui.Button("Refresh");
        refresh.Click += (_, _) => RefreshJobs();
        var open = Ui.Button("Open data folder");
        open.Click += (_, _) => OpenFolder(AppPaths.Root);
        var send = Ui.Button("Send test label", primary: true);
        send.Click += async (_, _) => await SendTestLabelAsync();

        toolbar.Controls.AddRange(new Control[] { refresh, open, send });

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

        _lvJobs = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        _lvJobs.Columns.Add("Time", 82);
        _lvJobs.Columns.Add("Name", 220);
        _lvJobs.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _lvJobs.Columns.Add("Status", 100);
        _lvJobs.Columns.Add("Detail", 380);
        _lvJobs.SelectedIndexChanged += (_, _) => LoadPreview();
        _lvJobs.Resize += (_, _) => AutoSizeJobsColumns();

        var previewPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
            Margin = new Padding(0, 12, 0, 0),
            Padding = new Padding(10, 8, 10, 8)
        };
        previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var previewHeader = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Dock = DockStyle.Top,
            BackColor = Color.White
        };
        previewHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _lblPreviewInfo = Ui.Text("Select a job to preview its data.", muted: true, font: Theme.Small);

        _rbText = Ui.Radio("Text");
        _rbText.Checked = true;
        _rbHex = Ui.Radio("Hex");
        _rbText.CheckedChanged += (_, _) => LoadPreview();
        _rbHex.CheckedChanged += (_, _) => LoadPreview();

        previewHeader.Controls.Add(_lblPreviewInfo, 0, 0);
        previewHeader.Controls.Add(Ui.Row(_rbText, _rbHex), 1, 0);

        _rtbPreview = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = Theme.Mono,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Theme.Text,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            DetectUrls = false
        };

        previewPanel.Controls.Add(previewHeader, 0, 0);
        previewPanel.Controls.Add(_rtbPreview, 0, 1);

        grid.Controls.Add(_lvJobs, 0, 0);
        grid.Controls.Add(previewPanel, 0, 1);

        host.Controls.Add(grid);
        host.Controls.Add(toolbar);
        _tabJobs.Controls.Add(host);
    }

    private void BuildLogTab()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(18, 14, 18, 16) };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Bg
        };

        var copy = Ui.Button("Copy log");
        copy.Click += (_, _) =>
        {
            try { if (_rtbLog.TextLength > 0) Clipboard.SetText(_rtbLog.Text); } catch { }
        };

        var openLogs = Ui.Button("Open log folder");
        openLogs.Click += (_, _) => OpenFolder(AppPaths.Logs);

        var clear = Ui.Button("Clear view");
        clear.Click += (_, _) => _rtbLog.Clear();

        var openData = Ui.Button("Open data folder");
        openData.Click += (_, _) => OpenFolder(AppPaths.Root);

        toolbar.Controls.AddRange(new Control[] { copy, openLogs, openData, clear });

        var box = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(8), BorderStyle = BorderStyle.FixedSingle };
        _rtbLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = Theme.Mono,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            DetectUrls = false
        };
        box.Controls.Add(_rtbLog);

        host.Controls.Add(box);
        host.Controls.Add(toolbar);
        _tabLog.Controls.Add(host);
    }

    private void BuildStatusBar()
    {
        var bar = new StatusStrip
        {
            BackColor = Theme.Card,
            ForeColor = Theme.Muted,
            SizingGrip = false,
            Padding = new Padding(10, 2, 10, 2)
        };

        _barStatus = new ToolStripStatusLabel("Idle") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _barQueue = new ToolStripStatusLabel("Queue: 0") { BorderSides = ToolStripStatusLabelBorderSides.Left };
        _barPath = new ToolStripStatusLabel("") { BorderSides = ToolStripStatusLabelBorderSides.Left, ForeColor = Theme.Faint };

        bar.Items.AddRange(new ToolStripItem[] { _barStatus, _barQueue, _barPath });
        Controls.Add(bar);
    }

    private void BuildTray()
    {
        _tray.Icon = IconFactory.AppIcon(32);
        _tray.Text = "LabelBridge";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new ContextMenuStrip { Font = Theme.Body };
        menu.Items.Add("Open LabelBridge", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start / stop bridge", null, async (_, _) => await ToggleEngineAsync());
        menu.Items.Add("Send test label", null, async (_, _) => await SendTestLabelAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
    }

    // ================================================================== state

    private void OnShown(object? sender, EventArgs e)
    {
        if (!File.Exists(AppPaths.ConfigFile))
        {
            using var dialog = new RoleDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _settings.Role = dialog.Role;

            _settings.Save();
            LoadUi();
            ApplyRoleVisibility();
            RefreshPairingCode();
        }

        LayoutHeader();
        _ = RefreshPrinterStateAsync();
        RefreshJobs();

        if (_settings.AutoStartBridge) StartEngine();
        if (_settings.StartMinimized) BeginInvoke(new Action(HideToTray));
    }

    private void StartEngine()
    {
        if (_engine?.IsRunning == true) return;

        if (_engine != null)
        {
            _engine.Notice -= OnNotice;
            try { _engine.StopAsync().GetAwaiter().GetResult(); } catch { }
        }

        var engine = new BridgeEngine(_settings);
        engine.Notice += OnNotice;
        _engine = engine;

        try
        {
            engine.StartAsync().GetAwaiter().GetResult();
            Log.Info($"Bridge started as {_settings.Role}.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not start the bridge", ex);
            _lblStatusDetail.Text = "Could not start: " + ex.Message;
            Ui.Error(this, "Could not start the bridge:\r\n\r\n" + ex.Message);
        }

        RefreshDashboard();
    }

    private async Task ToggleEngineAsync()
    {
        if (_engine?.IsRunning == true)
        {
            await _engine.StopAsync();
            Log.Info("Bridge stopped by user.");
        }
        else
        {
            _settings.Save();
            StartEngine();
        }

        RefreshDashboard();
    }

    private async Task RestartEngineAsync()
    {
        if (_engine != null)
        {
            _engine.Notice -= OnNotice;
            try { await _engine.StopAsync(); } catch { }
            _engine = null;
        }

        if (_settings.AutoStartBridge) StartEngine();
        else RefreshDashboard();

        await Task.Yield();
    }

    private async void OnApply(object? sender, EventArgs e)
    {
        _loading = true;
        CollectUi();
        _loading = false;
        _settings.Save();
        SetDirty(false);
        ApplyRoleVisibility();
        RefreshPairingCode();

        await RestartEngineAsync();
        await RefreshPrinterStateAsync();
        RefreshDashboard();
        Log.Info("Settings applied.");
    }

    private void SetDirty(bool dirty = true)
    {
        if (_loading && dirty) return;
        _dirty = dirty;
        _btnApply.Enabled = dirty;
        _btnApply.Text = dirty ? "Apply & restart" : "Saved";
    }

    // =================================================================== data

    private void LoadUi()
    {
        _loading = true;
        try
        {
            _rbHost.Checked = _settings.IsHost;
            _rbClient.Checked = !_settings.IsHost;

            _txtTargetHost.Text = _settings.TargetHost;
            _numTargetPort.Value = Math.Clamp(_settings.TargetPort, 1, 65535);
            _rbSocket.Checked = !_settings.UseWindowsQueue;
            _rbQueue.Checked = _settings.UseWindowsQueue;
            _numCapturePort.Value = Math.Clamp(_settings.CapturePort, 1024, 65535);
            _chkDirect.Checked = _settings.DirectServerEnabled;
            _numDirectPort.Value = Math.Clamp(_settings.DirectPort, 1024, 65535);
            _txtRelayUrl.Text = _settings.RelayUrl;
            _chkAutoStart.Checked = AutoStart.IsEnabled();
            _chkAutoBridge.Checked = _settings.AutoStartBridge;
            _chkTray.Checked = _settings.MinimizeToTray;
            _txtFingerprint.Text = SafeFingerprint();

            _lblChannelInfo.Text = string.IsNullOrWhiteSpace(_settings.ChannelId)
                ? "No relay channel yet. Enter the relay URL and press 'Create channel'."
                : $"Channel: {_settings.ChannelId}\r\nRelay:  {_settings.RelayUrl}\r\n" +
                  (PairingFactory.IsReady(_settings)
                      ? "Pairing code is ready on the Pairing tab."
                      : "Channel exists, but the client token is missing - create a new channel.");

            if (!_settings.IsHost && _settings.Pair == null)
                _lblClientConn.Text = "No pairing code imported yet. Open the Pairing tab and paste the code from the host.";
            else if (!_settings.IsHost && _settings.Pair != null)
                _lblClientConn.Text = $"Paired with: {_settings.Pair.EndpointText}\r\n" +
                                      $"Printer: {_settings.Pair.PrinterName}";
        }
        finally
        {
            _loading = false;
        }

        SetDirty(false);
    }

    private void CollectUi()
    {
        _settings.Role = _rbHost.Checked ? "host" : "client";
        _settings.TargetHost = _txtTargetHost.Text.Trim();
        _settings.TargetPort = (int)_numTargetPort.Value;
        _settings.UseWindowsQueue = _rbQueue.Checked;
        _settings.WindowsQueueName = _cmbWindowsQueue.SelectedItem as string ?? _settings.WindowsQueueName;
        _settings.CapturePort = (int)_numCapturePort.Value;
        _settings.DirectServerEnabled = _chkDirect.Checked;
        _settings.DirectPort = (int)_numDirectPort.Value;
        _settings.RelayUrl = _txtRelayUrl.Text.Trim();
        _settings.RelayPollEnabled = !string.IsNullOrWhiteSpace(_settings.RelayUrl) &&
                                     !string.IsNullOrWhiteSpace(_settings.ChannelId) &&
                                     !string.IsNullOrWhiteSpace(_settings.HostToken);
        _settings.AutoStartBridge = _chkAutoBridge.Checked;
        _settings.MinimizeToTray = _chkTray.Checked;

        if (_cmbDrivers.SelectedItem is string driver && driver.Length > 0)
            _settings.VirtualPrinterDriver = driver;

        AutoStart.Set(_chkAutoStart.Checked);
    }

    private void ApplyRoleVisibility()
    {
        bool host = _settings.IsHost;

        _cardVirtualPrinter.Visible = !host;
        _cardRealPrinter.Visible = host;
        _cardRelay.Visible = host;
        _cardDirect.Visible = host;
        _cardClientConn.Visible = !host;

        _btnTestLabel.Visible = host;
        _btnWindowsTest.Visible = !host;
        _btnCheck.Text = host ? "Check printer" : "Check connection";
        _btnUsePair.Visible = !host;
        _txtPairCode.ReadOnly = host;
        _lblPairHelp.Text = host
            ? "Give this code to the people who will print to this printer. It contains the relay channel (or the LAN endpoint) and a key, but never the printer's address."
            : "Paste the pairing code you received from the host, then press 'Use this code'.";

        _lblHow.Text = host
            ? "Host mode: jobs arrive from the relay (or the direct TLS listener), wait in a durable queue, and are sent to the real printer. If the printer is offline, jobs are retried automatically."
            : "Client mode: print from any Windows application to the virtual printer. LabelBridge captures the ZPL, queues it, and delivers it to the host over the internet.";

        _btnStartStop.Text = _engine?.IsRunning == true ? "Stop" : "Start";
    }

    private void RefreshDashboard()
    {
        if (!IsHandleCreated || IsDisposed) return;

        var engine = _engine;
        bool running = engine?.IsRunning == true;

        _statMode.Set(_settings.IsHost ? "Host" : "Client",
            _settings.IsHost ? "prints on this PC" : "sends to a remote printer");

        _statState.Set(running ? "Running" : "Stopped",
            running ? "bridge is active" : "press Start to begin");

        _statQueue.Set((engine?.PendingCount ?? 0).ToString(), "waiting in queue");
        _statDelivered.Set((engine?.JobsOut ?? 0).ToString(), "delivered this session");

        if (!running)
            _pill.Set("Stopped", Theme.GraySoft, Theme.Muted);
        else if (LooksLikeProblem(engine!.Status))
            _pill.Set("Attention", Theme.AmberSoft, Theme.Amber);
        else
            _pill.Set("Running", Theme.GreenSoft, Theme.Green);

        if (running && engine != null)
            _lblStatusDetail.Text = engine.Status;

        _btnStartStop.Text = running ? "Stop" : "Start";
        _subtitle.Text = _settings.IsHost
            ? "Host - printing to " + (_settings.UseWindowsQueue
                ? $"queue '{_settings.WindowsQueueName}'"
                : $"{_settings.TargetHost}:{_settings.TargetPort}")
            : "Client - " + (_settings.Pair?.EndpointText ?? "not paired yet");

        _barStatus.Text = engine?.Status ?? "Idle";
        _barQueue.Text = $"Queue: {engine?.PendingCount ?? 0}";
        _barPath.Text = AppPaths.Root;
        _barPath.ToolTipText = AppPaths.Root;

        if (_settings.IsHost)
            _lblChannelInfo.Text = string.IsNullOrWhiteSpace(_settings.ChannelId)
                ? "No relay channel yet. Enter the relay URL and press 'Create channel'."
                : $"Channel: {_settings.ChannelId}\r\nRelay:  {_settings.RelayUrl}\r\n" +
                  "Receive status is shown below.";

        if (running && _settings.RelayPollEnabled)
            _lblRelayState.Text = "Polling the relay for jobs.";
        else if (_settings.IsHost)
            _lblRelayState.Text = "Relay polling is off. Create a channel or enable direct mode.";
    }

    private static bool LooksLikeProblem(string status)
    {
        var s = status.ToLowerInvariant();
        return s.Contains("error") || s.Contains("fail") || s.Contains("unreachable") ||
               s.Contains("not paired") || s.Contains("rejected") || s.Contains("retry");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed) return;

        _ticks++;
        RefreshDashboard();

        if (_ticks % 3 == 0 && _tabs.SelectedTab == _tabJobs) RefreshJobs();
        if (_ticks % 20 == 0 && _tabs.SelectedTab == _tabPrinter) _ = RefreshPrinterStateAsync();
    }

    private void OnNotice(BridgeNotice notice)
    {
        if (IsDisposed || !IsHandleCreated) return;

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<BridgeNotice>(OnNotice), notice); } catch { }
            return;
        }

        _lblStatusDetail.Text = notice.Message;

        if (notice.Kind == NoticeKind.Failed)
            _tray.ShowBalloonTip(2000, "LabelBridge", notice.Message, ToolTipIcon.Warning);
        else if (notice.Kind == NoticeKind.Delivered)
            _tray.ShowBalloonTip(1200, "LabelBridge", notice.Message, ToolTipIcon.Info);

        if (notice.Kind is NoticeKind.Incoming or NoticeKind.Delivered or NoticeKind.Failed)
            RefreshJobs();

        RefreshDashboard();
    }

    private void OnLogLine(LogLine line)
    {
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (_rtbLog.IsDisposed) return;
                if (_rtbLog.TextLength > 400_000)
                    _rtbLog.Text = _rtbLog.Text[^120_000..];

                _rtbLog.AppendText(line + Environment.NewLine);
                _rtbLog.SelectionStart = _rtbLog.TextLength;
                _rtbLog.ScrollToCaret();

                // Surface failures without forcing the user to watch the log tab.
                if (line.Level == LogLevel.Error && !_reallyExit &&
                    DateTime.UtcNow - _lastErrorBalloon > TimeSpan.FromSeconds(15))
                {
                    _lastErrorBalloon = DateTime.UtcNow;
                    try
                    {
                        _tray.ShowBalloonTip(4000, "LabelBridge problem",
                            line.Message.Length > 220 ? line.Message[..220] + "…" : line.Message,
                            ToolTipIcon.Warning);
                    }
                    catch { }
                }
            }));
        }
        catch { }
    }

    // ================================================================ actions

    private async Task SendTestLabelAsync()
    {
        if (_engine?.IsRunning != true)
        {
            Ui.Info(this, "Start the bridge first.");
            return;
        }

        Cursor = Cursors.WaitCursor;
        try
        {
            if (_settings.IsHost)
                await _engine.SendTestLabelAsync();
            else
                PrintWindowsTestPage();

            if (_settings.IsHost)
                Ui.Info(this, "A test label was sent to " + _engine.TargetText() + ".");
        }
        catch (Exception ex)
        {
            Ui.Error(this, "Could not send the test label:\r\n\r\n" + ex.Message);
        }
        finally { Cursor = Cursors.Default; }
    }

    private void PrintWindowsTestPage()
    {
        var name = _settings.VirtualPrinterName;
        PrinterManager.PrintTestPage(name);
        Ui.Info(this, $"A Windows test page is being printed to '{name}'.\n\n" +
                      "It will be captured, sent to the host and printed on the real label printer. " +
                      "Check the Jobs tab to follow it.");
    }

    private async Task ImportFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Send a printer file",
            Filter = "Printer files (*.prn;*.zpl;*.epl;*.txt)|*.prn;*.zpl;*.epl;*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            if (_engine == null || !_engine.IsRunning)
            {
                Ui.Info(this, "Start the bridge first.");
                return;
            }

            await _engine.QueueFileAsync(dialog.FileName);
            RefreshJobs();
            _tabs.SelectedTab = _tabJobs;
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex.Message);
        }
    }

    private async Task CheckConnectionAsync()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            if (_settings.IsHost)
            {
                var (ok, message) = _engine != null
                    ? await _engine.CheckPrinterAsync()
                    : await Task.FromResult((false, "Bridge is not created yet."));

                _lblPrinterCheck.Text = message;
                _lblPrinterCheck.ForeColor = ok ? Theme.Green : Theme.Red;
                if (!ok) Ui.Error(this, message); else Ui.Info(this, message);
            }
            else
            {
                var (ok, message) = _engine != null
                    ? await _engine.CheckTransportAsync()
                    : (false, "Bridge is not created yet.");

                _lblClientConn.Text = message;
                _lblClientConn.ForeColor = ok ? Theme.Green : Theme.Red;
                if (!ok) Ui.Error(this, message); else Ui.Info(this, message);
            }
        }
        catch (Exception ex)
        {
            Ui.Error(this, ex.Message);
        }
        finally { Cursor = Cursors.Default; }
    }

    private async Task InstallPrinterAsync()
    {
        _loading = true;
        CollectUi();
        _loading = false;

        if (!PrinterManager.IsPortFree(_settings.CapturePort))
        {
            if (!Ui.Confirm(this,
                    $"Port {_settings.CapturePort} is already in use by another program.\n\n" +
                    "Continue anyway? Choose a different capture port if the virtual printer does not work."))
                return;
        }

        _lblVirtualState.Text = "Installing - confirm the Windows administrator prompt...";
        _lblVirtualState.ForeColor = Theme.Amber;
        Application.DoEvents();

        var driver = _cmbDrivers.SelectedItem as string;
        var includeRaw = _chkRaw.Checked;

        var result = await Task.Run(() => PrinterManager.Install(_settings, includeRaw, driver));
        _settings.Save();

        if (result.Ok)
        {
            Ui.Info(this, result.Output + "\r\n\r\nYou can now print from any Windows application " +
                          $"to \"{_settings.VirtualPrinterName}\".");
        }
        else
        {
            Ui.Error(this, "Printer installation failed:\r\n\r\n" + result.Output);
        }

        await RefreshPrinterStateAsync();
        await RestartEngineAsync();
    }

    private async Task RemovePrinterAsync()
    {
        if (!Ui.Confirm(this, $"Remove the virtual printer \"{_settings.VirtualPrinterName}\" and its port?"))
            return;

        var result = await Task.Run(() => PrinterManager.Remove(_settings.VirtualPrinterName));
        if (result.Ok) Ui.Info(this, result.Output);
        else Ui.Error(this, result.Output);

        await RefreshPrinterStateAsync();
    }

    private async Task RefreshPrinterStateAsync()
    {
        if (_printerBusy) return;
        _printerBusy = true;

        try
        {
            var catalog = await Task.Run(() => PrinterManager.LoadCatalog());
            if (IsDisposed) return;

            if (catalog.Error != null)
            {
                _lblVirtualState.Text = catalog.Error;
                _lblVirtualState.ForeColor = Theme.Red;
                return;
            }

            // driver list
            var drivers = catalog.Drivers
                .Where(d => d.Contains("ZPL", StringComparison.OrdinalIgnoreCase) ||
                            d.Equals("Generic / Text Only", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d)
                .ToList();

            _loading = true;
            try
            {
                var current = _cmbDrivers.SelectedItem as string ?? _settings.VirtualPrinterDriver;
                _cmbDrivers.Items.Clear();
                foreach (var d in drivers) _cmbDrivers.Items.Add(d);

                if (drivers.Count == 0 && catalog.Drivers.Count > 0)
                    foreach (var d in catalog.Drivers) _cmbDrivers.Items.Add(d);

                if (_cmbDrivers.Items.Contains(current)) _cmbDrivers.SelectedItem = current;
                else if (_cmbDrivers.Items.Count > 0)
                    _cmbDrivers.SelectedIndex = PrinterManager.PickDriver(catalog, current) is { } pick
                        ? Math.Max(0, _cmbDrivers.Items.IndexOf(pick))
                        : 0;

                var installed = catalog.Find(_settings.VirtualPrinterName);
                var port = catalog.Ports.FirstOrDefault(p =>
                    p.Name.Equals(PrinterManager.PortName, StringComparison.OrdinalIgnoreCase));

                if (installed != null)
                {
                    _lblVirtualState.Text = $"Installed: {installed.Name}   (driver: {installed.Driver})";
                    _lblVirtualState.ForeColor = Theme.Green;

                    if (port != null && port.Port != _settings.CapturePort)
                        _lblVirtualHint.Text = $"Port mismatch: Windows forwards to 127.0.0.1:{port.Port} but " +
                                               $"LabelBridge is listening on {_settings.CapturePort}. Press " +
                                               "'Install / repair printer' to fix it.";
                    else if (drivers.Count == 0)
                        _lblVirtualHint.Text = "The ZDesigner ZPL driver is not installed. Download it from Zebra, " +
                                               "install it for your printer model, then repair the printer.";
                    else
                        _lblVirtualHint.Text = "The virtual printer is ready. Print from any Windows application; " +
                                               "the job is captured, queued and delivered to the host.";
                }
                else
                {
                    _lblVirtualState.Text = "Virtual printer is not installed.";
                    _lblVirtualState.ForeColor = Theme.Amber;
                    _lblVirtualHint.Text = drivers.Count == 0
                        ? "Install the Zebra ZDesigner ZPL driver first (button below), then press " +
                          "'Install / repair printer'."
                        : "Press 'Install / repair printer' and approve the administrator prompt. " +
                          "Windows will restart the print spooler once.";
                }

                // Windows queue list for host mode
                var queues = catalog.Printers.Select(p => p.Name).ToList();
                var selected = _cmbWindowsQueue.SelectedItem as string ?? _settings.WindowsQueueName;
                _cmbWindowsQueue.Items.Clear();
                foreach (var q in queues) _cmbWindowsQueue.Items.Add(q);
                if (_cmbWindowsQueue.Items.Contains(selected)) _cmbWindowsQueue.SelectedItem = selected;
                else if (_cmbWindowsQueue.Items.Count > 0) _cmbWindowsQueue.SelectedIndex = 0;
            }
            finally { _loading = false; }
        }
        catch (Exception ex)
        {
            Log.Warn("Printer detection failed: " + ex.Message);
        }
        finally { _printerBusy = false; }
    }

    private void DetectPrinterFromWindows()
    {
        var catalog = PrinterManager.LoadCatalog(force: true);
        var tcp = catalog.Ports
            .Where(p => !p.Name.Equals(PrinterManager.PortName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tcp.Count == 0)
        {
            Ui.Info(this, "No Windows printer with a TCP/IP port was found. Enter the printer address manually.");
            return;
        }

        var port = tcp[0];
        _txtTargetHost.Text = port.Host;
        _numTargetPort.Value = Math.Clamp(port.Port, 1, 65535);
        SetDirty();
        Ui.Info(this, $"Using {port.Host}:{port.Port} (from Windows port '{port.Name}').\r\n" +
                      "Press 'Apply & restart' to save.");
    }

    private async Task CreateChannelAsync()
    {
        var url = _txtRelayUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            Ui.Error(this, "Enter the relay URL first, for example http://192.168.50.10:8080");
            return;
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;

        Cursor = Cursors.WaitCursor;
        try
        {
            var (channelId, hostToken, clientToken) = await RelayPoller.CreateChannelAsync(
                url, _txtAdminSecret.Text.Trim(), Environment.MachineName, CancellationToken.None);

            _settings.RelayUrl = url.TrimEnd('/');
            _settings.ChannelId = channelId;
            _settings.HostToken = hostToken;
            _settings.ChannelClientToken = clientToken;
            _settings.RelayPollEnabled = true;
            _settings.Save();
            _txtRelayUrl.Text = _settings.RelayUrl;

            LoadUi();
            RefreshPairingCode();
            await RestartEngineAsync();
            SetDirty(false);

            Ui.Info(this, "Relay channel created.\r\n\r\n" +
                          $"Channel: {channelId}\r\n\r\n" +
                          "The pairing code on the Pairing tab is ready to share.");
        }
        catch (Exception ex)
        {
            Ui.Error(this, "Could not create the channel:\r\n\r\n" + ex.Message +
                           "\r\n\r\nCheck that the relay container is running and that the URL is reachable from this PC.");
        }
        finally { Cursor = Cursors.Default; }
    }

    private async Task TestRelayAsync()
    {
        var url = _txtRelayUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) { Ui.Error(this, "Enter the relay URL first."); return; }
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;

        Cursor = Cursors.WaitCursor;
        try
        {
            var health = await RelayPoller.HealthAsync(url, CancellationToken.None);
            _lblRelayState.Text = "Relay answered: " + health;
            _lblRelayState.ForeColor = Theme.Green;
            Ui.Info(this, "Relay is reachable.\r\n\r\n" + health);
        }
        catch (Exception ex)
        {
            _lblRelayState.Text = "Relay unreachable: " + ex.Message;
            _lblRelayState.ForeColor = Theme.Red;
            Ui.Error(this, "Relay is not reachable:\r\n\r\n" + ex.Message);
        }
        finally { Cursor = Cursors.Default; }
    }

    private void RefreshPairingCode()
    {
        if (_settings.IsHost)
        {
            if (!PairingFactory.IsReady(_settings) && !_settings.DirectServerEnabled)
            {
                _txtPairCode.Text = "";
                _lblPairSummary.Text = "Create a relay channel (Connection tab) or enable direct mode first.";
                return;
            }

            try
            {
                var pair = PairingFactory.BuildClientPair(_settings);
                _txtPairCode.Text = Pairing.Encode(pair);
                _lblPairSummary.Text = $"{pair}   -   printer: {pair.PrinterName}";
            }
            catch (Exception ex)
            {
                _txtPairCode.Text = "";
                _lblPairSummary.Text = "Could not build the pairing code: " + ex.Message;
            }
        }
        else
        {
            var pair = _settings.Pair;
            if (pair == null)
            {
                _lblPairSummary.Text = "Not paired yet.";
                return;
            }

            _txtPairCode.Text = Pairing.Encode(pair);
            _lblPairSummary.Text = $"{pair}   -   printer: {pair.PrinterName}";
        }
    }

    private void CopyPairCode()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_txtPairCode.Text)) return;
            Clipboard.SetText(_txtPairCode.Text);
            Ui.Info(this, "Pairing code copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Ui.Error(this, "Could not access the clipboard: " + ex.Message);
        }
    }

    private void SavePairCode()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save pairing code",
            Filter = "LabelBridge pairing (*.lbpair)|*.lbpair|Text file (*.txt)|*.txt",
            FileName = "labelbridge.lbpair"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dialog.FileName, _txtPairCode.Text);
            Ui.Info(this, "Saved to " + dialog.FileName);
        }
        catch (Exception ex)
        {
            Ui.Error(this, "Could not save the file: " + ex.Message);
        }
    }

    private async Task UsePairCodeAsync()
    {
        var text = _txtPairCode.Text;
        if (text.StartsWith("PK", StringComparison.OrdinalIgnoreCase))
        {
            try { text = File.ReadAllText(text); } catch { }
        }

        var pair = Pairing.TryDecode(text);
        if (pair == null)
        {
            Ui.Error(this, "That does not look like a LabelBridge pairing code.\r\n\r\nPaste the whole code, including the LB1: prefix.");
            return;
        }

        if (!pair.IsComplete)
        {
            Ui.Error(this, "The pairing code is incomplete. Ask the host to send a new one.");
            return;
        }

        _settings.Pair = pair;
        _settings.Save();
        RefreshPairingCode();
        SetDirty(false);

        await RestartEngineAsync();
        Ui.Info(this, "Pairing saved.\r\n\r\n" + pair + "\r\n\r\n" +
                      "The bridge will deliver jobs to this endpoint.");
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // =================================================================== jobs

    private void RefreshJobs()
    {
        if (_lvJobs == null || _engine == null) return;

        var selected = _selectedJobId;
        var items = new List<(PrintJob job, string state, string detail)>();

        foreach (var job in _engine.QueueSnapshot(80))
            items.Add((job, job.LastError != null ? "Retrying" : "Queued",
                job.LastError ?? $"{job.Source}"));

        foreach (var job in _engine.HistorySnapshot())
            items.Add((job, "Delivered", job.Source));

        if (items.Count == 0 && _engine.PendingCount == 0)
        {
            _lvJobs.BeginUpdate();
            _lvJobs.Items.Clear();
            _lvJobs.EndUpdate();
            _lblPreviewInfo.Text = "No jobs yet. Print something, send a test label, or import a .prn file.";
            return;
        }

        _lvJobs.BeginUpdate();
        try
        {
            _lvJobs.Items.Clear();
            foreach (var (job, state, detail) in items)
            {
                var item = new ListViewItem(job.CreatedUtc.ToLocalTime().ToString("HH:mm:ss")) { Tag = job.Id };
                item.SubItems.Add(job.Name);
                item.SubItems.Add(FormatBytes(job.Size));
                item.SubItems.Add(state);
                item.SubItems.Add(detail);
                item.ForeColor = state switch
                {
                    "Retrying" => Theme.Amber,
                    "Delivered" => Theme.Green,
                    _ => Theme.Text
                };
                _lvJobs.Items.Add(item);

                if (selected != null && job.Id == selected) item.Selected = true;
            }
        }
        finally { _lvJobs.EndUpdate(); }

        AutoSizeJobsColumns();
    }

    private void AutoSizeJobsColumns()
    {
        if (_lvJobs.Columns.Count < 5) return;
        int width = _lvJobs.ClientSize.Width - 82 - 220 - 90 - 100 - 4;
        _lvJobs.Columns[4].Width = Math.Max(120, width);
    }

    private void LoadPreview()
    {
        if (_lvJobs.SelectedItems.Count == 0)
        {
            _selectedJobId = null;
            _lblPreviewInfo.Text = "Select a job to preview its data.";
            _rtbPreview.Clear();
            return;
        }

        var item = _lvJobs.SelectedItems[0];
        _selectedJobId = item.Tag as string;

        if (_selectedJobId == null || _engine == null)
        {
            _rtbPreview.Text = "Job data is no longer available.";
            return;
        }

        var data = _engine.LoadJobData(_selectedJobId);
        if (data == null || data.Length == 0)
        {
            _lblPreviewInfo.Text = item.SubItems[1].Text + " - payload not available";
            _rtbPreview.Text = "This job has no stored payload (it may have been trimmed from history).";
            return;
        }

        var kind = PrinterData.Classify(data);
        _lblPreviewInfo.Text = $"{item.SubItems[1].Text}   -   {FormatBytes(data.Length)}   -   {PrinterData.Describe(kind)}";
        _rtbPreview.Text = _rbHex.Checked ? PrinterData.HexDump(data, 8192) : PrinterData.Preview(data, 65536);
        _rtbPreview.SelectionStart = 0;
        _rtbPreview.ScrollToCaret();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0") + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.00") + " MB"
    };

    private string SafeFingerprint()
    {
        try { return TlsCert.Fingerprint(TlsCert.LoadOrCreate()); }
        catch (Exception ex) { return "(unavailable: " + ex.Message + ")"; }
    }

    // ================================================================ closing

    private void HideToTray()
    {
        Hide();
        if (_trayHintShown) return;

        _trayHintShown = true;
        try
        {
            _tray.ShowBalloonTip(2500, "LabelBridge is still running",
                "Print jobs continue to be processed. Double-click the tray icon to open the window.",
                ToolTipIcon.Info);
        }
        catch { }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyExit && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _timer.Stop();
        Log.Written -= OnLogLine;
        _tray.Visible = false;

        if (_engine != null)
        {
            _engine.Notice -= OnNotice;
            try { Task.Run(async () => await _engine.StopAsync()).Wait(3000); } catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
