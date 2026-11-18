using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LabelBridge.Core;

public sealed class RelayOptions
{
    public string ListenPrefix { get; set; } = "http://+:8080/";
    public string DataDir { get; set; } = "data";
    public string? AdminSecret { get; set; }
    public int MaxJobsPerChannel { get; set; } = 1000;
    public int MaxPayloadBytes { get; set; } = 64 * 1024 * 1024;
    public int RetentionHours { get; set; } = 168;
    public int MaxChannels { get; set; } = 20000;
}

public sealed class RelayFullException : Exception
{
    public RelayFullException(string message) : base(message) { }
}

/// <summary>
/// File-backed channel + job storage for the relay. Everything survives a
/// container restart: jobs live on disk until the host acknowledges them.
/// </summary>
public sealed class ChannelStore
{
    private sealed class Channel
    {
        public required string Id;
        public required string Name;
        public required DateTime CreatedUtc;
        public required string HostHash;
        public required string ClientHash;
        public readonly SemaphoreSlim Signal = new(0, int.MaxValue);
        public readonly object Gate = new();
    }

    private sealed class ChannelMeta
    {
        public string ChannelId { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string HostHash { get; set; } = "";
        public string ClientHash { get; set; } = "";
    }

    private static readonly Regex IdPattern = new("^[0-9a-f]{16,64}$", RegexOptions.Compiled);
    private static readonly Regex JobIdPattern = new("^[A-Za-z0-9_-]{8,64}$", RegexOptions.Compiled);

    /// <summary>Only opaque, filesystem-safe ids are trusted from clients.</summary>
    public static bool IsSafeJobId(string? id) => !string.IsNullOrEmpty(id) && JobIdPattern.IsMatch(id);

    private readonly RelayOptions _options;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Channel> _cache = new();
    private readonly object _createGate = new();
    private int _enqueueCounter;

    public ChannelStore(RelayOptions options)
    {
        _options = options;
        _root = Path.Combine(options.DataDir, "channels");
        Directory.CreateDirectory(_root);
        RecoverInflight();
    }

    public int ChannelCount => Directory.EnumerateDirectories(_root).Count();

    // ------------------------------------------------------------- channels

    public (string channelId, string hostToken, string clientToken) CreateChannel(string name)
    {
        if (ChannelCount >= _options.MaxChannels)
            throw new RelayFullException("Relay channel limit reached.");

        var channelId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var hostToken = Pairing.NewKeyText();
        var clientToken = Pairing.NewKeyText();

        var meta = new ChannelMeta
        {
            ChannelId = channelId,
            Name = string.IsNullOrWhiteSpace(name) ? "printer" : name.Trim(),
            CreatedUtc = DateTime.UtcNow,
            HostHash = Pairing.Sha256Base64(hostToken),
            ClientHash = Pairing.Sha256Base64(clientToken)
        };

        lock (_createGate)
        {
            var dir = Path.Combine(_root, channelId);
            Directory.CreateDirectory(Path.Combine(dir, "jobs"));
            Directory.CreateDirectory(Path.Combine(dir, "inflight"));
            File.WriteAllText(Path.Combine(dir, "channel.json"), JsonSerializer.Serialize(meta, AppSettings.JsonOpts));
        }

        Log.Info($"Relay channel {channelId} created ('{meta.Name}').");
        return (channelId, hostToken, clientToken);
    }

    public bool IsHost(string channelId, string? token)
    {
        var ch = GetOrLoad(channelId);
        return ch != null && !string.IsNullOrEmpty(token) && Pairing.FixedTimeEquals(Pairing.Sha256Base64(token), ch.HostHash);
    }

    public bool IsClient(string channelId, string? token)
    {
        var ch = GetOrLoad(channelId);
        return ch != null && !string.IsNullOrEmpty(token) && Pairing.FixedTimeEquals(Pairing.Sha256Base64(token), ch.ClientHash);
    }

    public bool DeleteChannel(string channelId)
    {
        var ch = GetOrLoad(channelId);
        if (ch == null) return false;

        _cache.TryRemove(channelId, out _);
        try { Directory.Delete(Path.Combine(_root, channelId), true); } catch { }
        Log.Info($"Relay channel {channelId} deleted.");
        return true;
    }

    public (int pending, DateTime? oldest) Status(string channelId)
    {
        var ch = GetOrLoad(channelId);
        if (ch == null) return (0, null);

        var dir = Path.Combine(_root, channelId, "jobs");
        var files = Directory.EnumerateFiles(dir, "*.job").OrderBy(f => Path.GetFileName(f)).ToArray();
        DateTime? oldest = files.Length == 0 ? null : File.GetCreationTimeUtc(files[0]);
        return (files.Length, oldest);
    }

    // ----------------------------------------------------------------- jobs

    public void Enqueue(string channelId, PrintJob job)
    {
        var ch = GetOrLoad(channelId) ?? throw new KeyNotFoundException("Unknown channel.");
        if (job.Data.LongLength > _options.MaxPayloadBytes)
            throw new RelayFullException($"Job larger than the {_options.MaxPayloadBytes / (1024 * 1024)} MB relay limit.");

        var jobsDir = Path.Combine(_root, channelId, "jobs");
        var inflightDir = Path.Combine(_root, channelId, "inflight");

        // Never let a client-supplied id influence a path.
        var jobId = IsSafeJobId(job.Id) ? job.Id : Guid.NewGuid().ToString("N");
        job.Id = jobId;

        lock (ch.Gate)
        {
            int count = Directory.EnumerateFiles(jobsDir, "*.job").Count()
                      + Directory.EnumerateFiles(inflightDir, "*.job").Count();
            if (count >= _options.MaxJobsPerChannel)
                throw new RelayFullException("This printer's relay queue is full - the host is not collecting jobs.");

            var name = $"{DateTime.UtcNow.Ticks:D19}-{jobId}.job";
            JobFile.Write(Path.Combine(jobsDir, name), job);
        }

        ch.Signal.Release();

        if (Interlocked.Increment(ref _enqueueCounter) % 100 == 0) Cleanup();
    }

    /// <summary>Waits up to <paramref name="wait"/> for a job, then claims it.</summary>
    public async Task<PrintJob?> TakeNextAsync(string channelId, TimeSpan wait, CancellationToken ct)
    {
        var ch = GetOrLoad(channelId);
        if (ch == null) return null;

        var deadline = DateTime.UtcNow + wait;
        while (!ct.IsCancellationRequested)
        {
            var job = TryTake(ch);
            if (job != null) return job;

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;

            try { await ch.Signal.WaitAsync(remaining, ct); }
            catch (OperationCanceledException) { return null; }
        }

        return null;
    }

    private PrintJob? TryTake(Channel ch)
    {
        var jobsDir = Path.Combine(_root, ch.Id, "jobs");
        var inflightDir = Path.Combine(_root, ch.Id, "inflight");

        lock (ch.Gate)
        {
            var candidate = Directory.EnumerateFiles(jobsDir, "*.job")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate == null) return null;

            var dest = Path.Combine(inflightDir, Path.GetFileName(candidate));
            try
            {
                File.Move(candidate, dest);
            }
            catch (IOException)
            {
                return null; // raced with another poller
            }

            try
            {
                return JobFile.Read(dest);
            }
            catch (Exception ex)
            {
                Log.Warn("Relay discarding unreadable job: " + ex.Message);
                try { File.Delete(dest); } catch { }
                return null;
            }
        }
    }

    public void Ack(string channelId, string jobId)
    {
        if (!IsSafeJobId(jobId)) return;

        var inflightDir = Path.Combine(_root, channelId, "inflight");
        if (!Directory.Exists(inflightDir)) return;

        foreach (var f in Directory.EnumerateFiles(inflightDir, "*-" + jobId + ".job"))
        {
            if (!Path.GetFileNameWithoutExtension(f).EndsWith(jobId, StringComparison.Ordinal)) continue;
            try { File.Delete(f); } catch { }
        }
    }

    // -------------------------------------------------------------- upkeep

    /// <summary>Jobs claimed but never acked (host crash) return to the queue.</summary>
    private void RecoverInflight()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var inflight = Path.Combine(dir, "inflight");
            var jobs = Path.Combine(dir, "jobs");
            if (!Directory.Exists(inflight)) continue;

            foreach (var f in Directory.EnumerateFiles(inflight, "*.job"))
            {
                try { File.Move(f, Path.Combine(jobs, Path.GetFileName(f)), true); } catch { }
            }
        }
    }

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow.AddHours(-_options.RetentionHours);
        try
        {
            foreach (var channelDir in Directory.EnumerateDirectories(_root))
            {
                foreach (var sub in new[] { "jobs", "inflight" })
                {
                    var dir = Path.Combine(channelDir, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*.job"))
                    {
                        try
                        {
                            if (File.GetCreationTimeUtc(f) < cutoff) File.Delete(f);
                            else if (sub == "inflight" && DateTime.UtcNow - File.GetCreationTimeUtc(f) > TimeSpan.FromMinutes(10))
                                File.Move(f, Path.Combine(channelDir, "jobs", Path.GetFileName(f)), true);
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Relay cleanup failed: " + ex.Message);
        }
    }

    private Channel? GetOrLoad(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || !IdPattern.IsMatch(channelId)) return null;
        if (_cache.TryGetValue(channelId, out var cached)) return cached;

        var metaPath = Path.Combine(_root, channelId, "channel.json");
        if (!File.Exists(metaPath)) return null;

        try
        {
            var meta = JsonSerializer.Deserialize<ChannelMeta>(File.ReadAllText(metaPath), AppSettings.JsonOpts);
            if (meta == null) return null;

            var ch = new Channel
            {
                Id = meta.ChannelId,
                Name = meta.Name,
                CreatedUtc = meta.CreatedUtc,
                HostHash = meta.HostHash,
                ClientHash = meta.ClientHash
            };

            Directory.CreateDirectory(Path.Combine(_root, channelId, "jobs"));
            Directory.CreateDirectory(Path.Combine(_root, channelId, "inflight"));
            _cache.TryAdd(channelId, ch);
            return _cache.TryGetValue(channelId, out var winner) ? winner : ch;
        }
        catch
        {
            return null;
        }
    }
}
