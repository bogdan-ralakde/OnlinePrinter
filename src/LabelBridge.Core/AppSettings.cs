using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelBridge.Core;

public enum BridgeRole { Client, Host }

/// <summary>
/// Everything a client needs to reach a printer. Produced by the host (or the
/// relay admin UI) and handed over as a single compact "pairing code".
/// </summary>
public sealed class PairInfo
{
    /// <summary>"relay" (internet / behind NAT) or "direct" (LAN / VPN, TLS listener).</summary>
    public string Mode { get; set; } = "relay";

    // relay mode
    public string RelayUrl { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ClientToken { get; set; } = "";

    // direct mode
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9443;
    public string Key { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    // cosmetic
    public string PrinterName { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime IssuedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsRelay => string.Equals(Mode, "relay", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsComplete => IsRelay
        ? !string.IsNullOrWhiteSpace(RelayUrl) && !string.IsNullOrWhiteSpace(ChannelId) && !string.IsNullOrWhiteSpace(ClientToken)
        : !string.IsNullOrWhiteSpace(Host) && Port > 0 && !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Fingerprint);

    [JsonIgnore]
    public string EndpointText => IsRelay ? RelayUrl : $"{Host}:{Port}";

    public override string ToString()
        => IsRelay
            ? $"Relay {RelayUrl} (channel {Short(ChannelId)})"
            : $"Direct {Host}:{Port}";

    private static string Short(string s) => s.Length <= 8 ? s : s[..8] + "…";
}

public sealed class AppSettings
{
    public string Role { get; set; } = "client";
    public string DeviceName { get; set; } = Environment.MachineName;

    // ---------------------------------------------------------------- client
    /// <summary>Loopback TCP port the virtual printer spools into.</summary>
    public int CapturePort { get; set; } = 9191;
    /// <summary>Driver used for the virtual printer (GDI rendering).</summary>
    public string VirtualPrinterName { get; set; } = "LabelBridge";
    public string VirtualPrinterDriver { get; set; } = "ZDesigner ZD420-300dpi ZPL";
    public PairInfo? Pair { get; set; }

    // ------------------------------------------------------------------ host
    public string TargetHost { get; set; } = "";
    public int TargetPort { get; set; } = 9100;
    public bool UseWindowsQueue { get; set; }
    public string WindowsQueueName { get; set; } = "";

    public bool DirectServerEnabled { get; set; } = true;
    public int DirectPort { get; set; } = 9443;
    /// <summary>Base64url 32-byte pre-shared key for direct TLS mode.</summary>
    public string DirectKey { get; set; } = "";

    public bool RelayPollEnabled { get; set; }
    public string RelayUrl { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string HostToken { get; set; } = "";
    /// <summary>Kept locally only so the host can re-display the pairing code.</summary>
    public string ChannelClientToken { get; set; } = "";

    // ----------------------------------------------------------------- shell
    public bool AutoStart { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoStartBridge { get; set; } = true;
    public int MaxRetrySeconds { get; set; } = 120;

    // ------------------------------------------------------------- persisted
    public long LifetimeJobsSent { get; set; }
    public long LifetimeJobsFailed { get; set; }

    [JsonIgnore] public bool IsHost => string.Equals(Role, "host", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public BridgeRole EffectiveRole => IsHost ? BridgeRole.Host : BridgeRole.Client;

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
            // No ACL tightening here: settings must stay readable for every user
            // who prints on this PC (the installer grants Users Modify).
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    private void Normalize()
    {
        Role = IsHost ? "host" : "client";
        if (CapturePort is < 1024 or > 65535) CapturePort = 9191;
        if (DirectPort is < 1024 or > 65535) DirectPort = 9443;
        if (TargetPort is < 1 or > 65535) TargetPort = 9100;
        if (MaxRetrySeconds < 5) MaxRetrySeconds = 5;
        if (string.IsNullOrWhiteSpace(VirtualPrinterName)) VirtualPrinterName = "LabelBridge";
        if (string.IsNullOrWhiteSpace(VirtualPrinterDriver)) VirtualPrinterDriver = "ZDesigner ZD420-300dpi ZPL";

        if (string.IsNullOrWhiteSpace(DirectKey))
            DirectKey = Pairing.NewKeyText();

        RelayUrl = (RelayUrl ?? "").Trim().TrimEnd('/');
        TargetHost = (TargetHost ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(RelayUrl) && !RelayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            RelayUrl = "http://" + RelayUrl;

        if (IsHost)
        {
            RelayPollEnabled = RelayPollEnabled && !string.IsNullOrWhiteSpace(RelayUrl)
                               && !string.IsNullOrWhiteSpace(ChannelId) && !string.IsNullOrWhiteSpace(HostToken);
        }
    }

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts)!;
        copy.Normalize();
        return copy;
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
