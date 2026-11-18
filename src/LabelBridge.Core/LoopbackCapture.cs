using System.Net;
using System.Net.Sockets;

namespace LabelBridge.Core;

/// <summary>
/// Listens on 127.0.0.1 for the virtual printer. Windows' TCP/IP port monitor
/// (installed by LabelBridge) connects here and streams the driver output; each
/// connection is treated as one print job.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private readonly int _port;
    private readonly Func<PrintJob, Task> _onJob;
    private readonly Action<string>? _onProbe;
    private TcpListener? _listener;

    private const int MaxJobBytes = 256 * 1024 * 1024;
    private static readonly TimeSpan IdleFlush = TimeSpan.FromSeconds(20);

    public LoopbackCapture(int port, Func<PrintJob, Task> onJob, Action<string>? onProbe = null)
    {
        _port = port;
        _onJob = onJob;
        _onProbe = onProbe;
    }

    public bool IsListening => _listener != null;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start(64);
        Log.Info($"Virtual printer capture listening on 127.0.0.1:{_port}");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_listener == null) Start();

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Capture listener error", ex);
                await Task.Delay(1000, ct).ContinueWith(_ => { });
                continue;
            }

            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }

        Stop();
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "local";
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var ns = client.GetStream();
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                var lastData = DateTime.UtcNow;

                while (true)
                {
                    if (ct.IsCancellationRequested) break;

                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(IdleFlush);

                    int n;
                    try
                    {
                        n = await ns.ReadAsync(chunk, readCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // port monitor held the connection open without data - flush what we have
                        break;
                    }

                    if (n == 0) break;

                    buffer.Write(chunk, 0, n);
                    lastData = DateTime.UtcNow;

                    if (buffer.Length > MaxJobBytes)
                    {
                        Log.Error($"Print job exceeded {MaxJobBytes / (1024 * 1024)} MB - discarded.");
                        break;
                    }
                }

                var data = buffer.ToArray();
                if (data.Length == 0)
                {
                    Log.Debug("Empty connection from print spooler (status probe).");
                    return;
                }

                if (PrinterData.IsStatusProbe(data))
                {
                    Log.Debug($"Ignored driver status probe ({data.Length} bytes) from {endpoint}.");
                    _onProbe?.Invoke(PrinterData.Preview(data, 200));
                    return;
                }

                var kind = PrinterData.Classify(data);
                if (!PrinterData.IsForwardable(kind))
                {
                    Log.Error($"Spooled data is {PrinterData.Describe(kind)}, not printer language. " +
                              "Check that the LabelBridge printer uses the ZDesigner ZPL driver.");
                    throw new InvalidOperationException(
                        $"The virtual printer produced {PrinterData.Describe(kind)} instead of ZPL. " +
                        "Open LabelBridge > Virtual printer and make sure the ZDesigner ZPL driver is selected.");
                }

                var job = new PrintJob
                {
                    Name = $"Spool {DateTime.Now:HH:mm:ss}",
                    Source = "virtual printer",
                    Data = data
                };

                Log.Info($"Captured print job {job.Id} ({data.Length:N0} bytes, {PrinterData.Describe(kind)}) from {endpoint}.");
                await _onJob(job);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to capture a print job", ex);
            }
        }
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();
}
