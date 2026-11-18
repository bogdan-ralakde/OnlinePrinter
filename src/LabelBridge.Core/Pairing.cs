using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabelBridge.Core;

/// <summary>Pairing-code encoding, key generation and secret comparison.</summary>
public static class Pairing
{
    public const string Prefix = "LB1:";

    public static string NewKeyText() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    public static byte[] KeyFromText(string text)
    {
        try { return Base64Url.Decode(text.Trim()); }
        catch { return Array.Empty<byte>(); }
    }

    public static string Encode(PairInfo info)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(info, AppSettings.JsonOpts);
        var code = Prefix + Base64Url.Encode(json);

        // wrap to 96-char lines so it can be pasted into e-mail / chat cleanly
        var sb = new StringBuilder();
        for (int i = 0; i < code.Length; i += 96)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(code, i, Math.Min(96, code.Length - i));
        }
        return sb.ToString();
    }

    public static PairInfo? TryDecode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var cleaned = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var idx = cleaned.IndexOf(Prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        cleaned = cleaned[(idx + Prefix.Length)..];

        try
        {
            var json = Base64Url.Decode(cleaned);
            var info = JsonSerializer.Deserialize<PairInfo>(json, AppSettings.JsonOpts);
            if (info == null) return null;

            info.Mode = string.IsNullOrWhiteSpace(info.Mode) ? "relay" : info.Mode.Trim().ToLowerInvariant();
            info.RelayUrl = (info.RelayUrl ?? "").Trim().TrimEnd('/');
            if (info.RelayUrl.Length > 0 && !info.RelayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                info.RelayUrl = "http://" + info.RelayUrl;
            info.Label = (info.Label ?? "").Trim();
            info.PrinterName = (info.PrinterName ?? "").Trim();
            return info;
        }
        catch
        {
            return null;
        }
    }

    public static string Sha256Base64(byte[] data) => Base64Url.Encode(SHA256.HashData(data));
    public static string Sha256Base64(string text) => Sha256Base64(Encoding.UTF8.GetBytes(text));

    /// <summary>Constant-time comparison of two secrets (hex/base64 text or raw).</summary>
    public static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public static bool FixedTimeEquals(byte[] a, byte[] b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

    public static string HmacHex(byte[] key, byte[] data) =>
        Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
}

public static class Base64Url
{
    public static string Encode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static byte[] Decode(string value)
    {
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    public static string DecodeText(string value) => Encoding.UTF8.GetString(Decode(value));
}
