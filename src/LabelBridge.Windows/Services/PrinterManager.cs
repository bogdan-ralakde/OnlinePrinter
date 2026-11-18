using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LabelBridge.Core;

namespace LabelBridge.Services;

public sealed record PrinterInfo(string Name, string Driver, string Port);
public sealed record PortInfo(string Name, string Host, int Port);
public sealed record CommandResult(bool Ok, int ExitCode, string Output);

public sealed class PrinterCatalog
{
    public List<PrinterInfo> Printers { get; } = new();
    public List<string> Drivers { get; } = new();
    public List<PortInfo> Ports { get; } = new();
    public string? Error { get; set; }

    public PrinterInfo? Find(string name) =>
        Printers.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Creates the Windows side of the virtual printer: a TCP/IP port pointing at
/// 127.0.0.1 plus a printer queue using the ZDesigner ZPL driver, so anything
/// printed is rendered to ZPL by the normal Windows GDI path and streamed into
/// LabelBridge. All privileged work happens in one elevated PowerShell step.
/// </summary>
public static class PrinterManager
{
    public const string PortName = "LabelBridge_Loopback";
    public const string RawSuffix = " Raw";
    public const string ZebraDownloadUrl = "https://www.zebra.com/us/en/support-downloads/printer-software/printer-driver.html";

    private static PrinterCatalog? _cache;
    private static DateTime _cacheUtc;

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public static PrinterCatalog LoadCatalog(bool force = false)
    {
        if (!force && _cache != null && DateTime.UtcNow - _cacheUtc < TimeSpan.FromSeconds(20))
            return _cache;

        const string script = """
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$printers = @(Get-Printer | ForEach-Object { [pscustomobject]@{ name = $_.Name; driver = $_.DriverName; port = $_.PortName; status = [string]$_.PrinterStatus } })
$drivers  = @(Get-PrinterDriver | ForEach-Object { $_.Name })
$ports    = @(Get-PrinterPort | Where-Object { $_.PrinterHostAddress } | ForEach-Object { [pscustomobject]@{ name = $_.Name; host = $_.PrinterHostAddress; port = [int]$_.PortNumber } })
[pscustomobject]@{ printers = $printers; drivers = $drivers; ports = $ports } | ConvertTo-Json -Depth 5 -Compress
""";

        var catalog = new PrinterCatalog();
        try
        {
            var result = RunPowerShell(script, elevate: false, timeoutMs: 25000);
            var json = ExtractJson(result.Output);
            if (string.IsNullOrWhiteSpace(json))
            {
                catalog.Error = "Could not query printers: " + Short(result.Output);
                return catalog;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("printers", out var printers) && printers.ValueKind == JsonValueKind.Array)
                foreach (var p in printers.EnumerateArray())
                    catalog.Printers.Add(new PrinterInfo(
                        Str(p, "name"), Str(p, "driver"), Str(p, "port")));

            if (root.TryGetProperty("drivers", out var drivers) && drivers.ValueKind == JsonValueKind.Array)
                foreach (var d in drivers.EnumerateArray())
                    if (d.GetString() is { Length: > 0 } name) catalog.Drivers.Add(name);

            if (root.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
                foreach (var p in ports.EnumerateArray())
                    catalog.Ports.Add(new PortInfo(Str(p, "name"), Str(p, "host"),
                        p.TryGetProperty("port", out var pn) && pn.TryGetInt32(out var number) ? number : 9100));
        }
        catch (Exception ex)
        {
            catalog.Error = "Could not query printers: " + ex.Message;
        }

        _cache = catalog;
        _cacheUtc = DateTime.UtcNow;
        return catalog;
    }

    /// <summary>Best installed ZDesigner ZPL driver, preferring the requested one.</summary>
    public static string? PickDriver(PrinterCatalog catalog, string preferred)
    {
        if (catalog.Drivers.Any(d => d.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
            return catalog.Drivers.First(d => d.Equals(preferred, StringComparison.OrdinalIgnoreCase));

        var zebra = catalog.Drivers
            .Where(d => d.Contains("ZDesigner", StringComparison.OrdinalIgnoreCase) &&
                        d.Contains("ZPL", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Contains("300", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (zebra.Count > 0) return zebra[0];

        return catalog.Drivers.FirstOrDefault(d => d.Equals("Generic / Text Only", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }

    public static CommandResult Install(AppSettings settings, bool includeRawPrinter, string? driverOverride = null)
    {
        var catalog = LoadCatalog(force: true);
        var driver = driverOverride ?? PickDriver(catalog, settings.VirtualPrinterDriver);

        if (string.IsNullOrWhiteSpace(driver))
            return new CommandResult(false, 1,
                "No suitable printer driver found.\r\n\r\nInstall the Zebra ZDesigner driver for your printer model " +
                "(ZPL version), then try again.\r\n" + ZebraDownloadUrl);

        if (!driver.Contains("ZPL", StringComparison.OrdinalIgnoreCase) &&
            !driver.EqualNoCase("Generic / Text Only"))
            return new CommandResult(false, 1,
                $"Driver '{driver}' is not a ZPL driver. Choose a 'ZDesigner ... ZPL' driver so labels are rendered correctly.");

        var script = BuildInstallScript(settings.VirtualPrinterName, driver, PortName, settings.CapturePort, includeRawPrinter);
        var result = RunPowerShell(script, elevate: !IsElevated, timeoutMs: 180000);
        _cache = null;
        return result;
    }

    public static CommandResult Remove(string printerName)
    {
        var script = BuildRemoveScript(printerName, PortName);
        var result = RunPowerShell(script, elevate: !IsElevated, timeoutMs: 120000);
        _cache = null;
        return result;
    }

    public static void OpenPrinterProperties(string printerName)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe",
                $"printui.dll,PrintUIEntry /p /n \"{printerName}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Cannot open printer properties: " + ex.Message); }
    }

    public static void PrintTestPage(string printerName)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe",
                $"printui.dll,PrintUIEntry /k /n \"{printerName}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Cannot print test page: " + ex.Message); }
    }

    public static void OpenWindowsPrinters()
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "printers") { UseShellExecute = true }); }
        catch { }
    }

    public static void OpenDriverDownload()
    {
        try { Process.Start(new ProcessStartInfo(ZebraDownloadUrl) { UseShellExecute = true }); }
        catch { }
    }

    // ------------------------------------------------------------- scripts

    public static string BuildInstallScript(string printerName, string driver, string portName, int portNumber, bool includeRaw)
    {
        var name = Ps(printerName);
        var driverPs = Ps(driver);
        var port = Ps(portName);

        var raw = includeRaw ? $$"""

$rawName = {{Ps(printerName + RawSuffix)}}
$rawDriver = 'Generic / Text Only'
try {
    if (-not (Get-PrinterDriver -Name $rawDriver -ErrorAction SilentlyContinue)) {
        Add-PrinterDriver -Name $rawDriver -ErrorAction SilentlyContinue
    }
    if (Get-PrinterDriver -Name $rawDriver -ErrorAction SilentlyContinue) {
        if (Get-Printer -Name $rawName -ErrorAction SilentlyContinue) { Remove-Printer -Name $rawName }
        Add-Printer -Name $rawName -DriverName $rawDriver -PortName $port
        Write-Output "Raw pass-through printer '$rawName' installed."
    } else {
        Write-Output "NOTE: the 'Generic / Text Only' driver is not available - raw printer skipped."
    }
} catch {
    Write-Output ("NOTE: raw printer not installed: " + $_.Exception.Message)
}
""" : "";

        return $$"""
Import-Module PrintManagement -ErrorAction Stop

$name = {{name}}
$driver = {{driverPs}}
$port = {{port}}
$portNumber = {{portNumber}}

if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {
    throw "Printer driver '$driver' is not installed on this computer."
}

if (Get-Printer -Name $name -ErrorAction SilentlyContinue) { Remove-Printer -Name $name }
if (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue) { Remove-PrinterPort -Name $port }

Add-PrinterPort -Name $port -PrinterHostAddress '127.0.0.1' -PortNumber $portNumber -SNMP 2
Add-Printer -Name $name -DriverName $driver -PortName $port

# Stop the Zebra driver from polling a printer that is not really there.
try {
    $printerKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Printers\' + $name
    if (Test-Path $printerKey) {
        New-ItemProperty -Path $printerKey -Name 'EnableBIDI' -PropertyType DWord -Value 0 -Force -ErrorAction SilentlyContinue | Out-Null
    }
    $portKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\' + $port
    if (Test-Path $portKey) {
        New-ItemProperty -Path $portKey -Name 'SNMP Enabled' -PropertyType DWord -Value 0 -Force -ErrorAction SilentlyContinue | Out-Null
    }
} catch {
    Write-Output ("NOTE: could not disable driver status polling: " + $_.Exception.Message)
}
{{raw}}
Write-Output "Printer '$name' installed."
Write-Output "Driver : $driver"
Write-Output "Port   : $port -> 127.0.0.1:$portNumber (RAW)"

Restart-Service -Name Spooler -Force
Write-Output "Print spooler restarted."
""";
    }

    public static string BuildRemoveScript(string printerName, string portName)
    {
        return $$"""
Import-Module PrintManagement -ErrorAction Stop

$name = {{Ps(printerName)}}
$rawName = {{Ps(printerName + RawSuffix)}}
$port = {{Ps(portName)}}

if (Get-Printer -Name $name -ErrorAction SilentlyContinue) { Remove-Printer -Name $name }
if (Get-Printer -Name $rawName -ErrorAction SilentlyContinue) { Remove-Printer -Name $rawName }
Start-Sleep -Milliseconds 400
if (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue) { Remove-PrinterPort -Name $port }

Write-Output "Virtual printer removed."
""";
    }

    private static string Ps(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

    // ------------------------------------------------------------ execution

    /// <summary>Runs a PowerShell script block and returns captured output.</summary>
    private static CommandResult RunPowerShell(string body, bool elevate, int timeoutMs)
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        var dir = Path.Combine(Path.GetTempPath(), "labelbridge");
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, $"script-{id}.ps1");
        var logPath = Path.Combine(dir, $"script-{id}.log");

        var wrapper = $$"""
$ErrorActionPreference = 'Stop'
$__log = {{Ps(logPath)}}
try {
    $__output = & {
{{body}}
    } *>&1 | Out-String
    $__output | Out-File -FilePath $__log -Encoding utf8
    exit 0
} catch {
    $msg = "ERROR: " + $_.Exception.Message
    $detail = ($_ | Out-String)
    ($msg + "`r`n" + $detail) | Out-File -FilePath $__log -Encoding utf8
    exit 1
}
""";

        try
        {
            File.WriteAllText(scriptPath, wrapper, new UTF8Encoding(false));

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = dir
            };

            if (elevate) psi.Verb = "runas";

            using var process = Process.Start(psi);
            if (process == null) return new CommandResult(false, -1, "Could not start PowerShell.");

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return new CommandResult(false, -1, "The operation timed out.");
            }

            var output = File.Exists(logPath) ? File.ReadAllText(logPath, Encoding.UTF8) : "";
            var ok = process.ExitCode == 0 && !output.TrimStart().StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase);

            if (elevate && process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                output = "The operation failed. If you cancelled the administrator prompt, try again.";

            return new CommandResult(ok, process.ExitCode, output.Trim());
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new CommandResult(false, 1223, "Administrator approval was cancelled.");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, -1, ex.Message);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    // --------------------------------------------------------------- helpers

    public static string? LocalIPv4()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            return host.AddressList
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .FirstOrDefault(a => a.StartsWith("192.168.") || a.StartsWith("10.") || a.StartsWith("172."))
                ?? host.AddressList.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Select(a => a.ToString()).FirstOrDefault();
        }
        catch { return null; }
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string Short(string s) => s.Length <= 300 ? s : s[..300];
}

internal static class StringExtensions
{
    public static bool EqualNoCase(this string value, string other) =>
        value.Equals(other, StringComparison.OrdinalIgnoreCase);
}
