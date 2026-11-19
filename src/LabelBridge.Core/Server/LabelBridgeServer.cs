using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LabelBridge.Core;

namespace LabelBridge.Core.Server;

public sealed class LabelBridgeServer : IDisposable
{
    private readonly ServerConfig _config;
    private readonly ServerStore _store;
    private readonly HttpListener _listener = new();
    private readonly PrintService _printService;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LabelBridgeServer(ServerConfig config)
    {
        _config = config;
        _store = new ServerStore(config.DataDir);
        _store.EnsureAdmin(config.InitialAdminSecret);
        _printService = new PrintService(_store, config);
    }

    public ServerStore Store => _store;
    public PrintService PrintService => _printService;
    public ServerConfig Config => _config;

    public void Start()
    {
        if (!_config.ListenPrefix.EndsWith('/')) _config.ListenPrefix += '/';
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(_config.ListenPrefix);
        _listener.Start();
        _printService.Start();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info($"LabelBridge server listening on {_config.ListenPrefix}");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _loop?.Wait(3000); } catch { }
        _printService.Dispose();
    }

    public void Dispose() => Stop();

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleAsync(ctx), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                Log.Warn("Listener error: " + ex.Message);
                try { ctx?.Response.Close(); } catch { }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers["Server"] = "LabelBridge";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod.ToUpperInvariant();

            // CORS (allow client apps) - simple
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Client-Token, X-LB-Admin";
            res.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS";
            if (method == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

            if (path == "/health") { await WriteJson(res, 200, new { ok = true, name = "LabelBridge", version = _config.CurrentVersion, time = DateTime.UtcNow }); return; }
            if (path == "/" || path.StartsWith("/app")) { await ServeWeb(res, path); return; }
            if (path.StartsWith("/api/")) { await HandleApi(req, res, path, method); return; }

            res.StatusCode = 404; await WriteText(res, "Not found"); 
        }
        catch (Exception ex)
        {
            Log.Warn("Request error: " + ex.Message);
            try { res.StatusCode = 500; await WriteJson(res, 500, new { error = ex.Message }); } catch { }
        }
        finally { try { res.Close(); } catch { } }
    }
    private async Task ServeWeb(HttpListenerResponse res, string path)
    {
        var html = GetWebIndexHtml();
        res.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private async Task HandleApi(HttpListenerRequest req, HttpListenerResponse res, string path, string method)
    {
        // Public auth endpoints
        if (path == "/api/auth/login" && method == "POST") { await ApiLogin(req, res); return; }
        if (path == "/api/auth/logout" && method == "POST") { _store.Logout(GetBearer(req)); res.StatusCode = 204; return; }
        if (path == "/api/auth/me" && method == "GET") { await ApiMe(req, res); return; }

        // Client registration/pairing requests (public, then approved)
        if (path == "/api/client/register" && method == "POST") { await ApiClientRegister(req, res); return; }
        if (path == "/api/client/status" && method == "GET") { await ApiClientStatus(req, res); return; }
        if (path == "/api/client/claim" && method == "GET") { await ApiClientClaim(req, res); return; }
        if (path == "/api/client/config" && method == "GET") { await ApiClientConfig(req, res); return; }
        if (path == "/api/client/driver" && method == "GET") { await ApiClientDriverDownload(req, res); return; }
        if (path == "/api/client/jobs" && method == "POST") { await ApiClientUploadJob(req, res); return; }

        // Admin APIs require session
        if (!IsAuthed(req)) { await WriteJson(res, 401, new { error = "Unauthorized" }); return; }

        if (path == "/api/admin/setup-status" && method == "GET") { await WriteJson(res, 200, new { setupComplete = _store.State.SetupComplete, username = _store.State.Admin.Username }); return; }
        if (path == "/api/admin/change-password" && method == "POST") { await ApiChangePassword(req, res); return; }

        // Printer config
        if (path == "/api/admin/printer" && method == "GET") { await WriteJson(res, 200, _store.State.Printer); return; }
        if (path == "/api/admin/printer" && method == "PUT") { await ApiSetPrinter(req, res); return; }
        if (path == "/api/admin/printer/test" && method == "POST") { var r = await _printService.SendTestAsync(); await WriteJson(res, r.ok ? 200 : 400, new { ok = r.ok, message = r.message }); return; }
        if (path == "/api/admin/printer/probe" && method == "POST") { await ApiProbePrinter(req, res); return; }
        if (path.StartsWith("/api/admin/scan") && method == "GET") { await ApiScan(req, res); return; }

        // Clients
        if (path == "/api/admin/clients" && method == "GET") { await WriteJson(res, 200, _store.State.Clients.OrderByDescending(c=>c.CreatedUtc).ToList()); return; }
        if (path.StartsWith("/api/admin/clients/") && method == "POST")
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                var id = parts[3];
                var action = parts.Length >= 5 ? parts[4] : "";
                if (action == "approve") { var (c,tok) = _store.ApproveClient(id); await WriteJson(res, c==null?404:200, c==null?new{error="not found"}:new{ok=true, client=c, token=tok}); return; }
                if (action == "revoke") { await WriteJson(res, _store.SetClientStatus(id, ClientStatus.Revoked)?200:404, new{ok=_store.SetClientStatus(id, ClientStatus.Revoked)}); return; }
                if (action == "decline") { await WriteJson(res, _store.SetClientStatus(id, ClientStatus.Declined)?200:404, new{ok=_store.SetClientStatus(id, ClientStatus.Declined)}); return; }
            }
        }
        if (path.StartsWith("/api/admin/clients/") && method == "DELETE")
        {
            var id = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            await WriteJson(res, _store.RemoveClient(id)?200:404, new{ok=_store.RemoveClient(id)}); return;
        }

        // Jobs
        if (path == "/api/admin/jobs" && method == "GET") { await WriteJson(res, 200, _printService.ListJobs(200).ToList()); return; }

        // Drivers
        if (path == "/api/admin/drivers" && method == "GET") { await WriteJson(res, 200, _store.State.Drivers); return; }
        if (path == "/api/admin/drivers/select" && method == "POST") { await ApiSelectDriver(req, res); return; }
        if (path.StartsWith("/api/admin/drivers/") && method == "DELETE")
        {
            var id = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            await WriteJson(res, _store.RemoveDriver(id)?200:404, new{ok=_store.RemoveDriver(id)}); return;
        }
        if (path == "/api/admin/drivers/upload" && method == "POST") { await ApiUploadDriver(req, res); return; }

        // Update info/apply (GitHub releases)
        if (path == "/api/admin/update-info" && method == "GET") { await ApiUpdateInfo(req, res); return; }
        if (path == "/api/admin/update-apply" && method == "POST") { await ApiUpdateApply(req, res); return; }

        // Server profile for client download info
        if (path == "/api/admin/client-bundle-info" && method == "GET") { await ApiClientBundleInfo(req, res); return; }

        await WriteJson(res, 404, new { error = "Not found" });
    }
    private bool IsAuthed(HttpListenerRequest req) => _store.ValidateSession(GetBearer(req));
    private static string? GetBearer(HttpListenerRequest req)
    {
        var auth = req.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        var cookie = req.Headers["Cookie"];
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            foreach (var part in cookie.Split(';'))
            {
                var kv = part.Trim().Split('=',2);
                if (kv.Length==2 && kv[0]=="lb_session") return Uri.UnescapeDataString(kv[1]);
            }
        }
        return null;
    }

    private async Task ApiLogin(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync<JsonElement>(req);
        var user = body.TryGetProperty("username", out var u)?u.GetString():"";
        var pass = body.TryGetProperty("password", out var p)?p.GetString():"";
        var (ok, token, err) = _store.Login(user??"", pass??"");
        if (!ok) { await WriteJson(res, 401, new { error = err }); return; }
        res.Headers["Set-Cookie"] = $"lb_session={Uri.EscapeDataString(token!)}; Path=/; HttpOnly; SameSite=Lax";
        await WriteJson(res, 200, new { ok=true, token, mustChange=_store.State.Admin.MustChangePassword });
    }

    private async Task ApiMe(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!IsAuthed(req)) { await WriteJson(res, 401, new { authenticated=false }); return; }
        await WriteJson(res, 200, new { authenticated=true, username=_store.State.Admin.Username, mustChange=_store.State.Admin.MustChangePassword, setupComplete=_store.State.SetupComplete, version=_config.CurrentVersion });
    }

    private async Task ApiChangePassword(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync<JsonElement>(req);
        var current = body.TryGetProperty("current", out var c)?c.GetString():"";
        var next = body.TryGetProperty("next", out var n)?n.GetString():"";
        if (string.IsNullOrWhiteSpace(next) || next.Length < 6) { await WriteJson(res, 400, new { error="Password must be at least 6 characters." }); return; }
        var ok = _store.ChangePassword(current??"", next);
        await WriteJson(res, ok?200:400, new { ok, error = ok?null:"Current password is incorrect." });
    }

    private async Task ApiClientRegister(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync<JsonElement>(req);
        var name = body.TryGetProperty("name", out var n)?n.GetString():"Client";
        var machine = body.TryGetProperty("machine", out var m)?m.GetString():Environment.MachineName;
        var c = _store.AddPendingClient(name??"Client", machine??"");
        await WriteJson(res, 200, new { ok=true, requestId=c.Id, status=c.Status, message="Request submitted. Wait for admin approval." });
    }

    private async Task ApiClientStatus(HttpListenerRequest req, HttpListenerResponse res)
    {
        var requestId = req.QueryString["requestId"] ?? "";
        var client = _store.GetClient(requestId);
        if (client == null) { await WriteJson(res, 404, new { error="Not found" }); return; }
        await WriteJson(res, 200, new { status=client.Status, name=client.Name, canClaim=!string.IsNullOrEmpty(client.PendingToken) && client.Status==nameof(ClientStatus.Approved) });
    }

    private async Task ApiClientClaim(HttpListenerRequest req, HttpListenerResponse res)
    {
        var requestId = req.QueryString["requestId"] ?? "";
        var client = _store.GetClient(requestId);
        if (client == null) { await WriteJson(res, 404, new { error="Not found" }); return; }
        var token = _store.ClaimClientToken(requestId);
        if (string.IsNullOrEmpty(token))
        {
            await WriteJson(res, 409, new { error="Token not available. It may have been claimed already or client not approved.", status=client.Status });
            return;
        }
        await WriteJson(res, 200, new { ok=true, token, status=client.Status, name=client.Name });
    }

    private async Task ApiClientConfig(HttpListenerRequest req, HttpListenerResponse res)
    {
        var token = req.Headers["X-Client-Token"] ?? req.QueryString["token"] ?? "";
        var client = _store.ValidateClientToken(token);
        if (client == null) { await WriteJson(res, 401, new { error="Invalid or not approved client token." }); return; }
        var p = _store.State.Printer;
        await WriteJson(res, 200, new {
            ok = true,
            printer = new { p.Name, p.Host, p.Port, p.Enabled, profile = p.Profile },
            server = new { version = _config.CurrentVersion, repo = _config.Repo },
            driver = GetSelectedDriverInfo(),
            clientBundle = new { latestClientAssetHint = "LabelBridge-*-win-x64.zip", repo = _config.Repo }
        });
    }

    private object? GetSelectedDriverInfo()
    {
        if (string.IsNullOrEmpty(_store.State.SelectedDriverId)) return null;
        var d = _store.GetDriver(_store.State.SelectedDriverId);
        if (d == null) return null;
        return new { d.Id, d.DisplayName, d.Version, d.FileName, d.Size, d.Sha256, d.SilentArgs, downloadUrl = $"/api/client/driver?id={d.Id}" };
    }

    private async Task ApiClientDriverDownload(HttpListenerRequest req, HttpListenerResponse res)
    {
        var token = req.Headers["X-Client-Token"] ?? req.QueryString["token"] ?? "";
        var client = _store.ValidateClientToken(token);
        if (client == null) { res.StatusCode = 401; await WriteText(res, "Unauthorized"); return; }
        var id = req.QueryString["id"];
        DriverPackage? d = null;
        if (!string.IsNullOrWhiteSpace(id)) d = _store.GetDriver(id);
        if (d == null && !string.IsNullOrWhiteSpace(_store.State.SelectedDriverId)) d = _store.GetDriver(_store.State.SelectedDriverId);
        if (d == null) { res.StatusCode = 404; await WriteText(res, "No driver available"); return; }
        var path = Path.Combine(_store.DriversDir, d.FileName);
        if (!File.Exists(path)) { res.StatusCode = 404; await WriteText(res, "Driver file missing"); return; }
        res.ContentType = "application/octet-stream";
        res.Headers["Content-Disposition"] = $"attachment; filename=\"{Sanitize(d.DisplayName)}\"";
        var bytes = await File.ReadAllBytesAsync(path);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private static string Sanitize(string name)
    {
        var s = new string((name ?? "driver").Where(c => !char.IsControl(c) && c != '"' && c != '\\').ToArray());
        return string.IsNullOrWhiteSpace(s) ? "driver" : s;
    }

    private async Task ApiClientUploadJob(HttpListenerRequest req, HttpListenerResponse res)
    {
        var token = req.Headers["X-Client-Token"] ?? "";
        var client = _store.ValidateClientToken(token);
        if (client == null) { await WriteJson(res, 401, new { error="Unauthorized" }); return; }
        var nameRaw = req.Headers["X-Job-Name"] ?? "Print job";
        var name = nameRaw;
        try
        {
            // client may send base64-encoded UTF8 name
            var bytes = Convert.FromBase64String(nameRaw);
            var decoded = Encoding.UTF8.GetString(bytes);
            if (!string.IsNullOrWhiteSpace(decoded)) name = decoded;
        }
        catch { }
        using var ms = new MemoryStream();
        await req.InputStream.CopyToAsync(ms);
        var data = ms.ToArray();
        var (ok, msg, jobId) = await _printService.EnqueueJobAsync(client.Id, name, data);
        await WriteJson(res, ok?200:400, new { ok, message=msg, jobId });
    }

    private async Task ApiSetPrinter(HttpListenerRequest req, HttpListenerResponse res)
    {
        try
        {
            var body = await ReadJsonAsync<JsonElement>(req);
            var p = _store.State.Printer;
            if (body.TryGetProperty("name", out var n)) p.Name = n.GetString() ?? p.Name;
            if (body.TryGetProperty("host", out var h)) p.Host = h.GetString() ?? p.Host;
            if (body.TryGetProperty("port", out var pt) && pt.TryGetInt32(out var pi)) p.Port = pi;
            if (body.TryGetProperty("enabled", out var en)) p.Enabled = en.GetBoolean();
            if (body.TryGetProperty("profile", out var pr) && pr.ValueKind == JsonValueKind.Object)
            {
                var prof = p.Profile ??= new PrinterProfile();
                if (pr.TryGetProperty("name", out var pn)) prof.Name = pn.GetString() ?? prof.Name;
                if (pr.TryGetProperty("width", out var w)) prof.Width = w.GetString() ?? prof.Width;
                if (pr.TryGetProperty("height", out var hh)) prof.Height = hh.GetString() ?? prof.Height;
                if (pr.TryGetProperty("dpi", out var dp)) prof.Dpi = dp.GetString() ?? prof.Dpi;
                if (pr.TryGetProperty("orientation", out var or)) prof.Orientation = or.GetString() ?? prof.Orientation;
                if (pr.TryGetProperty("mediaType", out var mt)) prof.MediaType = mt.GetString() ?? prof.MediaType;
                if (pr.TryGetProperty("printMethod", out var pm)) prof.PrintMethod = pm.GetString() ?? prof.PrintMethod;
                if (pr.TryGetProperty("darkness", out var dk)) prof.Darkness = dk.GetString() ?? prof.Darkness;
                if (pr.TryGetProperty("speed", out var sp)) prof.Speed = sp.GetString() ?? prof.Speed;
                if (pr.TryGetProperty("driverHint", out var dh)) prof.DriverHint = dh.GetString() ?? prof.DriverHint;
                if (pr.TryGetProperty("printLanguage", out var pl)) prof.PrintLanguage = pl.GetString() ?? prof.PrintLanguage;
                if (pr.TryGetProperty("extra", out var ex) && ex.ValueKind == JsonValueKind.Object)
                {
                    prof.Extra ??= new(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in ex.EnumerateObject()) prof.Extra[prop.Name] = prop.Value.ToString();
                }
            }
            _store.Save();
            await WriteJson(res, 200, new { ok=true, printer=p });
        }
        catch (Exception ex) { await WriteJson(res, 400, new { error=ex.Message }); }
    }

    private async Task ApiProbePrinter(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync<JsonElement>(req);
        var host = body.TryGetProperty("host", out var h)?h.GetString():"";
        var port = body.TryGetProperty("port", out var p)&&p.TryGetInt32(out var pv)?pv:9100;
        if (string.IsNullOrWhiteSpace(host)) { await WriteJson(res, 400, new { ok=false, message="host required" }); return; }
        var r = _printService.TestConnection(host, port);
        await WriteJson(res, r.ok?200:400, new { ok=r.ok, message=r.message });
    }

    private async Task ApiScan(HttpListenerRequest req, HttpListenerResponse res)
    {
        var subnet = req.QueryString["subnet"];
        var port = int.TryParse(req.QueryString["port"], out var pp)?pp:9100;
        var found = await PrinterDiscovery.ScanAsync(subnet, port, 300, 120, _cts?.Token ?? default);
        await WriteJson(res, 200, new { ok=true, port, results=found });
    }

    private async Task ApiSelectDriver(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadJsonAsync<JsonElement>(req);
        var id = body.TryGetProperty("id", out var i)?i.GetString():null;
        if (!string.IsNullOrWhiteSpace(id) && _store.GetDriver(id)==null) { await WriteJson(res, 404, new { error="Driver not found" }); return; }
        _store.State.SelectedDriverId = string.IsNullOrWhiteSpace(id)?null:id;
        _store.Save();
        await WriteJson(res, 200, new { ok=true, selectedDriverId=_store.State.SelectedDriverId });
    }

    private async Task ApiUploadDriver(HttpListenerRequest req, HttpListenerResponse res)
    {
        // Expect multipart/form-data or raw with headers; keep simple: accept JSON base64 for now? Or handle multipart.
        // For simplicity, support application/octet-stream with headers: X-File-Name, X-Display-Name, X-Version, X-Silent-Args
        var ct = req.ContentType ?? "";
        if (ct.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("application/x-msdownload", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ct))
        {
            using var ms = new MemoryStream();
            await req.InputStream.CopyToAsync(ms);
            var data = ms.ToArray();
            var fileName = req.Headers["X-File-Name"] ?? "driver.bin";
            var display = req.Headers["X-Display-Name"] ?? fileName;
            var version = req.Headers["X-Version"] ?? "";
            var silent = req.Headers["X-Silent-Args"];
            var notes = req.Headers["X-Notes"];
            var pkg = _store.AddDriver(fileName, display, version, data, silent, notes);
            await WriteJson(res, 200, new { ok=true, driver=pkg });
            return;
        }
        await WriteJson(res, 400, new { error="Use application/octet-stream with X-File-Name header." });
    }

    private async Task ApiUpdateInfo(HttpListenerRequest req, HttpListenerResponse res)
    {
        try
        {
            var info = await FetchReleaseInfoAsync();
            await WriteJson(res, 200, info);
        }
        catch (Exception ex)
        {
            await WriteJson(res, 200, new { ok = false, current = _config.CurrentVersion, error = ex.Message });
        }
    }

    private async Task<object> FetchReleaseInfoAsync()
    {
        using var http = CreateGitHubHttp();
        var url = $"https://api.github.com/repos/{_config.Repo}/releases/latest";
        var json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var htmlUrl = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        var assets = new List<object>();
        if (doc.RootElement.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                assets.Add(new
                {
                    name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
                    size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0
                });
            }
        }
        var hasUpdate = !string.IsNullOrEmpty(tag) && !SameVersion(tag, _config.CurrentVersion);
        return new { ok = true, current = _config.CurrentVersion, latest = tag, url = htmlUrl, hasUpdate, assets };
    }

    private static bool SameVersion(string a, string b)
    {
        static string N(string? v) => (v ?? "").Trim().TrimStart('v', 'V');
        var aa = N(a); var bb = N(b);
        if (Version.TryParse(aa, out var va) && Version.TryParse(bb, out var vb)) return va == vb;
        return string.Equals(aa, bb, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateGitHubHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LabelBridgeServer/1.0");
        return http;
    }

    private async Task ApiUpdateApply(HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!OperatingSystem.IsLinux())
        {
            await WriteJson(res, 400, new { ok = false, error = "Server auto-apply is supported only on Linux/systemd installs. Download the latest release and update manually." });
            return;
        }
        try
        {
            var (ok, message, details) = await TryApplyServerUpdateAsync();
            await WriteJson(res, ok ? 200 : 500, new { ok, message, details });
        }
        catch (Exception ex)
        {
            await WriteJson(res, 500, new { ok = false, error = ex.Message });
        }
    }

    private async Task<(bool ok, string message, object? details)> TryApplyServerUpdateAsync()
    {
        using var http = CreateGitHubHttp();
        var releaseJson = await http.GetStringAsync($"https://api.github.com/repos/{_config.Repo}/releases/latest");
        using var doc = JsonDocument.Parse(releaseJson);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(tag)) return (false, "Could not determine latest release tag.", null);

        string? assetUrl = null; string assetName = ""; long assetSize = 0;
        string? checksumUrl = null; string checksumName = "";
        if (doc.RootElement.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                if (name.Equals("labelbridge-relay-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase) ||
                    (name.Contains("linux-x64", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)))
                { assetUrl = url; assetName = name; assetSize = size; }
                if (name.Contains("sha256", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) || name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) || name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                { checksumUrl = url; checksumName = name; }
            }
        }
        if (string.IsNullOrWhiteSpace(assetUrl)) return (false, "Latest release does not contain labelbridge-relay-linux-x64.tar.gz.", new { latest = tag });

        var tempRoot = Path.Combine(_config.DataDir, "updates");
        Directory.CreateDirectory(tempRoot);
        var work = Path.Combine(tempRoot, "apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archivePath = Path.Combine(work, string.IsNullOrWhiteSpace(assetName) ? "server.tar.gz" : assetName);
            await DownloadToFileAsync(http, assetUrl, archivePath);

            string? expectedHash = null;
            if (!string.IsNullOrWhiteSpace(checksumUrl))
            {
                try
                {
                    var sums = await http.GetStringAsync(checksumUrl);
                    expectedHash = ParseSha256ForAsset(sums, assetName);
                }
                catch { expectedHash = null; }
            }
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(archivePath))).ToLowerInvariant();
                if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    return (false, "Downloaded update checksum verification failed.", new { latest = tag, asset = assetName, checksumAsset = checksumName });
            }

            var extractDir = Path.Combine(work, "extract");
            Directory.CreateDirectory(extractDir);
            if (!TryExtractTarGz(archivePath, extractDir, out var extractErr))
                return (false, "Failed to extract update archive: " + extractErr, new { latest = tag, asset = assetName, manual = ManualUpgradeCommand(tag) });

            var newBin = Directory.EnumerateFiles(extractDir, "LabelBridge.Relay", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(newBin) || !File.Exists(newBin))
                return (false, "Update archive did not contain LabelBridge.Relay.", new { latest = tag, asset = assetName, manual = ManualUpgradeCommand(tag) });

            var target = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
                target = Path.Combine(AppContext.BaseDirectory, "LabelBridge.Relay");
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
                return (false, "Could not determine installed server binary path for in-place update.", new { latest = tag, manual = ManualUpgradeCommand(tag) });

            // Try in-place replace if writable (best-effort; systemd install usually requires root privileges).
            try
            {
                var tmpTarget = target + ".new";
                File.Copy(newBin, tmpTarget, true);
                try { if (OperatingSystem.IsLinux()) File.SetUnixFileMode(tmpTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); } catch { }
                File.Move(tmpTarget, target, true);
            }
            catch (Exception ex)
            {
                return (false, "Downloaded and verified update but could not replace the installed binary (insufficient permissions). Run the manual upgrade command on the server.", new { latest = tag, asset = assetName, target, verifiedChecksum = !string.IsNullOrWhiteSpace(expectedHash), manual = ManualUpgradeCommand(tag), detail = ex.Message });
            }

            // Try to restart service; if not possible, ask operator to restart. New binary is in place.
            try
            {
                var psi = new ProcessStartInfo("systemctl", "restart labelbridge-relay") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch { }

            _ = Task.Run(async () => { await Task.Delay(1500); try { Environment.Exit(0); } catch { } });
            return (true, $"Update {tag} applied. Service restart requested (systemd Restart=always will recover if needed).", new { latest = tag, asset = assetName, verifiedChecksum = !string.IsNullOrWhiteSpace(expectedHash), target });
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static string ManualUpgradeCommand(string tag)
        => "curl -fsSL https://raw.githubusercontent.com/bogdan-ralakde/OnlinePrinter/master/scripts/install-lxc.sh | sudo LABELBRIDGE_VERSION=" + tag + " bash";

    private static async Task DownloadToFileAsync(HttpClient http, string url, string dest)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await res.Content.CopyToAsync(fs);
    }

    private static string? ParseSha256ForAsset(string sumsText, string assetName)
    {
        if (string.IsNullOrWhiteSpace(sumsText) || string.IsNullOrWhiteSpace(assetName)) return null;
        foreach (var raw in sumsText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length < 64) continue;
            var hash = line[..64];
            if (!hash.All(c => Uri.IsHexDigit(c))) continue;
            var rest = line[64..].Trim().TrimStart('*');
            if (rest.EndsWith(assetName, StringComparison.OrdinalIgnoreCase) || rest.Equals(Path.GetFileName(assetName), StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    private static bool TryExtractTarGz(string archivePath, string destDir, out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo("tar", $"-xzf \"{archivePath}\" -C \"{destDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) { error = "Could not start tar."; return false; }
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            if (p.ExitCode != 0) { error = string.IsNullOrWhiteSpace(stderr) ? ("tar exit " + p.ExitCode) : stderr.Trim(); return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private async Task ApiClientBundleInfo(HttpListenerRequest req, HttpListenerResponse res)
    {
        var baseUrl = $"{req.Url?.Scheme}://{req.Url?.Host}:{req.Url?.Port}";
        await WriteJson(res, 200, new {
            ok=true,
            serverBaseUrl = baseUrl,
            repo = _config.Repo,
            version = _config.CurrentVersion,
            instructions = "Install LabelBridge Windows client from releases, then register and wait for approval. The client will download the approved driver/profile from this server."
        });
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        var s = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(s)) return default;
        return JsonSerializer.Deserialize<T>(s, AppSettings.JsonOpts);
    }

    private static async Task WriteJson(HttpListenerResponse res, int status, object obj)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj, AppSettings.JsonOpts));
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteText(HttpListenerResponse res, string text)
    {
        res.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private static string GetWebIndexHtml() => WebUI.IndexHtml;
}
