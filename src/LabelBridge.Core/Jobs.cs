using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelBridge.Core;

/// <summary>A single label / document captured from a print queue.</summary>
public sealed class PrintJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public DateTime NextAttemptUtc { get; set; } = DateTime.MinValue;
    public string? LastError { get; set; }

    [JsonIgnore] public byte[] Data { get; set; } = Array.Empty<byte>();
    [JsonIgnore] public long Size => Data.LongLength;

    public PrintJob CloneShallow() => new()
    {
        Id = Id, Name = Name, Source = Source, CreatedUtc = CreatedUtc,
        Attempts = Attempts, NextAttemptUtc = NextAttemptUtc, LastError = LastError
    };

    public override string ToString() => $"{Name} ({Size:N0} B)";
}

/// <summary>
/// On-disk job container: [int32 headerLen][utf8 json header][int64 dataLen][data].
/// Survives restarts and app updates.
/// </summary>
public static class JobFile
{
    private const int MaxHeaderBytes = 256 * 1024;
    private const long MaxDataBytes = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(string path, PrintJob job)
    {
        var header = job.CloneShallow();
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(headerBytes.Length);
            bw.Write(headerBytes);
            bw.Write(job.Data.LongLength);
            bw.Write(job.Data);
            bw.Flush();
        }

        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    public static PrintJob Read(string path, string? overrideId = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var br = new BinaryReader(fs);

        int headerLen = br.ReadInt32();
        if (headerLen <= 0 || headerLen > MaxHeaderBytes) throw new InvalidDataException("Invalid job header length.");

        var header = JsonSerializer.Deserialize<PrintJob>(br.ReadBytes(headerLen), JsonOptions)
                     ?? throw new InvalidDataException("Invalid job header.");

        long dataLen = br.ReadInt64();
        if (dataLen < 0 || dataLen > MaxDataBytes) throw new InvalidDataException("Invalid job payload length.");

        header.Data = br.ReadBytes((int)dataLen);
        if (header.Data.LongLength != dataLen) throw new InvalidDataException("Truncated job payload.");

        if (!string.IsNullOrEmpty(overrideId))
        {
            header.Id = overrideId;
            header.Name = string.IsNullOrWhiteSpace(header.Name) ? "relay job" : header.Name;
        }

        return header;
    }

    /// <summary>Reads only the header - used for fast queue listings.</summary>
    public static PrintJob ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs);
        int headerLen = br.ReadInt32();
        if (headerLen <= 0 || headerLen > MaxHeaderBytes) throw new InvalidDataException("Invalid job header length.");
        return JsonSerializer.Deserialize<PrintJob>(br.ReadBytes(headerLen), JsonOptions)
               ?? throw new InvalidDataException("Invalid job header.");
    }
}

/// <summary>
/// Durable FIFO job queue backed by a folder. Each item is retried with
/// exponential backoff until the destination acknowledges it.
/// </summary>
public sealed class JobStore
{
    private readonly string _dir;
    private readonly int _maxItems;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public JobStore(string dir, int maxItems = 2000)
    {
        _dir = AppPaths.Ensure(dir);
        _maxItems = maxItems;
    }

    public string Folder => _dir;
    public SemaphoreSlim Signal => _signal;

    public void Wake() => _signal.Release();

    public int Count
    {
        get
        {
            try { return Directory.EnumerateFiles(_dir, "*.job").Count(); }
            catch { return 0; }
        }
    }

    public IReadOnlyList<string> Paths()
    {
        try
        {
            return Directory.EnumerateFiles(_dir, "*.job")
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public IReadOnlyList<PrintJob> Snapshot(int max = 200)
    {
        var list = new List<PrintJob>();
        foreach (var p in Paths().Take(max))
        {
            try { list.Add(JobFile.ReadHeader(p)); } catch { }
        }
        return list;
    }

    public bool ContainsId(string id)
    {
        try
        {
            return Directory.EnumerateFiles(_dir, "*.job")
                .Any(p => Path.GetFileNameWithoutExtension(p).EndsWith(id, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public string Enqueue(PrintJob job)
    {
        lock (_gate)
        {
            Trim();
            var name = $"{job.CreatedUtc:yyyyMMddHHmmssfff}-{job.Id}.job";
            var path = Path.Combine(_dir, name);
            JobFile.Write(path, job);
            _signal.Release();
            return path;
        }
    }

    public bool TryPeek(DateTime utcNow, out string path, out PrintJob job)
    {
        path = "";
        job = null!;

        foreach (var candidate in Paths())
        {
            try
            {
                var header = JobFile.ReadHeader(candidate);
                if (header.NextAttemptUtc > utcNow) continue;

                var full = JobFile.Read(candidate);
                path = candidate;
                job = full;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Discarding unreadable queue item " + Path.GetFileName(candidate) + ": " + ex.Message);
                TryDelete(candidate);
            }
        }

        return false;
    }

    public void MarkFailed(string path, PrintJob job, string error, TimeSpan delay)
    {
        lock (_gate)
        {
            job.Attempts++;
            job.LastError = Truncate(error, 500);
            job.NextAttemptUtc = DateTime.UtcNow.Add(delay);
            try { JobFile.Write(path, job); }
            catch (Exception ex) { Log.Warn("Could not update queue item: " + ex.Message); }
        }
        _signal.Release();
    }

    public void Remove(string path)
    {
        lock (_gate) TryDelete(path);
    }

    public string Archive(PrintJob job, string archiveDir)
    {
        var dir = AppPaths.Ensure(archiveDir);
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{job.Id}.job";
        var dest = Path.Combine(dir, name);
        try { JobFile.Write(dest, job); } catch (Exception ex) { Log.Warn("Archive failed: " + ex.Message); }

        try
        {
            var files = Directory.EnumerateFiles(dir, "*.job")
                .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
                .Skip(500)
                .ToArray();
            foreach (var f in files) TryDelete(f);
        }
        catch { }

        return dest;
    }

    private void Trim()
    {
        try
        {
            var files = Paths();
            if (files.Count <= _maxItems) return;

            foreach (var f in files.Take(files.Count - _maxItems + 50))
            {
                Log.Warn("Queue limit reached - dropping oldest item " + Path.GetFileName(f));
                TryDelete(f);
            }
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
