using System.Text;

namespace LabelBridge.Core;

public enum PrinterDataKind
{
    Empty,
    Zpl,
    Epl,
    EscPos,
    PostScript,
    Emf,
    Xps,
    Pdf,
    Unknown
}

/// <summary>
/// Lightweight heuristics so the app can (a) warn when a job is not printer
/// language at all and (b) render a readable preview in the GUI.
/// </summary>
public static class PrinterData
{
    public static PrinterDataKind Classify(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return PrinterDataKind.Empty;

        if (StartsWith(data, "%PDF")) return PrinterDataKind.Pdf;
        if (StartsWith(data, "%!PS")) return PrinterDataKind.PostScript;

        // EMF: record type 1 followed by " EMF" at offset 40
        if (data.Length > 44 && data[0] == 1 && data[1] == 0 && data[2] == 0 && data[3] == 0 &&
            data[40] == (byte)' ' && data[41] == (byte)'E' && data[42] == (byte)'M' && data[43] == (byte)'F')
            return PrinterDataKind.Emf;

        if (data.Length > 4 && data[0] == (byte)'P' && data[1] == (byte)'K' && data[2] == 3 && data[3] == 4)
            return PrinterDataKind.Xps;

        var head = data.Length > 8192 ? data[..8192] : data;
        var text = Encoding.Latin1.GetString(head);

        if (text.Contains("^XA", StringComparison.Ordinal) || text.Contains("^XZ", StringComparison.Ordinal) ||
            text.Contains("^FO", StringComparison.Ordinal) || text.Contains("^GFA", StringComparison.Ordinal))
            return PrinterDataKind.Zpl;

        if (text.Contains("\r\nN\r\n", StringComparison.Ordinal) || text.Contains("\nN\n", StringComparison.Ordinal) ||
            text.Contains("\r\nq", StringComparison.Ordinal))
            return PrinterDataKind.Epl;

        if (data.Length > 2 && data[0] == 0x1B && (data[1] == (byte)'@' || data[1] == (byte)'!'))
            return PrinterDataKind.EscPos;

        return PrinterDataKind.Unknown;
    }

    /// <summary>EMF/XPS/PDF cannot be sent raw to a label printer.</summary>
    public static bool IsForwardable(PrinterDataKind kind) => kind switch
    {
        PrinterDataKind.Emf or PrinterDataKind.Xps or PrinterDataKind.Pdf or PrinterDataKind.Empty => false,
        _ => true
    };

    /// <summary>Small status queries some drivers send; never forward those.</summary>
    public static bool IsStatusProbe(byte[] data)
    {
        if (data.Length == 0) return true;
        if (data.Length > 512) return false;

        var s = Encoding.Latin1.GetString(data);
        return s.StartsWith("~HS", StringComparison.Ordinal)
               || s.StartsWith("~HI", StringComparison.Ordinal)
               || s.StartsWith("~HM", StringComparison.Ordinal)
               || s.StartsWith("~HQ", StringComparison.Ordinal)
               || s.StartsWith("~HU", StringComparison.Ordinal)
               || s.StartsWith("~JA", StringComparison.Ordinal)
               || s.StartsWith("\u001b@", StringComparison.Ordinal) && data.Length < 8;
    }

    public static string Describe(PrinterDataKind kind) => kind switch
    {
        PrinterDataKind.Zpl => "ZPL label data",
        PrinterDataKind.Epl => "EPL label data",
        PrinterDataKind.EscPos => "ESC/POS data",
        PrinterDataKind.PostScript => "PostScript",
        PrinterDataKind.Emf => "EMF (rendered Windows page)",
        PrinterDataKind.Xps => "XPS package",
        PrinterDataKind.Pdf => "PDF document",
        PrinterDataKind.Empty => "empty",
        _ => "raw data"
    };

    /// <summary>Human-readable printable preview (control bytes shown as ·).</summary>
    public static string Preview(byte[] data, int max = 65536)
    {
        var len = Math.Min(data.Length, max);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = data[i];
            switch (b)
            {
                case 13: sb.Append('\n'); break;      // CR -> newline (ZPL uses CRLF)
                case 10: break;
                case 9: sb.Append("    "); break;
                default:
                    if (b >= 32 && b < 127) sb.Append((char)b);
                    else if (b == 0) sb.Append('·');
                    else sb.Append('.');
                    break;
            }
        }
        if (data.Length > len) sb.Append($"\n\n… {data.Length - len:N0} more bytes");
        return sb.ToString();
    }

    public static string HexDump(byte[] data, int max = 512)
    {
        var len = Math.Min(data.Length, max);
        var sb = new StringBuilder(len * 4);
        for (int i = 0; i < len; i += 16)
        {
            sb.Append(i.ToString("X6")).Append("  ");
            for (int j = 0; j < 16; j++)
            {
                sb.Append(i + j < len ? data[i + j].ToString("X2") : "  ");
                sb.Append(' ');
            }
            sb.Append(' ');
            for (int j = 0; j < 16 && i + j < len; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine();
        }
        if (data.Length > len) sb.AppendLine($"… {data.Length - len:N0} more bytes");
        return sb.ToString();
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, string ascii)
    {
        if (data.Length < ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
            if (data[i] != (byte)ascii[i]) return false;
        return true;
    }
}
