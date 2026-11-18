using LabelBridge.Core;

namespace LabelBridge.Relay;

/// <summary>
/// The internet relay. Runs anywhere .NET 8 runs, including a TrueNAS LXC or a
/// container. Terminate TLS in a reverse proxy (Caddy, Traefik, nginx, Cloudflare
/// Tunnel) and point it at this process.
///
///   LabelBridge.Relay                                   defaults
///   LabelBridge.Relay --listen http://+:8080/ --data /data --admin s3cret
///
/// Environment variables (container friendly): LB_LISTEN, LB_DATA, LB_ADMIN_SECRET,
/// LB_MAX_JOBS, LB_MAX_MB, LB_RETENTION_HOURS.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        if (args.Any(a => a is "--version"))
        {
            Console.WriteLine("LabelBridge relay " + typeof(Program).Assembly.GetName().Version);
            return 0;
        }

        // Container health probe: exit 0 when the relay answers /health.
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

        var options = new RelayOptions
        {
            ListenPrefix = Arg(args, "--listen") ?? Env("LB_LISTEN") ?? "http://+:8080/",
            DataDir = Arg(args, "--data") ?? Env("LB_DATA") ?? "data",
            AdminSecret = Arg(args, "--admin") ?? Env("LB_ADMIN_SECRET"),
        };

        if (int.TryParse(Arg(args, "--max-jobs") ?? Env("LB_MAX_JOBS"), out var maxJobs) && maxJobs > 0)
            options.MaxJobsPerChannel = maxJobs;

        if (int.TryParse(Arg(args, "--max-mb") ?? Env("LB_MAX_MB"), out var maxMb) && maxMb > 0)
            options.MaxPayloadBytes = maxMb * 1024 * 1024;

        if (int.TryParse(Arg(args, "--retention-hours") ?? Env("LB_RETENTION_HOURS"), out var hours) && hours > 0)
            options.RetentionHours = hours;

        if (!options.ListenPrefix.EndsWith('/')) options.ListenPrefix += "/";

        Log.Minimum = LogLevel.Info;

        Directory.CreateDirectory(options.DataDir);

        Log.Info($"LabelBridge relay starting - version {typeof(Program).Assembly.GetName().Version}");
        Log.Info($"Data directory: {Path.GetFullPath(options.DataDir)}");

        using var server = new RelayServer(options);
        using var stop = new ManualResetEventSlim();

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Cannot start the relay: " + ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Common causes:");
            Console.Error.WriteLine("  * the port is already in use");
            Console.Error.WriteLine("  * binding to a hostname like '+' needs admin/root on Windows");
            Console.Error.WriteLine("  * on Linux, ports below 1024 require privileges - use 8080 behind a proxy");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("  LabelBridge relay is running");
        Console.WriteLine("  ------------------------------");
        Console.WriteLine($"  Listening : {options.ListenPrefix}");
        Console.WriteLine($"  Data      : {Path.GetFullPath(options.DataDir)}");
        Console.WriteLine($"  Admin     : {(string.IsNullOrEmpty(options.AdminSecret) ? "NOT SET - anyone who can reach this URL can create a channel" : "required")}");
        Console.WriteLine();
        Console.WriteLine($"  Health    : {options.ListenPrefix.Replace("+", "localhost").TrimEnd('/')}/health");
        Console.WriteLine();
        Console.WriteLine("  Create a printer channel from the Windows host app (Connection tab),");
        Console.WriteLine("  or with:  curl -X POST -H 'X-LB-Admin: <secret>' <url>/api/v1/channels");
        Console.WriteLine();
        Console.WriteLine("  Press Ctrl+C to stop.");

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled relay error: " + e.ExceptionObject);

        stop.Wait();
        server.Stop();

        Console.WriteLine("Relay stopped.");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
LabelBridge relay - print job relay for the LabelBridge Windows client/host pair

Usage:
  LabelBridge.Relay [options]

Options:
  --listen <prefix>        HttpListener prefix (default http://+:8080/)
  --data <dir>             storage folder for channels and queued jobs (default ./data)
  --admin <secret>         require this value in X-LB-Admin when creating channels
  --max-jobs <n>           max queued jobs per printer channel (default 1000)
  --max-mb <n>             max single job size in MB (default 64)
  --retention-hours <n>    delete unclaimed jobs after this many hours (default 168)

Environment:
  LB_LISTEN, LB_DATA, LB_ADMIN_SECRET, LB_MAX_JOBS, LB_MAX_MB, LB_RETENTION_HOURS

Docker example:
  docker run -d --name labelbridge-relay -p 8080:8080 \
    -v labelbridge:/data -e LB_ADMIN_SECRET=change-me labelbridge/relay
""");
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
