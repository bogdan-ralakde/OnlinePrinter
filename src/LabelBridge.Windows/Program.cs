using System.Text.Json;
using LabelBridge.Core;
using LabelBridge.Services;

namespace LabelBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ConsoleBridge.Attach();
        try { return Cli.Run(args); }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            Console.Error.WriteLine("ERROR: " + ex);
            if (Environment.UserInteractive && args.Length == 0)
                MessageBox.Show(ex.ToString(), "LabelBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            case "--help": case "-h": case "/?": PrintHelp(); return 0;
            case "--version": Console.WriteLine("LabelBridge Client " + ClientAgent.AppVersion); return 0;
            case "--gui": case "--tray": return RunGui(null);
            case "--selftest": return SelfTest.Run(rest);
            case "--printer-install": return PrinterCli.Install(rest);
            case "--printer-remove": return PrinterCli.Remove(rest);
            case "--printer-script": return PrinterCli.PrintScript(rest);
            case "--printer-status": return PrinterCli.Status();
            case "--send": return SendFile(rest).GetAwaiter().GetResult();
            case "--paths": PrintPaths(); return 0;
            default:
                Console.WriteLine($"Unknown command '{args[0]}'. Use --help.");
                return 2;
        }
    }

    private static int RunGui(string? _)
    {
        var settings = AppSettings.Load();
        using var mutex = new Mutex(true, @"Local\LabelBridge.Gui.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("LabelBridge is already running.\n\nLook for the LabelBridge icon in the notification area (system tray).",
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
        try { MessageBox.Show(ex?.ToString() ?? "Unknown error", "LabelBridge - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
    }

    private static async Task<int> SendFile(string[] args)
    {
        string? file = null, server = null, token = null, name = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--send" && i + 1 < args.Length) file = args[++i];
            else if (a == "--server" && i + 1 < args.Length) server = args[++i];
            else if (a == "--token" && i + 1 < args.Length) token = args[++i];
            else if (a == "--name" && i + 1 < args.Length) name = args[++i];
            else if (!a.StartsWith("--") && file == null) file = a;
        }
        var settings = AppSettings.Load();
        server ??= settings.ServerUrl; token ??= settings.ClientToken;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) { Console.Error.WriteLine("Usage: LabelBridge --send <file> [--server <url> --token <token>]"); return 2; }
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(token)) { Console.Error.WriteLine("Server URL and approved client token are required."); return 2; }
        try
        {
            using var api = new ServerApiClient(server);
            var data = await File.ReadAllBytesAsync(file);
            var (ok, jobId, msg) = await api.UploadJobAsync(token, name ?? Path.GetFileName(file), data, CancellationToken.None);
            Console.WriteLine(ok ? $"OK {jobId} {msg}" : "FAILED: " + msg);
            return ok ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAILED: " + ex.Message); return 1; }
    }

    private static void PrintPaths()
    {
        Console.WriteLine("Data root : " + AppPaths.Root);
        Console.WriteLine("Settings  : " + AppPaths.ConfigFile);
        Console.WriteLine("Logs      : " + AppPaths.Logs);
        Console.WriteLine("Queue     : " + AppPaths.QueueClient);
        Console.WriteLine("History   : " + AppPaths.History);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
LabelBridge Client - virtual printer that sends jobs to LabelBridge server

  LabelBridge                         Open client UI
  LabelBridge --send <file> [--server <url> --token <token>]
                                      Upload a file as a print job
  LabelBridge --printer-install [--driver <name>] [--port <n>]
                                      Create/repair the local 'LabelBridge' virtual printer (admin)
  LabelBridge --printer-remove        Remove the local virtual printer (admin)
  LabelBridge --printer-status        Show detected printers/drivers as JSON
  LabelBridge --printer-script        Print printer install PowerShell script
  LabelBridge --selftest              Run built-in Option A sanity checks
  LabelBridge --paths                 Show data folders
""");
    }
}

internal static class PrinterCli
{
    public static int Install(string[] args)
    {
        var settings = AppSettings.Load();
        var driver = Get(args, "--driver");
        var portText = Get(args, "--port");
        if (int.TryParse(portText, out var port) && port is > 1024 and < 65536) settings.CapturePort = port;
        var result = PrinterManager.Install(settings, includeRawPrinter: false, driverOverride: driver);
        Console.WriteLine(result.Output);
        return result.Ok ? 0 : 1;
    }

    public static int Remove(string[] args)
    {
        var settings = AppSettings.Load();
        var name = Get(args, "--name") ?? settings.VirtualPrinterName;
        var result = PrinterManager.Remove(name);
        Console.WriteLine(result.Output);
        return result.Ok ? 0 : 1;
    }

    public static int PrintScript(string[] args)
    {
        var settings = AppSettings.Load();
        var portText = Get(args, "--port");
        if (int.TryParse(portText, out var port) && port is > 1024 and < 65536) settings.CapturePort = port;
        var driver = Get(args, "--driver");
        if (string.IsNullOrWhiteSpace(driver))
        {
            var catalog = PrinterManager.LoadCatalog(force: true);
            driver = PrinterManager.PickDriver(catalog, settings.VirtualPrinterDriver) ?? "<ZDesigner ZPL driver>";
        }
        Console.WriteLine(PrinterManager.BuildInstallScript(settings.VirtualPrinterName, driver, PrinterManager.PortName, settings.CapturePort, includeRaw: false));
        return 0;
    }

    public static int Status()
    {
        var catalog = PrinterManager.LoadCatalog(force: true);
        var settings = AppSettings.Load();
        var installed = catalog.Find(settings.VirtualPrinterName);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            catalog.Error,
            virtualPrinter = settings.VirtualPrinterName,
            installed = installed != null,
            installedDriver = installed?.Driver,
            capturePort = settings.CapturePort,
            portFree = PrinterManager.IsPortFree(settings.CapturePort),
            drivers = catalog.Drivers,
            printers = catalog.Printers
        }, new JsonSerializerOptions { WriteIndented = true }));
        return installed != null ? 0 : 1;
    }

    private static string? Get(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

