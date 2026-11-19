using LabelBridge.Core;
using LabelBridge.Core.Server;

namespace LabelBridge.Relay;

/// <summary>
/// LabelBridge all-in-one server (Linux/Windows). Hosts web UI + API for
/// admin auth/setup, printer discovery/config, client approval, job queue and
/// direct printing to a network printer (TCP 9100). Windows clients register and
/// upload print jobs over HTTP(S).
///
/// Env: LB_LISTEN, LB_DATA, LB_ADMIN_SECRET, LB_MAX_JOBS, LB_MAX_MB, LB_RETENTION_HOURS, LB_REPO, LB_VERSION
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?")) { PrintHelp(); return 0; }
        if (args.Any(a => a is "--version")) { Console.WriteLine("Online Printer Server " + GetVersion()); return 0; }

        var probe = Arg(args, "--health-check");
        if (!string.IsNullOrWhiteSpace(probe))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var resp = http.GetAsync(probe).GetAwaiter().GetResult();
                Console.WriteLine($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
                return resp.IsSuccessStatusCode ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("health check failed: " + ex.Message);
                return 1;
            }
        }

        var config = new ServerConfig
        {
            ListenPrefix = Arg(args, "--listen") ?? Env("LB_LISTEN") ?? "http://+:8080/",
            DataDir = Arg(args, "--data") ?? Env("LB_DATA") ?? "data",
            InitialAdminSecret = Arg(args, "--admin") ?? Env("LB_ADMIN_SECRET"),
            Repo = Env("LB_REPO") ?? "bogdan-ralakde/OnlinePrinter",
            CurrentVersion = Env("LB_VERSION") ?? GetVersion()
        };

        if (int.TryParse(Arg(args, "--max-jobs") ?? Env("LB_MAX_JOBS"), out var maxJobs) && maxJobs > 0) config.MaxJobs = maxJobs;
        if (int.TryParse(Arg(args, "--max-mb") ?? Env("LB_MAX_MB"), out var maxMb) && maxMb > 0) config.MaxPayloadBytes = maxMb * 1024 * 1024;
        if (int.TryParse(Arg(args, "--retention-hours") ?? Env("LB_RETENTION_HOURS"), out var hours) && hours > 0) config.RetentionHours = hours;

        Log.Minimum = LogLevel.Info;
        Directory.CreateDirectory(config.DataDir);

        Log.Info($"Online Printer Server starting - version {config.CurrentVersion}");
        Log.Info($"Data directory: {Path.GetFullPath(config.DataDir)}");

        using var server = new LabelBridgeServer(config);
        using var stop = new ManualResetEventSlim();
        try { server.Start(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Cannot start server: " + ex.Message);
            Console.Error.WriteLine("Check listen prefix/port permissions and that the port is free.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("  Online Printer Server is running");
        Console.WriteLine("  -----------------------------");
        Console.WriteLine($"  Listening : {config.ListenPrefix}");
        Console.WriteLine($"  Data      : {Path.GetFullPath(config.DataDir)}");
        Console.WriteLine($"  Web UI    : open the listen URL in a browser (login required)");
        Console.WriteLine($"  Health    : {config.ListenPrefix.Replace("+", "localhost").TrimEnd('/')}/health");
        Console.WriteLine();
        Console.WriteLine("  Configure printer and approve clients in the web UI.");
        Console.WriteLine("  Press Ctrl+C to stop.");

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled server error: " + e.ExceptionObject);

        stop.Wait();
        server.Stop();
        Console.WriteLine("Server stopped.");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Online Printer Server - all-in-one print server with web UI

Usage:
  LabelBridge.Relay [options]

Options:
  --listen <prefix>        HttpListener prefix (default http://+:8080/)
  --data <dir>             data directory (default ./data)
  --admin <secret>         initial admin password (if unset, a random one is logged once)
  --max-jobs <n>           max queued jobs (default 2000)
  --max-mb <n>             max upload size in MB (default 128)
  --retention-hours <n>    retention window (default 168)

Environment:
  LB_LISTEN, LB_DATA, LB_ADMIN_SECRET, LB_MAX_JOBS, LB_MAX_MB, LB_RETENTION_HOURS, LB_REPO, LB_VERSION
""");
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string GetVersion()
    {
        try { return typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.1.2"; } catch { return "1.1.2"; }
    }
}
