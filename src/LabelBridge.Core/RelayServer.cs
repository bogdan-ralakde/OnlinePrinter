using System.Net;
using System.Text;
using System.Text.Json;

namespace LabelBridge.Core;

/// <summary>
/// The internet-facing relay. Runs on Windows, Linux or in a container
/// (TrueNAS LXC/Docker). Only needs an HTTP port; terminate TLS in a reverse
/// proxy (Caddy, Traefik, nginx, Cloudflare Tunnel) or expose it directly.
///
/// Protocol (all bodies are raw printer bytes):
///   POST   /api/v1/channels                     -> create channel (admin)
///   POST   /api/v1/channels/{id}/jobs           -> client uploads a job
///   GET    /api/v1/channels/{id}/jobs/next      -> host long-poll
///   POST   /api/v1/channels/{id}/jobs/{job}/ack -> host confirms delivery
///   GET    /api/v1/channels/{id}/status         -> host queue depth
///   DELETE /api/v1/channels/{id}                -> remove channel
///   GET    /health
/// </summary>
public sealed class RelayServer : IDisposable
{
    private readonly RelayOptions _options;
    private readonly ChannelStore _store;
    private readonly HttpListener _listener = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _requests;
    private long _jobsAccepted;

    public RelayServer(RelayOptions options)
    {
        _options = options;
        _store = new ChannelStore(options);
        _listener.Prefixes.Add(options.ListenPrefix.EndsWith('/') ? options.ListenPrefix : options.ListenPrefix + "/");
        _listener.IgnoreWriteExceptions = true;
    }

    public ChannelStore Store => _store;
    public bool IsRunning { get; private set; }
    public string ListenPrefix => _options.ListenPrefix;
    public long JobsAccepted => Interlocked.Read(ref _jobsAccepted);

    public void Start()
    {
        if (IsRunning) return;
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        IsRunning = true;
        Log.Info($"Relay listening on {_options.ListenPrefix} (data: {_options.DataDir})");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        IsRunning = false;
        Log.Info("Relay stopped.");
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                break; // listener stopped
            }

            _ = Task.Run(() => HandleSafeAsync(ctx, ct), ct);
        }
    }

    private async Task HandleSafeAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        try
        {
            await HandleAsync(ctx, ct);
        }
        catch (RelayFullException ex)
        {
            await TryWriteAsync(ctx, 507, "text/plain", Encoding.UTF8.GetBytes(ex.Message));
        }
        catch (Exception ex)
        {
            Log.Error($"Relay request {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath} failed", ex);
            await TryWriteAsync(ctx, 500, "text/plain", Encoding.UTF8.GetBytes("relay error: " + ex.Message));
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var method = req.HttpMethod.ToUpperInvariant();
        var segments = (req.Url?.AbsolutePath ?? "/").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // GET /health
        if (method == "GET" && segments.Length == 1 && segments[0].Equals("health", StringComparison.OrdinalIgnoreCase))
        {
            await WriteText(ctx, 200, $"ok channels={_store.ChannelCount} jobs={JobsAccepted} uptime={Uptime()}");
            return;
        }

        // GET /   (tiny status page - handy on a NAS)
        if (method == "GET" && segments.Length == 0)
        {
            await WriteText(ctx, 200, HtmlStatus(), "text/html");
            return;
        }

        if (segments.Length < 3 || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("channels", StringComparison.OrdinalIgnoreCase))
        {
            await WriteText(ctx, 404, "not found");
            return;
        }

        // POST /api/v1/channels
        if (segments.Length == 3)
        {
            if (method != "POST") { await WriteText(ctx, 405, "method not allowed"); return; }

            if (!string.IsNullOrEmpty(_options.AdminSecret) &&
                !Pairing.FixedTimeEquals(req.Headers[RelayApi.AdminHeader], _options.AdminSecret))
            {
                await WriteText(ctx, 401, "admin secret required");
                return;
            }

            string name = "printer";
            var body = await ReadBodyAsync(req, 64 * 1024, ct);
            if (body.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString() ?? name;
                }
                catch { }
            }

            var created = _store.CreateChannel(name);
            await WriteJson(ctx, 201, new
            {
                channelId = created.channelId,
                hostToken = created.hostToken,
                clientToken = created.clientToken
            });
            return;
        }

        var channelId = segments[3];

        // DELETE /api/v1/channels/{id}
        if (segments.Length == 4)
        {
            if (method != "DELETE") { await WriteText(ctx, 405, "method not allowed"); return; }
            if (!_store.IsHost(channelId, req.Headers[RelayApi.TokenHeader]))
            {
                await WriteText(ctx, 401, "invalid host token");
                return;
            }
            await WriteText(ctx, _store.DeleteChannel(channelId) ? 200 : 404, "deleted");
            return;
        }

        if (segments.Length >= 5 && segments[4].Equals("jobs", StringComparison.OrdinalIgnoreCase))
        {
            // POST /api/v1/channels/{id}/jobs  -> upload
            if (segments.Length == 5 && method == "POST")
            {
                if (!_store.IsClient(channelId, req.Headers[RelayApi.TokenHeader]))
                {
                    await WriteText(ctx, 401, "invalid client token");
                    return;
                }

                var id = req.Headers[RelayApi.JobIdHeader];
                if (!ChannelStore.IsSafeJobId(id)) id = Guid.NewGuid().ToString("N");
                var name = RelayApi.DecodeName(req.Headers[RelayApi.JobNameHeader]);
                if (name.Length > 200) name = name[..200];
                name = name.Replace('\r', ' ').Replace('\n', ' ');

                var data = await ReadBodyAsync(req, _options.MaxPayloadBytes, ct);
                if (data.Length == 0) { await WriteText(ctx, 400, "empty job"); return; }

                var job = new PrintJob
                {
                    Id = id!,
                    Name = string.IsNullOrWhiteSpace(name) ? "Relay job" : name,
                    Source = "relay:" + (req.RemoteEndPoint?.Address.ToString() ?? "?"),
                    Data = data
                };

                _store.Enqueue(channelId, job);
                Interlocked.Increment(ref _jobsAccepted);
                Log.Info($"Relay accepted job {job.Id} on channel {channelId} ({data.Length:N0} bytes).");

                await WriteJson(ctx, 202, new { id = job.Id });
                return;
            }

            // GET /api/v1/channels/{id}/jobs/next?wait=25
            if (segments.Length == 6 && segments[5].Equals("next", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                if (!_store.IsHost(channelId, req.Headers[RelayApi.TokenHeader]))
                {
                    await WriteText(ctx, 401, "invalid host token");
                    return;
                }

                int wait = 25;
                var q = req.QueryString["wait"];
                if (!string.IsNullOrEmpty(q) && int.TryParse(q, out var parsed)) wait = Math.Clamp(parsed, 0, 50);

                var job = await _store.TakeNextAsync(channelId, TimeSpan.FromSeconds(wait), ct);
                if (job == null)
                {
                    await WriteText(ctx, 204, "");
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.AddHeader(RelayApi.JobIdHeader, job.Id);
                ctx.Response.AddHeader(RelayApi.JobNameHeader, RelayApi.EncodeName(job.Name));
                ctx.Response.ContentLength64 = job.Data.Length;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(job.Data, ct);
                }
                catch
                {
                    // Host vanished mid-transfer; put the job back in the queue.
                    _store.Ack(channelId, job.Id);
                    try
                    {
                        var retry = job.CloneShallow();
                        retry.Data = job.Data;
                        _store.Enqueue(channelId, retry);
                    }
                    catch { }
                }
                return;
            }

            // POST /api/v1/channels/{id}/jobs/{jobId}/ack
            if (segments.Length == 7 && segments[6].Equals("ack", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                if (!_store.IsHost(channelId, req.Headers[RelayApi.TokenHeader]))
                {
                    await WriteText(ctx, 401, "invalid host token");
                    return;
                }

                _store.Ack(channelId, segments[5]);
                await WriteText(ctx, 200, "ok");
                return;
            }

            await WriteText(ctx, 404, "not found");
            return;
        }

        // GET /api/v1/channels/{id}/status
        if (segments.Length == 5 && segments[4].Equals("status", StringComparison.OrdinalIgnoreCase) && method == "GET")
        {
            if (!_store.IsHost(channelId, req.Headers[RelayApi.TokenHeader]))
            {
                await WriteText(ctx, 401, "invalid host token");
                return;
            }

            var (pending, oldest) = _store.Status(channelId);
            await WriteJson(ctx, 200, new { pending, oldestUtc = oldest?.ToString("o"), channelId });
            return;
        }

        await WriteText(ctx, 404, "not found");
    }

    // -------------------------------------------------------------- helpers

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest req, int maxBytes, CancellationToken ct)
    {
        if (req.ContentLength64 > maxBytes)
            throw new RelayFullException($"Body exceeds the {maxBytes / (1024 * 1024)} MB limit.");

        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await req.InputStream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > maxBytes)
                throw new RelayFullException($"Body exceeds the {maxBytes / (1024 * 1024)} MB limit.");
        }
        return ms.ToArray();
    }

    private static Task WriteText(HttpListenerContext ctx, int status, string text, string contentType = "text/plain")
        => WriteBytes(ctx, status, contentType, Encoding.UTF8.GetBytes(text ?? ""));

    private static Task WriteJson(HttpListenerContext ctx, int status, object value) =>
        WriteBytes(ctx, status, "application/json", JsonSerializer.SerializeToUtf8Bytes(value));

    private static async Task WriteBytes(HttpListenerContext ctx, int status, string contentType, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = body.Length;
        if (body.Length > 0) await ctx.Response.OutputStream.WriteAsync(body);
    }

    private static async Task TryWriteAsync(HttpListenerContext ctx, int status, string contentType, byte[] body)
    {
        try
        {
            if (ctx.Response.OutputStream.CanWrite) await WriteBytes(ctx, status, contentType, body);
        }
        catch { }
    }

    private string Uptime()
    {
        var span = DateTime.UtcNow - _startedUtc;
        return $"{(int)span.TotalHours}h{span.Minutes:00}m";
    }

    private string HtmlStatus() => $$"""
<!doctype html>
<html><head><meta charset="utf-8"><title>LabelBridge relay</title>
<style>
 body{font-family:Segoe UI,system-ui,sans-serif;background:#0f1420;color:#e8edf5;margin:0;padding:48px;display:flex;justify-content:center}
 .card{background:#161d2c;border:1px solid #263149;border-radius:14px;padding:28px 34px;max-width:560px;width:100%}
 h1{margin:0 0 4px;font-size:22px} .sub{color:#8fa1bd;margin-bottom:24px;font-size:13px}
 .row{display:flex;justify-content:space-between;padding:10px 0;border-bottom:1px solid #222c42;font-size:14px}
 .row:last-child{border:0} .v{font-family:Consolas,monospace;color:#7fd4a5}
</style></head><body><div class="card">
 <h1>LabelBridge relay</h1><div class="sub">Print job relay - online</div>
 <div class="row"><span>Channels</span><span class="v">{{_store.ChannelCount}}</span></div>
 <div class="row"><span>Jobs accepted</span><span class="v">{{JobsAccepted}}</span></div>
 <div class="row"><span>Requests</span><span class="v">{{Interlocked.Read(ref _requests)}}</span></div>
 <div class="row"><span>Uptime</span><span class="v">{{Uptime()}}</span></div>
 <div class="row"><span>Data directory</span><span class="v">{{WebUtility.HtmlEncode(Path.GetFullPath(_options.DataDir))}}</span></div>
</div></body></html>
""";
}
