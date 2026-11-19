using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LabelBridge.Core.Server;

/// <summary>
/// Best-effort identification of a network printer over its RAW/TCP port (usually 9100).
/// Label printers answer a small set of vendor status/identity commands even when no
/// driver or SNMP is available. Results are used to pre-fill the profile that is pushed
/// to approved clients. Nothing here modifies the printer.
/// </summary>
public sealed record PrinterIdentity(
    bool Reachable,
    string Make,
    string Model,
    string PrintLanguage,
    string SuggestedDriverHint,
    string Raw,
    string Error)
{
    public static PrinterIdentity Failed(string error) =>
        new(false, "", "", "", "", "", error);
}

public static class PrinterIdentityProbe
{
    // command, expected language, short label
    private static readonly (byte[] Cmd, string Language)[] Queries =
    {
        (Encoding.ASCII.GetBytes("~HI\r"), "ZPL"),                       // Zebra host identification
        (Encoding.ASCII.GetBytes("U\r\n"), "EPL"),                       // EPL identification
        (Encoding.ASCII.GetBytes("\r"), "TSPL"),                         // TSPL status
        (Encoding.ASCII.GetBytes("! 0 0 0 0 1\r\n\r\n"), "CPCL"),
        (Encoding.ASCII.GetBytes("\x1D\x49\x01"), "ESC/POS"),            // GS I 1
        (Encoding.ASCII.GetBytes("\x1B%-12345X@PJL INFO ID\r\n\x1B%-12345X"), "PJL"),
    };

    public static async Task<PrinterIdentity> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) return PrinterIdentity.Failed("No printer host configured.");
        if (port <= 0 || port > 65535) port = 9100;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).AsTask().WaitAsync(cts.Token);
            var stream = client.GetStream();
            stream.ReadTimeout = 400;
            stream.WriteTimeout = 400;

            var sb = new StringBuilder();
            string language = "";

            foreach (var (cmd, lang) in Queries)
            {
                if (cts.IsCancellationRequested) break;
                try
                {
                    await stream.WriteAsync(cmd, cts.Token);
                    await Task.Delay(90, cts.Token);
                    var chunk = ReadAvailable(stream);
                    if (chunk.Length > 0)
                    {
                        var text = Clean(chunk);
                        if (text.Length > 0)
                        {
                            sb.AppendLine($"[{lang}] {text}");
                            if (language.Length == 0) language = lang;
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* this query language unsupported - try next */ }
            }

            var raw = sb.ToString();
            var (make, model) = ParseIdentity(raw);
            if (language.Length == 0) language = GuessLanguage(raw, model);

            return new PrinterIdentity(
                Reachable: true,
                Make: make,
                Model: model,
                PrintLanguage: language,
                SuggestedDriverHint: SuggestDriver(make, model, language),
                Raw: raw.Length > 1200 ? raw[..1200] : raw,
                Error: "");
        }
        catch (OperationCanceledException)
        {
            return PrinterIdentity.Failed("Printer did not answer in time (TCP connect/query timeout).");
        }
        catch (Exception ex)
        {
            return PrinterIdentity.Failed(ex.Message);
        }
        finally
        {
            try { client?.Dispose(); } catch { }
        }
    }

    private static string ReadAvailable(NetworkStream stream)
    {
        using var ms = new MemoryStream();
        var buf = new byte[2048];
        try
        {
            while (stream.DataAvailable && ms.Length < 4096)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
        }
        catch { }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static string Clean(string raw)
    {
        var sb = new StringBuilder();
        foreach (char c in raw)
        {
            if (c == '\r' || c == '\n' || c == '\t') { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); }
            else if (c >= 32 && c < 127) sb.Append(c);
        }
        return Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
    }

    private static (string make, string model) ParseIdentity(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");

        string[] makers =
        {
            "Zebra", "ZDesigner", "Honeywell", "Intermec", "Dymo", "TSC", "Brother",
            "Epson", "SATO", "Avery", "Xerox", "HP ", "Canon", "Lexmark", "OKI", "Kokuyo", "GoDEX", "Godex"
        };

        string make = "";
        foreach (var m in makers)
        {
            if (raw.Contains(m, StringComparison.OrdinalIgnoreCase)) { make = m.Trim(); break; }
        }

        // Model hints, in priority order.
        string[] patterns =
        {
            @"\b(ZD\d{3}[a-z]?|ZT\d{3}[a-z]?|ZM4\d{2}|ZQ5\d{2}|LP2\d{2}|ZK\d{3})\b",
            @"\b(TE\d{3}|TC\d{2}|TTP\d{3}|DA\d{3}|MX\d{3})\b",
            @"\b(PC4\d{2}|PD4\d{2}|PM4\d{2})\b",
            @"\b(LabelWriter|LX\d{3}|DW\d{3}|DYMO)\b",
            @"\b(4T5\d{2}|4T3\d{2})\b",
            @"\b(RW\d{3})\b"
        };
        string model = "";
        foreach (var p in patterns)
        {
            var m = Regex.Match(raw, p, RegexOptions.IgnoreCase);
            if (m.Success) { model = m.Value.ToUpperInvariant(); break; }
        }

        if (make.Length == 0 && raw.Contains("ZPL", StringComparison.OrdinalIgnoreCase)) make = "Zebra";
        if (make.Length == 0 && raw.Contains("EPL", StringComparison.OrdinalIgnoreCase)) make = "EPL";
        if (model.Length == 0 && raw.Contains("ZPL", StringComparison.OrdinalIgnoreCase)) model = "ZPL printer";
        return (make, model);
    }

    private static string GuessLanguage(string raw, string model)
    {
        if (raw.Contains("ZPL", StringComparison.OrdinalIgnoreCase) || model.StartsWith("ZD") || model.StartsWith("ZT")) return "ZPL";
        if (raw.Contains("EPL", StringComparison.OrdinalIgnoreCase)) return "EPL";
        if (raw.Contains("TSPL", StringComparison.OrdinalIgnoreCase) || model.StartsWith("T") && Regex.IsMatch(model, @"\d")) return "TSPL";
        if (raw.Contains("CPCL", StringComparison.OrdinalIgnoreCase)) return "CPCL";
        if (raw.Contains("PJL", StringComparison.OrdinalIgnoreCase)) return "PDF";
        return "RAW";
    }

    private static string SuggestDriver(string make, string model, string language)
    {
        var parts = new List<string>();
        if (make.Length > 0) parts.Add(make);
        if (model.Length > 0 && !model.Contains(make, StringComparison.OrdinalIgnoreCase)) parts.Add(model);
        if (language.Equals("ZPL", StringComparison.OrdinalIgnoreCase) && !parts.Any(p => p.Contains("ZPL", StringComparison.OrdinalIgnoreCase)))
            parts.Add("ZPL");
        return string.Join(" ", parts);
    }
}
