using System.Text;
using LabelBridge.Core;
using LabelBridge.Core.Server;

namespace LabelBridge.Services;

/// <summary>
/// Built-in verification for the server + Windows client architecture.
/// Runs locally with no GUI, no hardware and no internet access.
/// </summary>
internal static class SelfTest
{
    private static int _failures;
    private static int _passes;

    public static int Run(string[] args)
    {
        Log.Minimum = LogLevel.Warn;
        Console.WriteLine("LabelBridge self-test (server + Windows client)");
        Console.WriteLine("================================");

        var root = Path.Combine(Path.GetTempPath(), "labelbridge-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            TestFormatPolicy();
            TestJobFileRoundTrip(root);
            TestServerStoreAndApproval(root);
            TestServerPrintQueuePolicy(root).GetAwaiter().GetResult();
            TestTestLabelGeneration(root);
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
        if (_failures == 0)
            Console.WriteLine("Core checks passed. Full end-to-end validation still requires: web UI/admin approval, Windows virtual printer install (admin), and a reachable network printer (TCP/9100).");
        return _failures == 0 ? 0 : 1;
    }

    private static void TestFormatPolicy()
    {
        try
        {
            var zpl = Encoding.ASCII.GetBytes("^XA^FO20,20^FDHELLO^FS^XZ");
            var epl = Encoding.ASCII.GetBytes("\nN\nA20,20,0,4,1,1,N,\"HI\"\nP1\n");
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%...");
            var ps = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\n...");
            var emf = new byte[64];
            emf[0] = 1; emf[40] = (byte)' '; emf[41] = (byte)'E'; emf[42] = (byte)'M'; emf[43] = (byte)'F';
            var xps = new byte[] { (byte)'P', (byte)'K', 3, 4, 0, 0 };
            var empty = Array.Empty<byte>();

            Expect(PrinterData.Classify(zpl) == PrinterDataKind.Zpl, "ZPL detected");
            Expect(PrinterData.Classify(epl) == PrinterDataKind.Epl, "EPL detected");
            Expect(PrinterData.Classify(pdf) == PrinterDataKind.Pdf, "PDF detected");
            Expect(PrinterData.Classify(ps) == PrinterDataKind.PostScript, "PostScript detected");
            Expect(PrinterData.Classify(emf) == PrinterDataKind.Emf, "EMF detected");
            Expect(PrinterData.Classify(xps) == PrinterDataKind.Xps, "XPS detected");

            Expect(PrinterData.IsForwardable(PrinterDataKind.Zpl), "ZPL accepted for direct pass-through");
            Expect(PrinterData.IsForwardable(PrinterDataKind.Pdf), "PDF accepted for pass-through (printer/profile must support)");
            Expect(!PrinterData.IsForwardable(PrinterDataKind.Emf), "EMF rejected for raw network printing");
            Expect(!PrinterData.IsForwardable(PrinterDataKind.Xps), "XPS rejected for raw network printing");
            Expect(!PrinterData.IsForwardable(PrinterDataKind.Empty), "empty rejected");
            Expect(!string.IsNullOrWhiteSpace(PrinterData.RejectReason(PrinterDataKind.Emf)), "EMF rejection reason provided");

            Pass("format detection and direct-print policy");
        }
        catch (Exception ex) { Fail("format detection and direct-print policy: " + ex.Message); }
    }

    private static void TestJobFileRoundTrip(string root)
    {
        try
        {
            var path = Path.Combine(root, "job.job");
            var job = new PrintJob { Id = Guid.NewGuid().ToString("N"), Name = "test", Source = "selftest", Data = Encoding.UTF8.GetBytes("^XA^FDX^FS^XZ") };
            JobFile.Write(path, job);
            var back = JobFile.Read(path);
            Expect(back.Id == job.Id && back.Name == job.Name && back.Data.SequenceEqual(job.Data), "job contents preserved");
            var hdr = JobFile.ReadHeader(path);
            Expect(hdr.Id == job.Id, "header readable");
            Pass("durable job file round-trip");
        }
        catch (Exception ex) { Fail("durable job file round-trip: " + ex.Message); }
    }

    private static void TestServerStoreAndApproval(string root)
    {
        try
        {
            var dir = Path.Combine(root, "server");
            var store = new ServerStore(dir);
            store.EnsureAdmin("init-secret");
            Expect(!store.State.SetupComplete, "initial admin requires password change/setup by default for generated flow");
            var (ok, token, err) = store.Login("admin", "init-secret");
            Expect(ok && !string.IsNullOrWhiteSpace(token), "admin login with initial secret: " + (err ?? "ok"));
            Expect(store.ValidateSession(token), "session valid");
            Expect(store.ChangePassword("init-secret", "new-pass-123"), "password changed");
            var (ok2, token2, _) = store.Login("admin", "new-pass-123");
            Expect(ok2 && !string.IsNullOrWhiteSpace(token2), "login with new password");

            var client = store.AddPendingClient("PC1", "MACHINE1");
            Expect(client.Status == nameof(ClientStatus.Pending), "client pending");
            var (approved, issued) = store.ApproveClient(client.Id);
            Expect(approved != null && !string.IsNullOrWhiteSpace(issued), "client approved with token");
            var claimed = store.ClaimClientToken(client.Id);
            Expect(claimed == issued, "token claim returns issued token once");
            var claimed2 = store.ClaimClientToken(client.Id);
            Expect(string.IsNullOrEmpty(claimed2), "token cannot be claimed twice");
            var authed = store.ValidateClientToken(issued);
            Expect(authed != null && authed.Id == client.Id, "claimed token authenticates client");
            store.SetClientStatus(client.Id, ClientStatus.Revoked);
            Expect(store.ValidateClientToken(issued) == null, "revoked token rejected");

            store.State.Printer.Host = "10.0.0.5"; store.State.Printer.Port = 9100;
            store.State.Printer.Profile.Name = "4x6"; store.State.Printer.Profile.Width = "4in"; store.State.Printer.Profile.Height = "6in";
            store.Save();
            var store2 = new ServerStore(dir);
            Expect(store2.State.Printer.Host == "10.0.0.5" && store2.State.Printer.Profile.Width == "4in", "state persisted");

            Pass("server store, auth, client approval/token claim, persistence");
        }
        catch (Exception ex) { Fail("server store/auth/approval: " + ex.Message); }
    }

    private static async Task TestServerPrintQueuePolicy(string root)
    {
        try
        {
            var dir = Path.Combine(root, "printsvc");
            var store = new ServerStore(dir);
            store.State.Printer.Host = ""; // keep worker from actually printing
            store.Save();
            var cfg = new ServerConfig { DataDir = dir, MaxPayloadBytes = 1024 * 1024, MaxJobs = 100 };
            using var svc = new PrintService(store, cfg);

            var (okZpl, msgZpl, idZpl) = await svc.EnqueueJobAsync("c1", "label", Encoding.ASCII.GetBytes("^XA^XZ"));
            Expect(okZpl && !string.IsNullOrEmpty(idZpl), "ZPL job accepted: " + msgZpl);

            var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test");
            var (okPdf, msgPdf, _) = await svc.EnqueueJobAsync("c1", "doc.pdf", pdf);
            Expect(okPdf, "PDF accepted for raw pass-through (printer must support): " + msgPdf);

            var emf = new byte[64];
            emf[0] = 1; emf[40] = (byte)' '; emf[41] = (byte)'E'; emf[42] = (byte)'M'; emf[43] = (byte)'F';
            var (okEmf, msgEmf, _) = await svc.EnqueueJobAsync("c1", "bad", emf);
            Expect(!okEmf && (msgEmf?.Contains("EMF", StringComparison.OrdinalIgnoreCase) ?? false), "EMF rejected with clear reason: " + msgEmf);

            var xps = new byte[] { (byte)'P', (byte)'K', 3, 4, 1, 2, 3, 4 };
            var (okXps, msgXps, _) = await svc.EnqueueJobAsync("c1", "bad", xps);
            Expect(!okXps && (msgXps?.Contains("XPS", StringComparison.OrdinalIgnoreCase) ?? false), "XPS rejected with clear reason: " + msgXps);

            Pass("server enqueue/queue format policy");
        }
        catch (Exception ex) { Fail("server enqueue/queue format policy: " + ex.Message); }
    }

    private static void TestTestLabelGeneration(string root)
    {
        try
        {
            var dir = Path.Combine(root, "labels");
            var store = new ServerStore(dir);
            using var svc = new PrintService(store, new ServerConfig { DataDir = dir });
            var zpl = svc.BuildTestLabel(new PrinterProfile { PrintLanguage = "ZPL", Name = "P", Width = "4in", Height = "6in", Dpi = "203" });
            Expect(Encoding.UTF8.GetString(zpl).Contains("^XA"), "ZPL test label");
            var epl = svc.BuildTestLabel(new PrinterProfile { PrintLanguage = "EPL" });
            Expect(Encoding.ASCII.GetString(epl).Contains("P1"), "EPL test label");
            var tspl = svc.BuildTestLabel(new PrinterProfile { PrintLanguage = "TSPL", Width = "100", Height = "60" });
            Expect(Encoding.ASCII.GetString(tspl).Contains("PRINT"), "TSPL test label");
            Pass("test label generation per print language");
        }
        catch (Exception ex) { Fail("test label generation: " + ex.Message); }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
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
}
