using System.Security.Cryptography;
using System.Text;

namespace LabelBridge.Core;

/// <summary>
/// Central location for every on-disk artefact the app owns.
/// Uses ProgramData when writable (installer grants Modify to Users), otherwise
/// falls back to LocalAppData so a plain copy-paste run also works.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string QueueClient => Ensure(Path.Combine(Root, "client-queue"));
    public static string QueueHost   => Ensure(Path.Combine(Root, "host-queue"));
    public static string History     => Ensure(Path.Combine(Root, "history"));
    public static string Inbox       => Ensure(Path.Combine(Root, "inbox"));
    public static string Logs        => Ensure(Path.Combine(Root, "logs"));
    public static string Certs       => Ensure(Path.Combine(Root, "certs"));
    public static string Temp        => Ensure(Path.Combine(Root, "temp"));

    public static string ConfigFile => Path.Combine(Root, "settings.json");
    public static string CertFile   => Path.Combine(Certs, "labelbridge-host.pfx");

    private static string ResolveRoot()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabelBridge"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabelBridge"),
        };

        foreach (var dir in candidates)
            if (TryWritable(dir)) return dir;

        var fallback = Path.Combine(Path.GetTempPath(), "LabelBridge");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool TryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Ensure(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* surfaced later on use */ }
        return path;
    }

    /// <summary>
    /// Best-effort ACL tightening for files holding secrets (the host TLS key).
    /// Well-known groups are referenced by SID so this also works on non-English
    /// Windows. Uses icacls, so no Windows-only managed APIs are required.
    /// </summary>
    public static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;

        try
        {
            var who = Environment.UserDomainName + "\\" + Environment.UserName;
            Run("icacls", $"\"{path}\" /inheritance:r /grant:r \"{who}:F\" *S-1-5-18:F *S-1-5-32-544:F");
        }
        catch
        {
            // Non-fatal: the file is still protected by the parent folder's ACL.
        }
    }

    private static void Run(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(5000);
    }

    public static string ShortenPath(string? path, int max = 64)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return path.Length <= max ? path : path[..(max / 2 - 2)] + "..." + path[^(max / 2 - 2)..];
    }

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
