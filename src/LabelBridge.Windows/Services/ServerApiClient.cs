using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabelBridge.Core;

namespace LabelBridge.Services;

public sealed class ServerPrinterProfile
{
    public string Name { get; set; } = "Default";
    public string Width { get; set; } = "";
    public string Height { get; set; } = "";
    public string Dpi { get; set; } = "";
    public string Orientation { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string PrintMethod { get; set; } = "";
    public string Darkness { get; set; } = "";
    public string Speed { get; set; } = "";
    public string DriverHint { get; set; } = "";
    public string PrintLanguage { get; set; } = "";
}

public sealed class ServerDriverInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string? SilentArgs { get; set; }
    public string? DownloadUrl { get; set; }
}

public sealed class GitHubReleaseAsset
{
    public string Name { get; set; } = "";
    public string BrowserDownloadUrl { get; set; } = "";
    public long Size { get; set; }
}

public sealed class ServerClientConfig
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string PrinterName { get; set; } = "";
    public string PrinterHost { get; set; } = "";
    public int PrinterPort { get; set; } = 9100;
    public bool PrinterEnabled { get; set; }
    public ServerPrinterProfile Profile { get; set; } = new();
    public ServerDriverInfo? Driver { get; set; }
    public string ServerVersion { get; set; } = "";
}

public sealed class ServerApiClient : IDisposable
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public ServerApiClient(string baseUrl)
    {
        BaseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    private string Url(string path) => BaseUrl + (path.StartsWith('/') ? path : "/" + path);

    public async Task<(bool ok, string requestId, string status, string message)> RegisterAsync(string name, string machine, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { name, machine }, AppSettings.JsonOpts);
        using var req = new HttpRequestMessage(HttpMethod.Post, Url("/api/client/register")) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return (false, "", "", ExtractError(body, res.StatusCode));
        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("requestId", out var r) ? r.GetString() ?? "" : "";
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "OK";
        return (true, id, status, msg);
    }

    public async Task<(bool ok, string status, bool canClaim, string? error)> GetStatusAsync(string requestId, CancellationToken ct)
    {
        using var res = await _http.GetAsync(Url($"/api/client/status?requestId={Uri.EscapeDataString(requestId)}"), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return (false, "", false, ExtractError(body, res.StatusCode));
        using var doc = JsonDocument.Parse(body);
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        var canClaim = doc.RootElement.TryGetProperty("canClaim", out var c) && c.ValueKind == JsonValueKind.True;
        return (true, status, canClaim, null);
    }

    public async Task<(bool ok, string? token, string status, string? error)> ClaimTokenAsync(string requestId, CancellationToken ct)
    {
        using var res = await _http.GetAsync(Url($"/api/client/claim?requestId={Uri.EscapeDataString(requestId)}"), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return (false, null, "", ExtractError(body, res.StatusCode));
        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        return (!string.IsNullOrWhiteSpace(token), token, status, token == null ? "No token" : null);
    }

    public async Task<ServerClientConfig> GetConfigAsync(string clientToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url("/api/client/config"));
        req.Headers.Add("X-Client-Token", clientToken);
        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return new ServerClientConfig { Ok = false, Error = ExtractError(body, res.StatusCode) };
        using var doc = JsonDocument.Parse(body);
        var cfg = new ServerClientConfig { Ok = true };
        if (doc.RootElement.TryGetProperty("printer", out var p))
        {
            cfg.PrinterName = p.TryGetProperty("Name", out var n) || p.TryGetProperty("name", out n) ? n.GetString() ?? "" : "";
            cfg.PrinterHost = p.TryGetProperty("Host", out var h) || p.TryGetProperty("host", out h) ? h.GetString() ?? "" : "";
            cfg.PrinterPort = (p.TryGetProperty("Port", out var po) || p.TryGetProperty("port", out po)) && po.TryGetInt32(out var pv) ? pv : 9100;
            cfg.PrinterEnabled = !(p.TryGetProperty("Enabled", out var e) || p.TryGetProperty("enabled", out e)) || e.ValueKind != JsonValueKind.False;
            if (p.TryGetProperty("profile", out var pr) || p.TryGetProperty("Profile", out pr))
            {
                cfg.Profile = new ServerPrinterProfile
                {
                    Name = Str(pr, "Name", "name") ?? "Default",
                    Width = Str(pr, "Width", "width") ?? "",
                    Height = Str(pr, "Height", "height") ?? "",
                    Dpi = Str(pr, "Dpi", "dpi") ?? "",
                    Orientation = Str(pr, "Orientation", "orientation") ?? "",
                    MediaType = Str(pr, "MediaType", "mediaType") ?? "",
                    PrintMethod = Str(pr, "PrintMethod", "printMethod") ?? "",
                    Darkness = Str(pr, "Darkness", "darkness") ?? "",
                    Speed = Str(pr, "Speed", "speed") ?? "",
                    DriverHint = Str(pr, "DriverHint", "driverHint") ?? "",
                    PrintLanguage = Str(pr, "PrintLanguage", "printLanguage") ?? "",
                };
            }
        }
        if (doc.RootElement.TryGetProperty("server", out var s))
            cfg.ServerVersion = Str(s, "version", "Version") ?? "";
        if (doc.RootElement.TryGetProperty("driver", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            cfg.Driver = new ServerDriverInfo
            {
                Id = Str(d, "Id", "id") ?? "",
                DisplayName = Str(d, "DisplayName", "displayName") ?? "",
                Version = Str(d, "Version", "version") ?? "",
                FileName = Str(d, "FileName", "fileName") ?? "",
                Size = (d.TryGetProperty("Size", out var sz) || d.TryGetProperty("size", out sz)) && sz.TryGetInt64(out var sl) ? sl : 0,
                Sha256 = Str(d, "Sha256", "sha256") ?? "",
                SilentArgs = Str(d, "SilentArgs", "silentArgs"),
                DownloadUrl = Str(d, "downloadUrl", "DownloadUrl")
            };
        }
        return cfg;
    }

    public async Task<(bool ok, string? jobId, string message)> UploadJobAsync(string clientToken, string name, byte[] data, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url("/api/client/jobs"));
        req.Headers.Add("X-Client-Token", clientToken);
        req.Headers.Add("X-Job-Name", Convert.ToBase64String(Encoding.UTF8.GetBytes(name ?? "Print job")));
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return (false, null, ExtractError(body, res.StatusCode));
        try
        {
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.TryGetProperty("jobId", out var j) ? j.GetString() : null;
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "OK" : "OK";
            return (true, id, msg);
        }
        catch { return (true, null, "OK"); }
    }

    public async Task<(bool ok, string path, string? error)> DownloadDriverAsync(string clientToken, string driverId, string destPath, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url($"/api/client/driver?id={Uri.EscapeDataString(driverId)}"));
            req.Headers.Add("X-Client-Token", clientToken);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                return (false, "", ExtractError(body, res.StatusCode));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using var fs = File.Create(destPath);
            await res.Content.CopyToAsync(fs, ct);
            return (true, destPath, null);
        }
        catch (Exception ex) { return (false, "", ex.Message); }
    }

    public async Task<(bool ok, string current, string? latest, string? url, bool hasUpdate, string? error, IReadOnlyList<GitHubReleaseAsset> assets)> CheckUpdateAsync(CancellationToken ct)
        => await CheckUpdateAsync("bogdan-ralakde/OnlinePrinter", ct);

    public async Task<(bool ok, string current, string? latest, string? url, bool hasUpdate, string? error, IReadOnlyList<GitHubReleaseAsset> assets)> CheckUpdateAsync(string repo, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("OnlinePrinterClient/" + ClientAgent.AppVersion);
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (false, ClientAgent.AppVersion, null, null, false, $"HTTP {(int)res.StatusCode}", Array.Empty<GitHubReleaseAsset>());
            using var doc = JsonDocument.Parse(body);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var url = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            var has = !string.IsNullOrEmpty(tag) && IsNewerVersion(tag, ClientAgent.AppVersion);
            var assets = new List<GitHubReleaseAsset>();
            if (doc.RootElement.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    assets.Add(new GitHubReleaseAsset
                    {
                        Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        BrowserDownloadUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
                        Size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0
                    });
                }
            }
            return (true, ClientAgent.AppVersion, tag, url, has, null, assets);
        }
        catch (Exception ex) { return (false, ClientAgent.AppVersion, null, null, false, ex.Message, Array.Empty<GitHubReleaseAsset>()); }
    }

    public static GitHubReleaseAsset? SelectWindowsClientAsset(IReadOnlyList<GitHubReleaseAsset> assets)
    {
        if (assets == null || assets.Count == 0) return null;
        // Prefer the current Online Printer Client setup package; fall back to older names.
        return assets.FirstOrDefault(a => a.Name.StartsWith("OnlinePrinterClient-", StringComparison.OrdinalIgnoreCase)
                                          && a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                                          && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                                          && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                          && a.Name.Contains("LabelBridge", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => a.Name.Contains("win", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
    }

    public static GitHubReleaseAsset? SelectChecksumAsset(IReadOnlyList<GitHubReleaseAsset> assets)
    {
        if (assets == null || assets.Count == 0) return null;
        return assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => a.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(bool ok, string path, string? error)> DownloadUrlToFileAsync(string url, string destPath, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("OnlinePrinterClient/" + ClientAgent.AppVersion);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return (false, "", $"HTTP {(int)res.StatusCode}");
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using var fs = File.Create(destPath);
            await res.Content.CopyToAsync(fs, ct);
            return (true, destPath, null);
        }
        catch (Exception ex) { return (false, "", ex.Message); }
    }

    public async Task<string?> TryGetExpectedSha256Async(IReadOnlyList<GitHubReleaseAsset> assets, string targetAssetName, CancellationToken ct)
    {
        try
        {
            var sumAsset = SelectChecksumAsset(assets);
            if (sumAsset == null || string.IsNullOrWhiteSpace(sumAsset.BrowserDownloadUrl)) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, sumAsset.BrowserDownloadUrl);
            req.Headers.UserAgent.ParseAdd("OnlinePrinterClient/" + ClientAgent.AppVersion);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length < 64) continue;
                var hash = line[..64];
                if (!hash.All(Uri.IsHexDigit)) continue;
                var rest = line[64..].Trim().TrimStart('*');
                if (rest.EndsWith(targetAssetName, StringComparison.OrdinalIgnoreCase) || rest.Equals(Path.GetFileName(targetAssetName), StringComparison.OrdinalIgnoreCase))
                    return hash.ToLowerInvariant();
            }
        }
        catch { }
        return null;
    }

    public static string NormalizeVer(string? v) => (v ?? "").Trim().TrimStart('v', 'V');

    public static bool IsNewerVersion(string? latestTag, string? current)
    {
        var a = NormalizeVer(latestTag); var b = NormalizeVer(current);
        if (string.IsNullOrEmpty(a)) return false;
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)) return va > vb;
        return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Str(JsonElement obj, string a, string b)
    {
        if (obj.TryGetProperty(a, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        if (obj.TryGetProperty(b, out v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    private static string ExtractError(string body, System.Net.HttpStatusCode code)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? body;
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString() ?? body;
        }
        catch { }
        if (string.IsNullOrWhiteSpace(body)) return $"HTTP {(int)code}";
        return body.Length > 300 ? body[..300] : body;
    }

    public void Dispose() => _http.Dispose();
}
