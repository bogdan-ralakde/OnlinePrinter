using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LabelBridge.Core;

namespace LabelBridge.Services;

/// <summary>
/// End-to-end verification with no GUI and no hardware: a fake printer, a real
/// relay, a host engine and a client engine in one process.
/// </summary>
internal static class SelfTest
{
    private static int _failures;
    private static int _passes;

    public static int Run(string[] args)
    {
        Log.Minimum = LogLevel.Warn;
        Console.WriteLine("LabelBridge self-test");
        Console.WriteLine("=====================");

        var root = Path.Combine(Path.GetTempPath(), "labelbridge-selftest-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(root);
            TestPairing();
            TestJobFile(root);
            TestClassifier();
            TestRelayEndToEnd(root).GetAwaiter().GetResult();
            TestDirectEndToEnd(root).GetAwaiter().GetResult();
            TestDurability(root).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Fail("unexpected exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"Passed: {_passes}   Failed: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- tests

    private static void TestPairing()
    {
        try
        {
            var pair = new PairInfo
            {
                Mode = "relay",
                RelayUrl = "https://print.example.com",
                ChannelId = "abc123",
                ClientToken = "secret-token",
                PrinterName = "LabelBridge",
                Label = "Zebra ZD420 – Büro"
            };

            var code = Pairing.Encode(pair);
            var back = Pairing.TryDecode(code);

            if (back == null) throw new Exception("decode returned null");
            if (back.ChannelId != pair.ChannelId || back.ClientToken != pair.ClientToken) throw new Exception("values differ");
            if (back.Label != pair.Label) throw new Exception("unicode label lost");
            if (!back.IsComplete) throw new Exception("pair marked incomplete");

            Pass("pairing code round-trip");
        }
        catch (Exception ex) { Fail("pairing code round-trip: " + ex.Message); }
    }

    private static void TestJobFile(string root)
    {
        try
        {
            var job = new PrintJob { Name = "job one", Source = "selftest", Data = Encoding.ASCII.GetBytes("^XA^FO10,10^FDhi^FS^XZ") };
            var path = Path.Combine(root, "job.job");
            JobFile.Write(path, job);
            var back = JobFile.Read(path);

            if (back.Id != job.Id || back.Name != job.Name || !back.Data.SequenceEqual(job.Data))
                throw new Exception("job contents were not preserved");

            var header = JobFile.ReadHeader(path);
            if (header.Id != job.Id) throw new Exception("header id mismatch");

            Pass("durable job container round-trip");
        }
        catch (Exception ex) { Fail("durable job container round-trip: " + ex.Message); }
    }

    private static void TestClassifier()
    {
        try
        {
            var zpl = Encoding.ASCII.GetBytes("^XA^FO50,50^A0N,40,40^FDHELLO^FS^XZ");
            if (PrinterData.Classify(zpl) != PrinterDataKind.Zpl) throw new Exception("ZPL not detected");
            if (PrinterData.Classify(new byte[] { 0x25, 0x50, 0x44, 0x46 }) != PrinterDataKind.Pdf) throw new Exception("PDF not detected");
            if (PrinterData.IsForwardable(PrinterDataKind.Emf)) throw new Exception("EMF should be rejected");
            Pass("printer-language detection");
        }
        catch (Exception ex) { Fail("printer-language detection: " + ex.Message); }
    }

    private static async Task TestRelayEndToEnd(string root)
    {
        var relay = (RelayServer?)null;
        var host = (BridgeEngine?)null;
        var client = (BridgeEngine?)null;
        var printer = new FakePrinter();

        try
        {
            var relayPort = FreePort();
            var relayUrl = $"http://127.0.0.1:{relayPort}";
            relay = new RelayServer(new RelayOptions
            {
                ListenPrefix = relayUrl + "/",
                DataDir = Path.Combine(root, "relay"),
                MaxJobsPerChannel = 100
            });
            relay.Start();

            var (channelId, hostToken, clientToken) =
                await RelayPoller.CreateChannelAsync(relayUrl, "", "selftest", CancellationToken.None);

            printer.Start();

            var hostSettings = new AppSettings
            {
                Role = "host",
                TargetHost = "127.0.0.1",
                TargetPort = printer.Port,
                DirectServerEnabled = false,
                RelayPollEnabled = true,
                RelayUrl = relayUrl,
                ChannelId = channelId,
                HostToken = hostToken,
                MaxRetrySeconds = 5
            };

            var clientSettings = new AppSettings
            {
                Role = "client",
                CapturePort = FreePort(),
                Pair = new PairInfo
                {
                    Mode = "relay",
                    RelayUrl = relayUrl,
                    ChannelId = channelId,
                    ClientToken = clientToken
                }
            };

            host = new BridgeEngine(hostSettings, Path.Combine(root, "host"));
            client = new BridgeEngine(clientSettings, Path.Combine(root, "client"));

            await host.StartAsync();
            await client.StartAsync();

            var sent = new List<byte[]>
            {
                MakeZpl(1, 2_000),
                MakeZpl(2, 200_000),
                MakeZpl(3, 2_000_000)
            };

            foreach (var payload in sent)
            {
                var path = Path.Combine(root, $"relay-{Guid.NewGuid():N}.prn");
                await File.WriteAllBytesAsync(path, payload);
                await client.QueueFileAsync(path);
            }

            var ok = await WaitUntil(() => printer.Received.Count >= sent.Count, TimeSpan.FromSeconds(45));

            if (!ok)
                throw new Exception($"only {printer.Received.Count}/{sent.Count} jobs reached the printer");

            var received = printer.Received.ToArray();
            foreach (var payload in sent)
            {
                if (!received.Any(r => r.SequenceEqual(payload)))
                    throw new Exception($"payload of {payload.Length:N0} bytes was altered in transit");
            }

            Pass($"relay end-to-end ({sent.Sum(s => s.Length):N0} bytes in 3 jobs)");
        }
        catch (Exception ex) { Fail("relay end-to-end: " + ex.Message); }
        finally
        {
            if (client != null) await client.StopAsync();
            if (host != null) await host.StopAsync();
            relay?.Stop();
            printer.Dispose();
        }
    }

    private static async Task TestDirectEndToEnd(string root)
    {
        var host = (BridgeEngine?)null;
        var client = (BridgeEngine?)null;
        var printer = new FakePrinter();

        try
        {
            printer.Start();

            var hostSettings = new AppSettings
            {
                Role = "host",
                TargetHost = "127.0.0.1",
                TargetPort = printer.Port,
                DirectServerEnabled = true,
                DirectPort = FreePort(),
                DirectKey = Pairing.NewKeyText(),
                RelayPollEnabled = false,
                MaxRetrySeconds = 5
            };

            host = new BridgeEngine(hostSettings, Path.Combine(root, "direct-host"));
            await host.StartAsync();

            var fingerprint = host.DirectFingerprint;
            if (string.IsNullOrWhiteSpace(fingerprint)) throw new Exception("host did not expose a TLS fingerprint");

            var clientSettings = new AppSettings
            {
                Role = "client",
                CapturePort = FreePort(),
                Pair = new PairInfo
                {
                    Mode = "direct",
                    Host = "127.0.0.1",
                    Port = hostSettings.DirectPort,
                    Key = hostSettings.DirectKey,
                    Fingerprint = fingerprint
                }
            };

            client = new BridgeEngine(clientSettings, Path.Combine(root, "direct-client"));
            client.Notice += n =>
            {
                if (n.Kind is NoticeKind.Failed or NoticeKind.Warning)
                    Console.WriteLine("    [client] " + n.Message);
            };
            await client.StartAsync();

            var payload = MakeZpl(9, 150_000);
            var path = Path.Combine(root, "direct.prn");
            await File.WriteAllBytesAsync(path, payload);
            await client.QueueFileAsync(path);

            var ok = await WaitUntil(() => printer.Received.Count >= 1, TimeSpan.FromSeconds(30));
            if (!ok) throw new Exception("job did not arrive over TLS");
            if (!printer.Received.First().SequenceEqual(payload)) throw new Exception("payload altered in transit");

            Pass("direct TLS end-to-end with certificate pinning");
        }
        catch (Exception ex) { Fail("direct TLS end-to-end: " + ex.Message); }
        finally
        {
            if (client != null) await client.StopAsync();
            if (host != null) await host.StopAsync();
            printer.Dispose();
        }
    }

    private static async Task TestDurability(string root)
    {
        var relay = (RelayServer?)null;
        var host = (BridgeEngine?)null;
        var client = (BridgeEngine?)null;
        var printer = new FakePrinter(); // created but NOT started = printer offline

        try
        {
            var relayPort = FreePort();
            var relayUrl = $"http://127.0.0.1:{relayPort}";

            relay = new RelayServer(new RelayOptions
            {
                ListenPrefix = relayUrl + "/",
                DataDir = Path.Combine(root, "relay2")
            });
            relay.Start();

            var (channelId, hostToken, clientToken) =
                await RelayPoller.CreateChannelAsync(relayUrl, "", "durability", CancellationToken.None);

            var hostSettings = new AppSettings
            {
                Role = "host",
                TargetHost = "127.0.0.1",
                TargetPort = printer.Port,
                DirectServerEnabled = false,
                RelayPollEnabled = true,
                RelayUrl = relayUrl,
                ChannelId = channelId,
                HostToken = hostToken,
                MaxRetrySeconds = 5
            };

            var clientSettings = new AppSettings
            {
                Role = "client",
                CapturePort = FreePort(),
                Pair = new PairInfo { Mode = "relay", RelayUrl = relayUrl, ChannelId = channelId, ClientToken = clientToken }
            };

            host = new BridgeEngine(hostSettings, Path.Combine(root, "dur-host"));
            client = new BridgeEngine(clientSettings, Path.Combine(root, "dur-client"));

            await host.StartAsync();
            await client.StartAsync();

            var payload = MakeZpl(42, 25_000);
            var path = Path.Combine(root, "durable.prn");
            await File.WriteAllBytesAsync(path, payload);
            await client.QueueFileAsync(path);

            var queued = await WaitUntil(() => host.PendingCount >= 1, TimeSpan.FromSeconds(20));
            if (!queued) throw new Exception("host did not retain the job while the printer was offline");

            var deliveredWhileOffline = printer.Received.Count;
            if (deliveredWhileOffline > 0) throw new Exception("printer received data while it was offline");

            printer.Start(); // printer comes online - the queued job must print now

            var ok = await WaitUntil(() => printer.Received.Count >= 1, TimeSpan.FromSeconds(45));
            if (!ok) throw new Exception("queued job was not delivered after the printer came back");
            if (!printer.Received.First().SequenceEqual(payload)) throw new Exception("payload altered in transit");

            Pass("durable queue (printer offline -> queued -> delivered after recovery)");
        }
        catch (Exception ex) { Fail("durable queue: " + ex.Message); }
        finally
        {
            if (client != null) await client.StopAsync();
            if (host != null) await host.StopAsync();
            relay?.Stop();
            printer.Dispose();
        }
    }

    // ------------------------------------------------------------- helpers

    private static byte[] MakeZpl(int index, int size)
    {
        var head = Encoding.ASCII.GetBytes(
            $"^XA\n^CI28\n^PW1200\n^LL1800\n^FO60,60^A0N,60,60^FDSELFTEST JOB {index}^FS\n");
        var tail = Encoding.ASCII.GetBytes(
            $"^FO60,200^BY3^BCN,140,Y,N,N^FDLB{index:D4}{DateTime.Now:HHmmss}^FS\n^XZ\n");

        var pad = Math.Max(0, size - head.Length - tail.Length);
        var result = new byte[head.Length + pad + tail.Length];
        head.CopyTo(result, 0);
        Encoding.ASCII.GetBytes(new string('X', pad)).CopyTo(result, head.Length);
        tail.CopyTo(result, head.Length + pad);
        return result;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static void Pass(string name)
    {
        _passes++;
        Console.WriteLine($"  PASS  {name}");
    }

    private static void Fail(string name)
    {
        _failures++;
        Console.WriteLine($"  FAIL  {name}");
    }

    private sealed class FakePrinter : IDisposable
    {
        private readonly int _port;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        public ConcurrentQueue<byte[]> Received { get; } = new();
        public int Port => _port;

        public FakePrinter()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        public void Start()
        {
            if (_listener != null) return;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            var ct = _cts!.Token;
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener!.AcceptTcpClientAsync(ct); }
                catch { break; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        using (var ms = new MemoryStream())
                        {
                            await client.GetStream().CopyToAsync(ms, ct);
                            if (ms.Length > 0) Received.Enqueue(ms.ToArray());
                        }
                    }
                    catch { }
                }, ct);
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
