using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LabelBridge.Core;

/// <summary>
/// Direct-mode wire format. The payload is a JSON header plus the raw printer
/// bytes, authenticated with HMAC-SHA256 using the shared pairing key.
/// </summary>
public static class Envelope
{
    private const uint Magic = 0x31424C00; // "LB1\0"
    private const int MaxHeaderBytes = 256 * 1024;
    private const int MaxPayloadBytes = 256 * 1024 * 1024;
    public const int MacBytes = 32;

    public static async Task WriteAsync(Stream stream, PrintJob job, byte[] key, CancellationToken ct)
    {
        var header = job.CloneShallow();
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, AppSettings.JsonOpts);

        var payload = job.Data;
        var mac = ComputeMac(key, headerBytes, payload);

        var prefix = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(0), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), headerBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), payload.Length);

        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(headerBytes, ct);
        await stream.WriteAsync(payload, ct);
        await stream.WriteAsync(mac, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<PrintJob> ReadAsync(Stream stream, byte[] key, CancellationToken ct)
    {
        var prefix = await ReadExactAsync(stream, 12, ct);
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != Magic)
            throw new InvalidDataException("Unrecognised stream header.");

        int headerLen = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));
        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));

        if (headerLen <= 0 || headerLen > MaxHeaderBytes) throw new InvalidDataException("Invalid header size.");
        if (payloadLen < 0 || payloadLen > MaxPayloadBytes) throw new InvalidDataException("Invalid payload size.");

        var headerBytes = await ReadExactAsync(stream, headerLen, ct);
        var payload = payloadLen == 0 ? Array.Empty<byte>() : await ReadExactAsync(stream, payloadLen, ct);
        var mac = await ReadExactAsync(stream, MacBytes, ct);

        var expected = ComputeMac(key, headerBytes, payload);
        if (!CryptographicOperations.FixedTimeEquals(mac, expected))
            throw new CryptographicException("Authentication failed - the pairing key does not match this host.");

        var job = JsonSerializer.Deserialize<PrintJob>(headerBytes, AppSettings.JsonOpts)
                  ?? throw new InvalidDataException("Invalid job header.");
        job.Data = payload;
        return job;
    }

    private static byte[] ComputeMac(byte[] key, byte[] header, byte[] payload)
    {
        using var hmac = new HMACSHA256(key);
        hmac.TransformBlock(header, 0, header.Length, null, 0);
        hmac.TransformBlock(payload, 0, payload.Length, null, 0);
        hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hmac.Hash!;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException("Connection closed before the job was complete.");
            read += n;
        }
        return buffer;
    }
}

/// <summary>Self-signed TLS certificate for direct mode (pinned by fingerprint).</summary>
public static class TlsCert
{
    public static X509Certificate2 LoadOrCreate()
    {
        var path = AppPaths.CertFile;

        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoaderShim.Load(path);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(30) && existing.HasPrivateKey)
                    return existing;
            }
            catch (Exception ex)
            {
                Log.Warn("Existing TLS certificate unusable (" + ex.Message + "), creating a new one.");
            }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=LabelBridge Host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        var bytes = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, bytes);
        AppPaths.ProtectFile(path);

        return X509CertificateLoaderShim.Load(path);
    }

    public static string Fingerprint(X509Certificate2 cert) =>
        Base64Url.Encode(SHA256.HashData(cert.RawData));

    /// <summary>
    /// .NET 8 keeps the X509Certificate2(path) constructor; newer SDKs prefer
    /// X509CertificateLoader. This shim keeps the call site stable.
    /// </summary>
    private static class X509CertificateLoaderShim
    {
        public static X509Certificate2 Load(string path) => new(path);
    }
}
