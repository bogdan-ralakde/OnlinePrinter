using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LabelBridge.Core;

/// <summary>Direct mode: TLS listener on the host, pinned-certificate clients.</summary>
public sealed class DirectServer : IDisposable
{
    private readonly int _port;
    private readonly byte[] _key;
    private readonly Func<PrintJob, Task> _onJob;
    private readonly X509Certificate2 _cert;
    private TcpListener? _listener;

    public DirectServer(int port, string keyText, Func<PrintJob, Task> onJob)
    {
        _port = port;
        _key = Pairing.KeyFromText(keyText);
        if (_key.Length != 32) throw new InvalidOperationException("Direct-mode key is invalid.");
        _onJob = onJob;
        _cert = TlsCert.LoadOrCreate();
    }

    public string Fingerprint => TlsCert.Fingerprint(_cert);
    public bool IsListening => _listener != null;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start(64);
        Log.Info($"Direct TLS listener started on port {_port} (fingerprint {Fingerprint}).");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_listener == null) Start();

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn("Direct listener accept failed: " + ex.Message);
                await Task.Delay(1000, ct).ContinueWith(_ => { });
                continue;
            }

            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }

        Stop();
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                using var ssl = new SslStream(client.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13
                }, timeout.Token);

                var job = await Envelope.ReadAsync(ssl, _key, timeout.Token);

                if (string.IsNullOrWhiteSpace(job.Name)) job.Name = "Remote job";
                if (string.IsNullOrWhiteSpace(job.Source)) job.Source = "direct " + peer;

                await _onJob(job);
                await ssl.WriteAsync(Encoding.ASCII.GetBytes("OK " + job.Id + "\n"), timeout.Token);
                await ssl.FlushAsync(timeout.Token);

                Log.Info($"Direct job {job.Id} accepted from {peer} ({job.Size:N0} bytes).");
            }
            catch (OperationCanceledException)
            {
                Log.Warn($"Direct connection from {peer} timed out.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Direct connection from {peer} rejected: {ex.Message}");
                try
                {
                    if (client.Connected)
                        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("ERR " + ex.Message.Replace('\n', ' ') + "\n"));
                }
                catch { }
            }
        }
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose()
    {
        Stop();
        _cert.Dispose();
    }
}

/// <summary>Direct mode client: pinned TLS + HMAC-authenticated envelope.</summary>
public sealed class DirectSender : IDisposable
{
    private readonly PairInfo _pair;
    private readonly byte[] _key;

    public DirectSender(PairInfo pair)
    {
        _pair = pair;
        _key = Pairing.KeyFromText(pair.Key);
        if (_key.Length != 32) throw new InvalidOperationException("Pairing code has an invalid key.");
    }

    public async Task SendAsync(PrintJob job, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(_pair.Host, _pair.Port, timeout.Token);

        using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert is null) return false;
            var fp = Base64Url.Encode(SHA256.HashData(cert.GetRawCertData()));
            var ok = Pairing.FixedTimeEquals(fp, _pair.Fingerprint);
            if (!ok) Log.Error("Host certificate fingerprint does not match the pairing code - connection refused.");
            return ok;
        });

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = string.IsNullOrWhiteSpace(_pair.Host) ? "localhost" : _pair.Host,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                  System.Security.Authentication.SslProtocols.Tls13
        }, timeout.Token);

        await Envelope.WriteAsync(ssl, job, _key, timeout.Token);

        // read one status line
        var buffer = new byte[512];
        var sb = new StringBuilder();
        while (sb.Length < 512)
        {
            int n = await ssl.ReadAsync(buffer, timeout.Token);
            if (n == 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            if (sb.ToString().Contains('\n')) break;
        }

        var reply = sb.ToString().Trim();
        if (!reply.StartsWith("OK", StringComparison.Ordinal))
            throw new IOException("Host rejected the job: " + (reply.Length == 0 ? "no reply" : reply));
    }

    public void Dispose() { }
}

/// <summary>Shared constants for the relay HTTP protocol.</summary>
public static class RelayApi
{
    public const string TokenHeader = "X-LB-Token";
    public const string AdminHeader = "X-LB-Admin";
    public const string JobIdHeader = "X-LB-Job-Id";
    public const string JobNameHeader = "X-LB-Job-Name";

    public const string ChannelsPath = "/api/v1/channels";
    public const string HealthPath = "/health";

    public static string ChannelUrl(string baseUrl, string channelId) =>
        $"{baseUrl.TrimEnd('/')}{ChannelsPath}/{Uri.EscapeDataString(channelId)}";

    public static string JobsUrl(string baseUrl, string channelId) => ChannelUrl(baseUrl, channelId) + "/jobs";
    public static string NextUrl(string baseUrl, string channelId, int waitSeconds) =>
        JobsUrl(baseUrl, channelId) + $"/next?wait={waitSeconds}";
    public static string AckUrl(string baseUrl, string channelId, string jobId) =>
        JobsUrl(baseUrl, channelId) + "/" + Uri.EscapeDataString(jobId) + "/ack";
    public static string StatusUrl(string baseUrl, string channelId) => ChannelUrl(baseUrl, channelId) + "/status";

    public static string EncodeName(string? name) => Base64Url.Encode(name ?? "");
    public static string DecodeName(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return "";
        try { return Base64Url.DecodeText(encoded); } catch { return ""; }
    }
}

/// <summary>Client side of relay mode: pushes spooled jobs over HTTPS.</summary>
public sealed class RelaySender : IDisposable
{
    private readonly PairInfo _pair;
    private readonly HttpClient _http;

    public RelaySender(PairInfo pair)
    {
        _pair = pair;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90),
            DefaultRequestVersion = HttpVersion.Version11
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LabelBridge/1.0");
    }

    public async Task SendAsync(PrintJob job, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, RelayApi.JobsUrl(_pair.RelayUrl, _pair.ChannelId));
        req.Headers.Add(RelayApi.TokenHeader, _pair.ClientToken);
        req.Headers.Add(RelayApi.JobIdHeader, job.Id);
        req.Headers.Add(RelayApi.JobNameHeader, RelayApi.EncodeName(job.Name));

        var content = new ByteArrayContent(job.Data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content = content;

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new IOException($"Relay rejected the job ({(int)resp.StatusCode}): {Trim(body)}");
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];

    public void Dispose() => _http.Dispose();
}

/// <summary>Host side of relay mode: long-polls the relay for queued jobs.</summary>
public sealed class RelayPoller : IDisposable
{
    private readonly string _relayUrl;
    private readonly string _channelId;
    private readonly string _hostToken;
    private readonly HttpClient _http;

    public RelayPoller(string relayUrl, string channelId, string hostToken)
    {
        _relayUrl = relayUrl.TrimEnd('/');
        _channelId = channelId;
        _hostToken = hostToken;
        // Long-poll + generous slack for slow links.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LabelBridge/1.0");
    }

    public string BaseUrl => _relayUrl;

    /// <summary>Returns the next job, or null when the wait expired.</summary>
    public async Task<PrintJob?> NextAsync(int waitSeconds, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, RelayApi.NextUrl(_relayUrl, _channelId, waitSeconds));
        req.Headers.Add(RelayApi.TokenHeader, _hostToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;

        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new IOException($"Relay poll failed ({(int)resp.StatusCode}): {Trim(text)}");
        }

        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        var id = Header(resp, RelayApi.JobIdHeader);
        var name = RelayApi.DecodeName(Header(resp, RelayApi.JobNameHeader));

        return new PrintJob
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            Name = string.IsNullOrWhiteSpace(name) ? "Relay job" : name,
            Source = "relay",
            Data = data
        };
    }

    public async Task AckAsync(string jobId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, RelayApi.AckUrl(_relayUrl, _channelId, jobId));
        req.Headers.Add(RelayApi.TokenHeader, _hostToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"Relay ack failed ({(int)resp.StatusCode}).");
    }

    public async Task<(int pending, string? oldest)> StatusAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, RelayApi.StatusUrl(_relayUrl, _channelId));
        req.Headers.Add(RelayApi.TokenHeader, _hostToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new IOException($"Relay status failed ({(int)resp.StatusCode}).");

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var pending = doc.RootElement.TryGetProperty("pending", out var p) ? p.GetInt32() : 0;
        var oldest = doc.RootElement.TryGetProperty("oldestUtc", out var o) ? o.GetString() : null;
        return (pending, oldest);
    }

    /// <summary>Creates a channel on the relay and returns the credentials.</summary>
    public static async Task<(string channelId, string hostToken, string clientToken)> CreateChannelAsync(
        string relayUrl, string adminSecret, string name, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var req = new HttpRequestMessage(HttpMethod.Post, relayUrl.TrimEnd('/') + RelayApi.ChannelsPath);
        if (!string.IsNullOrEmpty(adminSecret)) req.Headers.Add(RelayApi.AdminHeader, adminSecret);
        req.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new { name }),
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"Relay refused to create a channel ({(int)resp.StatusCode}): {Trim(body)}");

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return (
            doc.RootElement.GetProperty("channelId").GetString()!,
            doc.RootElement.GetProperty("hostToken").GetString()!,
            doc.RootElement.GetProperty("clientToken").GetString()!);
    }

    public static async Task<string> HealthAsync(string relayUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var resp = await http.GetAsync(relayUrl.TrimEnd('/') + RelayApi.HealthPath, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : $"HTTP {(int)resp.StatusCode}";
    }

    private static string Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "" : "";

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];

    public void Dispose() => _http.Dispose();
}
