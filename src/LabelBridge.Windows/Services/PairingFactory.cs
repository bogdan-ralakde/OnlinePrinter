using LabelBridge.Core;

namespace LabelBridge.Services;

/// <summary>Builds the pairing code a host hands to remote users.</summary>
public static class PairingFactory
{
    public static PairInfo BuildClientPair(AppSettings hostSettings, string? publicHost = null)
    {
        if (hostSettings.RelayPollEnabled &&
            !string.IsNullOrWhiteSpace(hostSettings.RelayUrl) &&
            !string.IsNullOrWhiteSpace(hostSettings.ChannelId) &&
            !string.IsNullOrWhiteSpace(hostSettings.ChannelClientToken))
        {
            return new PairInfo
            {
                Mode = "relay",
                RelayUrl = hostSettings.RelayUrl,
                ChannelId = hostSettings.ChannelId,
                ClientToken = hostSettings.ChannelClientToken,
                PrinterName = hostSettings.VirtualPrinterName,
                Label = Environment.MachineName
            };
        }

        var host = publicHost ?? PrinterManager.LocalIPv4() ?? "127.0.0.1";
        var cert = TlsCert.LoadOrCreate();

        return new PairInfo
        {
            Mode = "direct",
            Host = host,
            Port = hostSettings.DirectPort,
            Key = hostSettings.DirectKey,
            Fingerprint = TlsCert.Fingerprint(cert),
            PrinterName = hostSettings.VirtualPrinterName,
            Label = Environment.MachineName
        };
    }

    public static bool IsReady(AppSettings settings) =>
        settings.RelayPollEnabled && !string.IsNullOrWhiteSpace(settings.ChannelId) &&
        !string.IsNullOrWhiteSpace(settings.ChannelClientToken);
}

internal static class PrinterCommands
{
    public static int Install(string[] args)
    {
        var settings = AppSettings.Load();

        var driver = Get(args, "--driver");
        var portText = Get(args, "--port");
        if (int.TryParse(portText, out var port) && port is > 1024 and < 65536) settings.CapturePort = port;

        var includeRaw = args.Any(a => a.Equals("--raw", StringComparison.OrdinalIgnoreCase));
        var result = PrinterManager.Install(settings, includeRaw, driver);

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

    /// <summary>Prints the PowerShell that --printer-install would run (for review/dry runs).</summary>
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

        var includeRaw = args.Any(a => a.Equals("--raw", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(PrinterManager.BuildInstallScript(
            settings.VirtualPrinterName, driver, PrinterManager.PortName, settings.CapturePort, includeRaw));
        return 0;
    }

    public static int Status()
    {
        var catalog = PrinterManager.LoadCatalog(force: true);
        var settings = AppSettings.Load();
        var installed = catalog.Find(settings.VirtualPrinterName);

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            catalog.Error,
            virtualPrinter = settings.VirtualPrinterName,
            installed = installed != null,
            installedDriver = installed?.Driver,
            capturePort = settings.CapturePort,
            portFree = PrinterManager.IsPortFree(settings.CapturePort),
            drivers = catalog.Drivers,
            printers = catalog.Printers
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return installed != null ? 0 : 1;
    }

    public static int PrintPairingCode()
    {
        var settings = AppSettings.Load();
        if (!settings.IsHost)
        {
            Console.Error.WriteLine("This machine is configured as a client. Switch to host mode first (--role host).");
            return 1;
        }

        if (!PairingFactory.IsReady(settings) && !settings.DirectServerEnabled)
        {
            Console.Error.WriteLine("No relay channel and direct mode disabled - nothing to pair with.");
            return 1;
        }

        var pair = PairingFactory.BuildClientPair(settings);
        Console.WriteLine(Pairing.Encode(pair));
        Console.WriteLine();
        Console.WriteLine($"Mode: {pair.Mode}   Endpoint: {pair.EndpointText}");
        return 0;
    }

    private static string? Get(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
