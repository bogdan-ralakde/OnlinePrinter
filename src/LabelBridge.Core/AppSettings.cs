using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelBridge.Core;

/// <summary>
/// Client settings for the LabelBridge Windows app: local virtual printer capture
/// and upload to an approved LabelBridge server.
/// </summary>
public sealed class AppSettings
{
    public string DeviceName { get; set; } = Environment.MachineName;

    // Server connection / approval
    public string ServerUrl { get; set; } = "";
    public string ClientRequestId { get; set; } = "";
    public string ClientToken { get; set; } = "";
    public string ClientStatus { get; set; } = ""; // Pending/Approved/Revoked/Declined

    // Server-pushed printer/profile/driver metadata (best-effort)
    public string LastServerPrinterName { get; set; } = "";
    public string LastServerPrinterProfileJson { get; set; } = "";
    public string LastServerDriverId { get; set; } = "";
    public string LastInstalledDriverId { get; set; } = "";
    public DateTime? LastServerConfigUtc { get; set; }

    // Local virtual printer capture
    public int CapturePort { get; set; } = 9191;
    public string VirtualPrinterName { get; set; } = "LabelBridge";
    public string VirtualPrinterDriver { get; set; } = "ZDesigner ZD420-300dpi ZPL";

    // App behavior
    public bool AutoStart { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoStartBridge { get; set; } = true;

    // Stats
    public long LifetimeJobsSent { get; set; }
    public long LifetimeJobsFailed { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var json = File.ReadAllText(AppPaths.ConfigFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings - starting with defaults", ex);
        }

        return new AppSettings();
    }

    public void Save()
    {
        Normalize();
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(AppPaths.ConfigFile)) File.Delete(AppPaths.ConfigFile);
            File.Move(tmp, AppPaths.ConfigFile);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    public void Normalize()
    {
        DeviceName = string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();
        ServerUrl = (ServerUrl ?? "").Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(ServerUrl) && !ServerUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            ServerUrl = "https://" + ServerUrl;

        ClientRequestId = (ClientRequestId ?? "").Trim();
        ClientToken = (ClientToken ?? "").Trim();
        ClientStatus = (ClientStatus ?? "").Trim();

        if (CapturePort is < 1024 or > 65535) CapturePort = 9191;
        if (string.IsNullOrWhiteSpace(VirtualPrinterName)) VirtualPrinterName = "LabelBridge";
        if (string.IsNullOrWhiteSpace(VirtualPrinterDriver)) VirtualPrinterDriver = "ZDesigner ZD420-300dpi ZPL";

        LastServerPrinterName ??= "";
        LastServerPrinterProfileJson ??= "";
        LastServerDriverId ??= "";
        LastInstalledDriverId ??= "";
    }

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts)!;
        copy.Normalize();
        return copy;
    }

    public static string NewToken(int bytes = 24)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
