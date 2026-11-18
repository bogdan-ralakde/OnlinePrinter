using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LabelBridge.Core;

public enum NoticeKind { Info, Warning, Error, Incoming, Delivered, Failed, State }

public sealed record BridgeNotice(NoticeKind Kind, string Message, PrintJob? Job = null);

/// <summary>
/// The bridge itself, independent of the UI:
///   Host   : network listeners -> durable queue -> real printer
///   Client : virtual printer capture -> durable queue -> relay / direct host
/// </summary>
public sealed class BridgeEngine : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly JobStore _queue;
    private readonly JobStore _history;
    private readonly object _gate = new();
    private readonly List<Task> _tasks = new();
    private readonly Queue<string> _recentIds = new();
    private readonly HashSet<string> _recentSet = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private LoopbackCapture? _capture;
    private DirectServer? _directServer;
    private RelayPoller? _poller;
    private RelaySender? _relaySender;
    private DirectSender? _directSender;
    private string _transportKey = "";
    private string _status = "Stopped";

    public BridgeEngine(AppSettings settings, string? dataRoot = null)
    {
        _settings = settings;
        var root = dataRoot ?? AppPaths.Root;
        _queue = new JobStore(Path.Combine(root, settings.IsHost ? "host-queue" : "client-queue"));
        _history = new JobStore(Path.Combine(root, "history"), 600);
    }

    public event Action<BridgeNotice>? Notice;

    private long _jobsIn;
    private long _jobsOut;
    private long _jobsFailed;

    public AppSettings Settings => _settings;
    public bool IsRunning { get; private set; }
    public string Status { get { lock (_gate) return _status; } }
    public long JobsIn => Interlocked.Read(ref _jobsIn);
    public long JobsOut => Interlocked.Read(ref _jobsOut);
    public long JobsFailed => Interlocked.Read(ref _jobsFailed);
    public int PendingCount => _queue.Count;
    public bool IsDirectListening => _directServer?.IsListening == true;
    public string? DirectFingerprint => _directServer?.Fingerprint;

    public IReadOnlyList<PrintJob> QueueSnapshot(int max = 200) => _queue.Snapshot(max);
    public IReadOnlyList<PrintJob> HistorySnapshot() => _history.Snapshot(60);

    /// <summary>Fetches the stored payload of a queued or archived job (for the preview pane).</summary>
    public byte[]? LoadJobData(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        foreach (var folder in new[] { _queue.Folder, _history.Folder })
        {
            string? match = null;
            try
            {
                match = Directory.EnumerateFiles(folder, "*" + id + "*.job")
                    .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                        .EndsWith(id, StringComparison.OrdinalIgnoreCase));
            }
            catch { }

            if (match == null) continue;

            try
            {
                var job = JobFile.Read(match);
                if (job.Data.Length > 0) return job.Data;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read stored job " + id + ": " + ex.Message);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ life

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _tasks.Clear();

        if (_settings.IsHost)
        {
            if (_settings.DirectServerEnabled)
            {
                _directServer = new DirectServer(_settings.DirectPort, _settings.DirectKey, OnNetworkJobAsync);
                _directServer.Start();
                _tasks.Add(Task.Run(() => _directServer.RunAsync(ct), ct));
            }

            if (_settings.RelayPollEnabled && !string.IsNullOrWhiteSpace(_settings.RelayUrl))
            {
                _poller = new RelayPoller(_settings.RelayUrl, _settings.ChannelId, _settings.HostToken);
                _tasks.Add(Task.Run(() => PollRelayLoopAsync(ct), ct));
            }

            _tasks.Add(Task.Run(() => ForwardLoopAsync(ct), ct));
            SetStatus(_settings.DirectServerEnabled
                ? $"Host ready - TLS port {_settings.DirectPort}, forwarding to {TargetText()}"
                : $"Host ready - forwarding to {TargetText()}");
        }
        else
        {
            _capture = new LoopbackCapture(_settings.CapturePort, OnCapturedAsync, probe => Log.Debug("Driver probe: " + probe));
            _capture.Start();
            _tasks.Add(Task.Run(() => _capture.RunAsync(ct), ct));
            _tasks.Add(Task.Run(() => SendLoopAsync(ct), ct));
            SetStatus($"Client ready - virtual printer on port {_settings.CapturePort}");
        }

        IsRunning = true;
        Log.Info($"Bridge started as {_settings.Role}.");
        Raise(NoticeKind.State, "Started");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning && _cts == null) return;

        try { _cts?.Cancel(); } catch { }
        try { _capture?.Stop(); } catch { }
        try { _directServer?.Stop(); } catch { }

        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(6)); }
        catch { }

        _capture?.Dispose(); _capture = null;
        _directServer?.Dispose(); _directServer = null;
        _poller?.Dispose(); _poller = null;
        _relaySender?.Dispose(); _relaySender = null;
        _directSender?.Dispose(); _directSender = null;
        _transportKey = "";

        _cts?.Dispose();
        _cts = null;
        _tasks.Clear();
        IsRunning = false;
        SetStatus("Stopped");
        Log.Info("Bridge stopped.");
        Raise(NoticeKind.State, "Stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // -------------------------------------------------------------- capturing

    private async Task OnCapturedAsync(PrintJob job)
    {
        Enqueue(job);
        Interlocked.Increment(ref _jobsIn);
        Raise(NoticeKind.Incoming, $"Captured {job.Size:N0} bytes from the virtual printer", job);
        _queue.Wake();
        await Task.CompletedTask;
    }

    private async Task OnNetworkJobAsync(PrintJob job)
    {
        if (IsDuplicate(job.Id))
        {
            Log.Warn($"Duplicate job {job.Id} ignored.");
            await Task.CompletedTask;
            return;
        }

        Enqueue(job);
        Interlocked.Increment(ref _jobsIn);
        Raise(NoticeKind.Incoming, $"Received {job.Size:N0} bytes from {job.Source}", job);
        await Task.CompletedTask;
    }

    private void Enqueue(PrintJob job)
    {
        Remember(job.Id);
        _queue.Enqueue(job);
    }

    private bool IsDuplicate(string id)
    {
        lock (_gate)
        {
            if (_recentSet.Contains(id)) return true;
        }
        return _queue.ContainsId(id) || _history.ContainsId(id);
    }

    private void Remember(string id)
    {
        lock (_gate)
        {
            if (!_recentSet.Add(id)) return;
            _recentIds.Enqueue(id);
            while (_recentIds.Count > 2000)
                _recentSet.Remove(_recentIds.Dequeue());
        }
    }

    // --------------------------------------------------------------- delivery

    private async Task ForwardLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_queue.TryPeek(DateTime.UtcNow, out var path, out var job))
            {
                await WaitAsync(ct);
                continue;
            }

            try
            {
                await DeliverToPrinterAsync(job, ct);
                _queue.Archive(job, _history.Folder);
                _queue.Remove(path);
                Interlocked.Increment(ref _jobsOut);
                _settings.LifetimeJobsSent++;
                SetStatus($"Printed at {DateTime.Now:HH:mm:ss} - {job.Size:N0} bytes");
                Raise(NoticeKind.Delivered, $"Printed {job.Size:N0} bytes to {TargetText()}", job);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _jobsFailed);
                _settings.LifetimeJobsFailed++;
                var delay = Backoff(job.Attempts);
                _queue.MarkFailed(path, job, ex.Message, delay);
                SetStatus($"Printer error - retrying in {delay.TotalSeconds:N0}s: {ex.Message}");
                Raise(NoticeKind.Failed, $"Print failed (retry in {delay.TotalSeconds:N0}s): {ex.Message}", job);
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pair = _settings.Pair;
            if (pair == null || !pair.IsComplete)
            {
                SetStatus("Not paired - paste a pairing code in the Pairing tab");
                await DelayAsync(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            if (!_queue.TryPeek(DateTime.UtcNow, out var path, out var job))
            {
                await WaitAsync(ct);
                continue;
            }

            try
            {
                await DeliverToHostAsync(pair, job, ct);
                _queue.Archive(job, _history.Folder);
                _queue.Remove(path);
                Interlocked.Increment(ref _jobsOut);
                _settings.LifetimeJobsSent++;
                SetStatus($"Delivered at {DateTime.Now:HH:mm:ss} - {job.Size:N0} bytes");
                Raise(NoticeKind.Delivered, $"Delivered {job.Size:N0} bytes to {pair.EndpointText}", job);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _jobsFailed);
                _settings.LifetimeJobsFailed++;

                var rejected = ex.Message.Contains("401", StringComparison.Ordinal) ||
                               ex.Message.Contains("403", StringComparison.Ordinal) ||
                               ex.Message.Contains("pairing", StringComparison.OrdinalIgnoreCase) ||
                               ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase);

                var delay = rejected ? TimeSpan.FromSeconds(60) : Backoff(job.Attempts);
                _queue.MarkFailed(path, job, ex.Message, delay);
                Log.Warn($"Delivery to {pair.EndpointText} failed (attempt {job.Attempts}): {ex.Message}");
                SetStatus(rejected
                    ? "Pairing rejected by the host - import a fresh pairing code"
                    : $"Delivery failed - retrying in {delay.TotalSeconds:N0}s: {ex.Message}");
                Raise(NoticeKind.Failed, $"Delivery failed: {ex.Message}", job);
            }
        }
    }

    private async Task DeliverToPrinterAsync(PrintJob job, CancellationToken ct)
    {
        if (_settings.UseWindowsQueue)
        {
            await Task.Run(() => PrinterOutput.SendToWindowsQueue(_settings.WindowsQueueName, job.Data), ct);
            return;
        }

        await PrinterOutput.SendTcpAsync(_settings.TargetHost, _settings.TargetPort, job.Data, ct);
    }

    private async Task DeliverToHostAsync(PairInfo pair, PrintJob job, CancellationToken ct)
    {
        var key = pair.EndpointText + "|" + pair.ChannelId + "|" + pair.Mode;
        if (_transportKey != key)
        {
            _relaySender?.Dispose();
            _directSender?.Dispose();
            _relaySender = null;
            _directSender = null;

            if (pair.IsRelay) _relaySender = new RelaySender(pair);
            else _directSender = new DirectSender(pair);

            _transportKey = key;
        }

        if (_relaySender != null) await _relaySender.SendAsync(job, ct);
        else if (_directSender != null) await _directSender.SendAsync(job, ct);
        else throw new InvalidOperationException("No transport available for this pairing.");
    }

    private async Task PollRelayLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var job = await _poller!.NextAsync(25, ct);
                failures = 0;

                if (job != null)
                {
                    if (!IsDuplicate(job.Id))
                    {
                        Enqueue(job);
                        Interlocked.Increment(ref _jobsIn);
                        Raise(NoticeKind.Incoming, $"Relay job {job.Size:N0} bytes queued for printing", job);
                    }
                    else
                    {
                        Log.Warn($"Relay re-delivered job {job.Id}; acknowledged without printing twice.");
                    }

                    // Ack as soon as the job is durably queued locally.
                    await _poller.AckAsync(job.Id, ct);
                }

                SetStatus($"Relay connected - {TargetText()}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                SetStatus($"Relay unreachable ({ex.Message}) - retrying");
                if (failures == 1) Raise(NoticeKind.Warning, "Relay connection problem: " + ex.Message);
                var wait = TimeSpan.FromSeconds(Math.Min(60, 5 * failures));
                await DelayAsync(wait, ct);
            }
        }
    }

    // ----------------------------------------------------------------- actions

    /// <summary>Reads a file and queues it (client) or prints it (host).</summary>
    public Task QueueFileAsync(string path, string? name = null)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length == 0) throw new InvalidOperationException("The file is empty.");

        var kind = PrinterData.Classify(data);
        if (!PrinterData.IsForwardable(kind))
            throw new InvalidOperationException($"'{Path.GetFileName(path)}' is {PrinterData.Describe(kind)}, not printer data.");

        var job = new PrintJob
        {
            Name = name ?? Path.GetFileName(path),
            Source = "file import",
            Data = data
        };

        Enqueue(job);
        Interlocked.Increment(ref _jobsIn);
        Raise(NoticeKind.Incoming, $"Imported {job.Size:N0} bytes from {job.Name}", job);
        return Task.CompletedTask;
    }

    /// <summary>Host: sends a sample label straight to the printer.</summary>
    public async Task SendTestLabelAsync(CancellationToken ct = default)
    {
        var data = Encoding.ASCII.GetBytes(SampleZpl());
        var job = new PrintJob { Name = "Test label", Source = "test", Data = data };

        await DeliverToPrinterAsync(job, ct);
        Interlocked.Increment(ref _jobsOut);
        _settings.LifetimeJobsSent++;
        SetStatus($"Test label sent at {DateTime.Now:HH:mm:ss}");
        Raise(NoticeKind.Delivered, $"Test label sent to {TargetText()}", job);
    }

    /// <summary>Host: checks that the configured printer endpoint answers.</summary>
    public async Task<(bool ok, string message)> CheckPrinterAsync(CancellationToken ct = default)
    {
        if (_settings.UseWindowsQueue)
            return (true, $"Using Windows queue '{_settings.WindowsQueueName}'");

        if (string.IsNullOrWhiteSpace(_settings.TargetHost))
            return (false, "No printer address configured.");

        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(_settings.TargetHost, _settings.TargetPort, cts.Token);
            return (true, $"Connected to {_settings.TargetHost}:{_settings.TargetPort}");
        }
        catch (Exception ex)
        {
            return (false, $"Cannot reach {_settings.TargetHost}:{_settings.TargetPort} - {ex.Message}");
        }
    }

    /// <summary>Client: verifies the relay or host is reachable with the current pairing.</summary>
    public async Task<(bool ok, string message)> CheckTransportAsync(CancellationToken ct = default)
    {
        var pair = _settings.Pair;
        if (pair == null || !pair.IsComplete) return (false, "No pairing code imported.");

        try
        {
            if (pair.IsRelay)
            {
                var health = await RelayPoller.HealthAsync(pair.RelayUrl, ct);
                return (true, $"Relay {pair.RelayUrl} answered: {health.Trim()}");
            }

            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(pair.Host, pair.Port, cts.Token);
            return (true, $"Host {pair.Host}:{pair.Port} is reachable.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public string TargetText() => _settings.UseWindowsQueue
        ? $"queue '{_settings.WindowsQueueName}'"
        : $"{_settings.TargetHost}:{_settings.TargetPort}";

    public static string SampleZpl()
    {
        var now = DateTime.Now;
        var code = "LB" + now.ToString("yyMMddHHmmss");
        return "^XA\n" +
               "^CI28\n" +
               "^PW1200\n" +
               "^LL1800\n" +
               "^FO40,40^GB1120,420,5^FS\n" +
               "^FO80,80^A0N,90,90^FDLABELBRIDGE^FS\n" +
               "^FO80,190^A0N,48,48^FDTEST LABEL - PRINT PATH OK^FS\n" +
               "^FO80,270^A0N,38,38^FD" + now.ToString("yyyy-MM-dd HH:mm:ss") + "^FS\n" +
               "^FO80,340^A0N,32,32^FD" + Environment.MachineName + " -> " + "printer" + "^FS\n" +
               "^BY3,3,150\n" +
               "^FO80,520^BCN,170,Y,N,N^FD" + code + "^FS\n" +
               "^FO80,740^A0N,34,34^FD" + code + "^FS\n" +
               "^FO80,830^A0N,30,30^FDPowered by LabelBridge^FS\n" +
               "^XZ\n";
    }

    // ------------------------------------------------------------------ utils

    private TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Pow(2, Math.Min(attempts, 7)); // 1,2,4 ... 128
        return TimeSpan.FromSeconds(Math.Min(_settings.MaxRetrySeconds, seconds));
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        try { await _queue.Signal.WaitAsync(TimeSpan.FromSeconds(1), ct); }
        catch (OperationCanceledException) { }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }

    private void SetStatus(string status)
    {
        bool changed;
        lock (_gate)
        {
            changed = _status != status;
            _status = status;
        }
        if (changed) Raise(NoticeKind.State, status);
    }

    private void Raise(NoticeKind kind, string message, PrintJob? job = null)
    {
        try { Notice?.Invoke(new BridgeNotice(kind, message, job)); }
        catch (Exception ex) { Log.Warn("Notice handler failed: " + ex.Message); }
    }
}
