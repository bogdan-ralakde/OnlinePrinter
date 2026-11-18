using LabelBridge.Core;
using LabelBridge.Services;

namespace LabelBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ConsoleBridge.Attach();

        try
        {
            return Cli.Run(args);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            Console.Error.WriteLine("ERROR: " + ex);
            if (Environment.UserInteractive && args.Length == 0)
            {
                MessageBox.Show(ex.ToString(), "LabelBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 1;
        }
    }
}

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return RunGui(null);

        var cmd = args[0].Trim().ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        switch (cmd)
        {
            case "--help":
            case "-h":
            case "/?":
                PrintHelp();
                return 0;

            case "--version":
                Console.WriteLine("LabelBridge " + typeof(Cli).Assembly.GetName().Version);
                return 0;

            case "--gui":
            case "--tray":
                return RunGui(null);

            case "--role":
                return RunGui(Get(rest, "--role") ?? rest.FirstOrDefault());

            case "--selftest":
                return SelfTest.Run(rest);

            case "--send":
                return SendFileCommand(rest);

            case "--printer-install":
                return PrinterCommands.Install(rest);

            case "--printer-remove":
                return PrinterCommands.Remove(rest);

            case "--printer-script":
                return PrinterCommands.PrintScript(rest);

            case "--printer-status":
                return PrinterCommands.Status();

            case "--pairing":
                return PrinterCommands.PrintPairingCode();

            case "--relay":
                return RelayCommand(rest);

            case "--daemon":
                return DaemonCommand(rest);

            case "--paths":
                PrintPaths();
                return 0;

            default:
                Console.WriteLine($"Unknown command '{args[0]}'. Use --help.");
                return 2;
        }
    }

    // ------------------------------------------------------------------- GUI

    private static int RunGui(string? role)
    {
        var settings = AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(role))
            settings.Role = role.Contains("host", StringComparison.OrdinalIgnoreCase) ? "host" : "client";

        using var mutex = new Mutex(true, @"Local\LabelBridge.Gui.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show(
                "LabelBridge is already running.\n\nLook for the LabelBridge icon in the notification area (system tray).",
                "LabelBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception);

        using var form = new UI.MainForm(settings);
        Application.Run(form);

        GC.KeepAlive(mutex);
        return 0;
    }

    private static void ShowCrash(Exception? ex)
    {
        Log.Error("Unhandled UI error", ex ?? new Exception("unknown"));
        MessageBox.Show(ex?.ToString() ?? "Unknown error", "LabelBridge - unexpected error",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ------------------------------------------------------------- commands

    private static async Task<int> SendFileCommandImpl(string[] args)
    {
        var file = Get(args, "--send") ?? args.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            Console.Error.WriteLine("Usage: LabelBridge --send <file.prn> --pair <code|@pairfile>");
            return 2;
        }

        var pairArg = Get(args, "--pair");
        if (string.IsNullOrWhiteSpace(pairArg))
        {
            Console.Error.WriteLine("Missing --pair <code|@file>.");
            return 2;
        }

        var pairText = pairArg.StartsWith('@') ? File.ReadAllText(pairArg[1..]) : pairArg;
        var pair = Pairing.TryDecode(pairText);
        if (pair == null || !pair.IsComplete)
        {
            Console.Error.WriteLine("The pairing code is not valid.");
            return 2;
        }

        var data = File.ReadAllBytes(file);
        var job = new PrintJob
        {
            Name = Get(args, "--name") ?? Path.GetFileName(file),
            Source = "cli",
            Data = data
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            if (pair.IsRelay)
            {
                using var sender = new RelaySender(pair);
                await sender.SendAsync(job, cts.Token);
            }
            else
            {
                using var sender = new DirectSender(pair);
                await sender.SendAsync(job, cts.Token);
            }

            Console.WriteLine($"OK {data.Length:N0} bytes delivered to {pair.EndpointText}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex.Message);
            return 1;
        }
    }

    private static int SendFileCommand(string[] args) => SendFileCommandImpl(args).GetAwaiter().GetResult();

    private static int RelayCommand(string[] args)
    {
        var options = new RelayOptions
        {
            ListenPrefix = Get(args, "--listen") ?? Environment.GetEnvironmentVariable("LB_LISTEN") ?? "http://+:8080/",
            DataDir = Get(args, "--data") ?? Environment.GetEnvironmentVariable("LB_DATA") ?? Path.Combine(AppPaths.Root, "relay"),
            AdminSecret = Get(args, "--admin") ?? Environment.GetEnvironmentVariable("LB_ADMIN_SECRET"),
        };

        using var server = new RelayServer(options);
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Cannot start relay: " + ex.Message);
            Console.Error.WriteLine("On Linux, use a non-privileged port (e.g. 8080) and put it behind a reverse proxy.");
            return 1;
        }

        Console.WriteLine($"LabelBridge relay listening on {options.ListenPrefix}");
        Console.WriteLine($"Data directory : {Path.GetFullPath(options.DataDir)}");
        Console.WriteLine($"Admin secret   : {(string.IsNullOrEmpty(options.AdminSecret) ? "(not set - channel creation is open)" : "required")}");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();
        done.Wait();

        server.Stop();
        return 0;
    }

    private static int DaemonCommand(string[] args)
    {
        var settings = AppSettings.Load();
        var role = Get(args, "--role");
        if (role != null) settings.Role = role;

        using var done = new ManualResetEventSlim();
        var engine = new BridgeEngine(settings);
        engine.Notice += n => Console.WriteLine($"[{n.Kind}] {n.Message}");
        engine.StartAsync().GetAwaiter().GetResult();

        Console.WriteLine($"LabelBridge running as {settings.Role}. Press Ctrl+C to stop.");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();
        done.Wait();

        engine.StopAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static void PrintPaths()
    {
        Console.WriteLine("Data root : " + AppPaths.Root);
        Console.WriteLine("Settings  : " + AppPaths.ConfigFile);
        Console.WriteLine("Logs      : " + AppPaths.Logs);
        Console.WriteLine("Queue     : " + AppPaths.QueueClient + " | " + AppPaths.QueueHost);
        Console.WriteLine("History   : " + AppPaths.History);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
LabelBridge - virtual label printer bridge

  LabelBridge                         Open the GUI
  LabelBridge --role host             Open the GUI in host mode
  LabelBridge --role client           Open the GUI in client mode

  LabelBridge --send <file> --pair <code|@file>
                                      Send a raw ZPL/EPL file using a pairing code
  LabelBridge --printer-install [--driver <name>] [--port <n>]
                                      Create/repair the virtual printer (admin)
  LabelBridge --printer-remove        Delete the virtual printer (admin)
  LabelBridge --printer-status        Show detected printers/drivers as JSON
  LabelBridge --printer-script        Print the PowerShell used to install the printer
  LabelBridge --pairing               Print this host's pairing code

  LabelBridge --relay [--listen http://+:8080/] [--data <dir>] [--admin <secret>]
                                      Run the internet relay (Linux/Docker/Windows)
  LabelBridge --daemon [--role host]  Run the bridge headless
  LabelBridge --selftest              End-to-end self-test
  LabelBridge --paths                 Show data folders
""");
    }

    private static string? Get(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
