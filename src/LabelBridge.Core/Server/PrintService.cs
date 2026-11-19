using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LabelBridge.Core;

namespace LabelBridge.Core.Server;

public sealed class PrintService : IDisposable
{
    private readonly ServerStore _store;
    private readonly ServerConfig _config;
    private readonly JobStore _queue;
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    public event Action<string>? Changed;

    public PrintService(ServerStore store, ServerConfig config)
    {
        _store = store;
        _config = config;
        _queue = new JobStore(_store.QueueDir, Math.Max(100, config.MaxJobs));
    }

    public void Start() => _worker = Task.Run(() => WorkerLoop(_cts.Token));

    public (bool ok, string message) TestConnection(string host, int port, int timeoutMs = 5000)
    {
        try
        {
            using var tcp = new TcpClient();
            var task = tcp.ConnectAsync(host, port);
            if (!task.Wait(timeoutMs)) return (false, $"Timeout connecting to {host}:{port}");
            return (true, $"Connected to {host}:{port}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool ok, string message)> SendTestAsync(CancellationToken ct = default)
    {
        var p = _store.State.Printer;
        if (string.IsNullOrWhiteSpace(p.Host)) return (false, "Printer host is not configured.");
        var data = BuildTestLabel(p.Profile);
        try
        {
            await PrinterOutput.SendTcpAsync(p.Host, p.Port, data, ct, 10000);
            return (true, $"Sent test label to {p.Host}:{p.Port}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public byte[] BuildTestLabel(PrinterProfile profile)
    {
        profile ??= new PrinterProfile();
        var lang = (profile.PrintLanguage ?? "").Trim().ToUpperInvariant();
        var name = string.IsNullOrWhiteSpace(profile.Name) ? "LabelBridge" : profile.Name.Trim();
        var size = string.Join(" x ", new[] { profile.Width, profile.Height }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        var dpi = string.IsNullOrWhiteSpace(profile.Dpi) ? "" : profile.Dpi + "dpi";
        var meta = string.Join(" ", new[] { size, dpi, profile.Orientation, profile.MediaType, profile.PrintMethod, profile.Darkness, profile.Speed }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (string.IsNullOrEmpty(lang) || lang.Contains("ZPL"))
        {
            var zpl = $"^XA\n^CI28\n^FO30,30^A0N,50,50^FDLabelBridge Test^FS\n^FO30,100^A0N,30,30^FDProfile: {EscapeZpl(name)}^FS\n^FO30,150^A0N,28,28^FD{EscapeZpl(meta)}^FS\n^FO30,200^A0N,28,28^FD{EscapeZpl(now)}^FS\n^FO30,260^BCN,120,Y,N,N^FDLBTEST^FS\n^XZ\n";
            return Encoding.UTF8.GetBytes(zpl);
        }
        if (lang.Contains("EPL"))
        {
            var epl = $"N\nA30,30,0,4,1,1,N,\"LabelBridge Test\"\nA30,90,0,3,1,1,N,\"Profile: {EscapePlain(name)}\"\nA30,130,0,3,1,1,N,\"{EscapePlain(meta)}\"\nA30,170,0,3,1,1,N,\"{EscapePlain(now)}\"\nB30,220,0,1,2,2,120,B,\"LBTEST\"\nP1\n";
            return Encoding.ASCII.GetBytes(epl);
        }
        if (lang.Contains("TSPL") || lang.Contains("TSC"))
        {
            var w = string.IsNullOrWhiteSpace(profile.Width) ? "100" : profile.Width.Replace("mm","",StringComparison.OrdinalIgnoreCase).Replace("in","",StringComparison.OrdinalIgnoreCase).Trim();
            var h = string.IsNullOrWhiteSpace(profile.Height) ? "60" : profile.Height.Replace("mm","",StringComparison.OrdinalIgnoreCase).Replace("in","",StringComparison.OrdinalIgnoreCase).Trim();
            var tspl = $"SIZE {w} mm,{h} mm\r\nGAP 2 mm,0 mm\r\nCLS\r\nTEXT 30,30,\"3\",0,1,1,\"LabelBridge Test\"\r\nTEXT 30,80,\"2\",0,1,1,\"Profile: {EscapePlain(name)}\"\r\nTEXT 30,120,\"2\",0,1,1,\"{EscapePlain(meta)}\"\r\nTEXT 30,160,\"2\",0,1,1,\"{EscapePlain(now)}\"\r\nBARCODE 30,210,\"128\",100,1,0,2,2,\"LBTEST\"\r\nPRINT 1\r\n";
            return Encoding.ASCII.GetBytes(tspl);
        }
        if (lang.Contains("CPCL"))
        {
            var cpcl = $"! 0 200 200 400 1\r\nTEXT 4 0 30 30 LabelBridge Test\r\nTEXT 2 0 30 80 Profile: {EscapePlain(name)}\r\nTEXT 2 0 30 120 {EscapePlain(meta)}\r\nTEXT 2 0 30 160 {EscapePlain(now)}\r\nBARCODE 128 1 1 80 30 220 LBTEST\r\nFORM\r\nPRINT\r\n";
            return Encoding.ASCII.GetBytes(cpcl);
        }
        if (lang.Contains("ESC") || lang.Contains("POS"))
        {
            var txt = $"\x1B@LabelBridge Test\nProfile: {name}\n{meta}\n{now}\n\n\n\x1DV1"; // init + cut partial
            return Encoding.ASCII.GetBytes(txt);
        }

        var plain = $"LabelBridge Test\r\nProfile: {name}\r\n{meta}\r\n{now}\r\n\r\n";
        return Encoding.UTF8.GetBytes(plain);
    }

    private static string EscapeZpl(string s) => (s ?? "").Replace("^", " ").Replace("~", " ").Replace("\\", " ");
    private static string EscapePlain(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");

    public Task<(bool ok, string message, string? jobId)> EnqueueJobAsync(string clientId, string name, byte[] data)
    {
        if (data == null || data.Length == 0) return Task.FromResult((false, "Empty job.", (string?)null));
        if (data.LongLength > _config.MaxPayloadBytes) return Task.FromResult((false, "Job too large.", (string?)null));

        var kind = PrinterData.Classify(data);
        if (!PrinterData.IsForwardable(kind))
            return Task.FromResult((false, PrinterData.RejectReason(kind), (string?)null));

        var safeName = string.IsNullOrWhiteSpace(name) ? "print job" : name.Trim();
        var desc = PrinterData.Describe(kind);
        if (!safeName.Contains(desc, StringComparison.OrdinalIgnoreCase))
            safeName = $"{safeName} ({desc})";

        var id = Guid.NewGuid().ToString("N");
        var job = new PrintJob { Id = id, Name = safeName, Source = "client:" + clientId, CreatedUtc = DateTime.UtcNow, Data = data };

        if (!PrinterData.IsDirectPrinterRawFriendly(kind))
            Log.Warn($"Accepted job {id} as {desc}; verify printer/profile compatibility for direct raw printing.");

        try
        {
            _queue.Enqueue(job);
            Changed?.Invoke("enqueue");
            return Task.FromResult((true, $"Queued ({desc})", (string?)id));
        }
        catch (Exception ex) { return Task.FromResult((false, ex.Message, (string?)null)); }
    }

    public IEnumerable<object> ListJobs(int max = 100)
    {
        foreach (var j in _queue.Snapshot(max))
            yield return new { j.Id, j.Name, j.Source, j.CreatedUtc, j.Attempts, j.NextAttemptUtc, j.LastError, Size = j.Size, State = j.LastError == null ? "queued" : "retrying" };
    }

    public int QueueCount => _queue.Count;

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var printer = _store.State.Printer;
                if (printer == null || !printer.Enabled || string.IsNullOrWhiteSpace(printer.Host))
                {
                    await Task.Delay(1500, ct);
                    continue;
                }

                if (!_queue.TryPeek(DateTime.UtcNow, out var path, out var job))
                {
                    try { await _queue.Signal.WaitAsync(1500, ct); } catch (OperationCanceledException) { }
                    continue;
                }

                try
                {
                    var kind = PrinterData.Classify(job.Data);
                    if (!PrinterData.IsForwardable(kind))
                        throw new InvalidOperationException(PrinterData.RejectReason(kind));

                    await PrinterOutput.SendTcpAsync(printer.Host, printer.Port, job.Data, ct, 20000);
                    _queue.Remove(path);
                    Log.Info($"Printed job {job.Id} to {printer.Host}:{printer.Port} ({job.Size} bytes, {PrinterData.Describe(kind)})");
                    Changed?.Invoke("printed");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    var delay = Backoff(job.Attempts);
                    _queue.MarkFailed(path, job, ex.Message, delay);
                    Log.Warn($"Print failed for {job.Id}: {ex.Message}; retry in {delay.TotalSeconds:F0}s");
                    Changed?.Invoke("failed");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Warn("Print worker error: " + ex.Message);
                try { await Task.Delay(2000, ct); } catch { }
            }
        }
    }

    private static TimeSpan Backoff(int attempts)
    {
        var secs = Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)));
        return TimeSpan.FromSeconds(Math.Max(2, secs));
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _worker?.Wait(3000); } catch { }
        _cts.Dispose();
    }
}

public sealed class ScanResult { public string host { get; set; } = ""; public int port { get; set; } public bool open { get; set; } }

public static class PrinterDiscovery
{
    public static async Task<List<ScanResult>> ScanAsync(string? subnetCidr, int port = 9100, int timeoutMs = 350, int concurrency = 96, CancellationToken ct = default)
    {
        var targets = BuildTargets(subnetCidr).Distinct().ToList();
        var results = new ConcurrentBag<ScanResult>();
        using var sem = new SemaphoreSlim(concurrency);
        var tasks = targets.Select(async ip =>
        {
            await sem.WaitAsync(ct);
            try { if (await ProbeAsync(ip, port, timeoutMs, ct)) results.Add(new ScanResult { host = ip, port = port, open = true }); }
            catch { }
            finally { sem.Release(); }
        }).ToArray();
        try { await Task.WhenAll(tasks); } catch { }
        return results.OrderBy(r => r.host, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> BuildTargets(string? subnetCidr)
    {
        if (!string.IsNullOrWhiteSpace(subnetCidr) && TryParseCidr(subnetCidr.Trim(), out var baseIp, out var prefix))
        {
            var count = 1 << Math.Max(0, 32 - prefix);
            if (count <= 4096)
            {
                for (int i = 1; i < count - 1; i++) yield return ToIp(baseIp + (uint)i);
                yield break;
            }
        }
        foreach (var ip in LocalSubnets24()) yield return ip;
    }

    private static IEnumerable<string> LocalSubnets24()
    {
        var list = new List<string>();
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var a in host.AddressList.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            {
                var b = a.GetAddressBytes();
                for (int i = 1; i < 255; i++) list.Add($"{b[0]}.{b[1]}.{b[2]}.{i}");
            }
        }
        catch { }
        if (list.Count == 0) for (int i = 1; i < 255; i++) list.Add($"192.168.1.{i}");
        return list.Distinct();
    }

    private static bool TryParseCidr(string cidr, out uint baseIp, out int prefix)
    {
        baseIp = 0; prefix = 24;
        var parts = cidr.Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        if (parts.Length > 1 && !int.TryParse(parts[1], out prefix)) return false;
        prefix = Math.Clamp(prefix, 16, 30);
        var b = ip.GetAddressBytes();
        uint v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        baseIp = v & mask;
        return true;
    }

    private static string ToIp(uint v) => $"{(v >> 24) & 255}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

    private static async Task<bool> ProbeAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }
}
