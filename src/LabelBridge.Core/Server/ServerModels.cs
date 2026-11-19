using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabelBridge.Core.Server;

public sealed class ServerConfig
{
    public string ListenPrefix { get; set; } = "http://+:8080/";
    public string DataDir { get; set; } = "data";
    public string? InitialAdminSecret { get; set; }
    public int MaxJobs { get; set; } = 2000;
    public int MaxPayloadBytes { get; set; } = 128 * 1024 * 1024;
    public int RetentionHours { get; set; } = 168;
    public string Repo { get; set; } = "bogdan-ralakde/OnlinePrinter";
    public string CurrentVersion { get; set; } = "1.0.0";
}

public sealed class AdminRecord
{
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public bool MustChangePassword { get; set; }
}

public enum ClientStatus { Pending, Approved, Revoked, Declined }

public sealed class ClientRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Status { get; set; } = nameof(ClientStatus.Pending);
    public string TokenHash { get; set; } = "";
    public string? PendingToken { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string? Notes { get; set; }
}

public sealed class PrinterProfile
{
    public string Name { get; set; } = "Default";
    public string Width { get; set; } = "";
    public string Height { get; set; } = "";
    public string Dpi { get; set; } = "";
    public string Orientation { get; set; } = "";
    public string MediaType { get; set; } = ""; // e.g. gap, mark, continuous
    public string PrintMethod { get; set; } = ""; // e.g. direct-thermal, thermal-transfer
    public string Darkness { get; set; } = "";
    public string Speed { get; set; } = "";
    public string DriverHint { get; set; } = ""; // exact Windows driver name/model hint for virtual printer
    public string PrintLanguage { get; set; } = ""; // e.g. ZPL, EPL, CPCL, TSPL, ESC/POS, PDF, RAW
    public Dictionary<string, string> Extra { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PrinterConfig
{
    public string Name { get; set; } = "Printer";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9100;
    public bool Enabled { get; set; } = true;
    public PrinterProfile Profile { get; set; } = new();
}

public sealed class DriverPackage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string? SilentArgs { get; set; }
    public string? Notes { get; set; }
    public DateTime UploadedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ServerState
{
    public AdminRecord Admin { get; set; } = new();
    public bool SetupComplete { get; set; }
    public PrinterConfig Printer { get; set; } = new();
    public List<ClientRecord> Clients { get; set; } = new();
    public List<DriverPackage> Drivers { get; set; } = new();
    public string? SelectedDriverId { get; set; }
}

public sealed class SessionRecord
{
    public string Token { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public string Username { get; set; } = "";
}

public static class ServerCrypto
{
    public static string NewToken(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static (string hash, string salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPassword(string password, string hashB64, string saltB64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}

public sealed class ServerStore
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _statePath;
    public ServerState State { get; private set; } = new();
    public string Dir => _dir;
    public string DriversDir => Path.Combine(_dir, "drivers");
    public string JobsDir => Path.Combine(_dir, "jobs");
    public string QueueDir => Path.Combine(_dir, "queue");

    private readonly Dictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);

    public ServerStore(string dataDir)
    {
        _dir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(DriversDir);
        Directory.CreateDirectory(JobsDir);
        Directory.CreateDirectory(QueueDir);
        _statePath = Path.Combine(_dir, "server.json");
        Load();
    }

    private void Load()
    {
        if (File.Exists(_statePath))
        {
            try { State = JsonSerializer.Deserialize<ServerState>(File.ReadAllText(_statePath), AppSettings.JsonOpts) ?? new ServerState(); }
            catch { State = new ServerState(); }
        }
        State.Admin ??= new AdminRecord();
        State.Printer ??= new PrinterConfig();
        State.Printer.Profile ??= new PrinterProfile();
        State.Clients ??= new();
        State.Drivers ??= new();
    }

    public void Save()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(State, AppSettings.JsonOpts);
            File.WriteAllText(_statePath + ".tmp", json);
            File.Move(_statePath + ".tmp", _statePath, true);
        }
    }

    public void EnsureAdmin(string? initialSecret)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(State.Admin.PasswordHash)) return; // already configured

            State.Admin.Username = string.IsNullOrWhiteSpace(State.Admin.Username) ? "admin" : State.Admin.Username;
            if (!string.IsNullOrWhiteSpace(initialSecret))
            {
                var (hash, salt) = ServerCrypto.HashPassword(initialSecret.Trim());
                State.Admin.PasswordHash = hash;
                State.Admin.PasswordSalt = salt;
                State.Admin.MustChangePassword = false;
                State.SetupComplete = true;
                Save();
            }
            else
            {
                // First run with no preconfigured secret: require interactive setup via web UI/API.
                State.Admin.PasswordHash = "";
                State.Admin.PasswordSalt = "";
                State.Admin.MustChangePassword = false;
                State.SetupComplete = false;
                Save();
                Log.Info("Admin setup required: open the web UI to create the initial admin password.");
            }
        }
    }

    public bool NeedsSetup
    {
        get { lock (_gate) return string.IsNullOrEmpty(State.Admin.PasswordHash); }
    }

    public (bool ok, string? token, string? error) SetupInitialAdmin(string? username, string? password)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(State.Admin.PasswordHash))
                return (false, null, "Already configured.");
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
                return (false, null, "Password must be at least 6 characters.");
            var user = string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim();
            var (hash, salt) = ServerCrypto.HashPassword(password);
            State.Admin.Username = user;
            State.Admin.PasswordHash = hash;
            State.Admin.PasswordSalt = salt;
            State.Admin.MustChangePassword = false;
            State.SetupComplete = true;
            Save();
            var token = ServerCrypto.NewToken(32);
            _sessions[token] = new SessionRecord { Token = token, Username = State.Admin.Username, ExpiresUtc = DateTime.UtcNow.AddHours(12) };
            return (true, token, null);
        }
    }

    public (bool ok, string? token, string? error) Login(string username, string password)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(State.Admin.PasswordHash))
                return (false, null, "Initial setup required.");
            if (!string.Equals(username?.Trim(), State.Admin.Username, StringComparison.OrdinalIgnoreCase))
                return (false, null, "Invalid credentials.");
            if (!ServerCrypto.VerifyPassword(password ?? "", State.Admin.PasswordHash, State.Admin.PasswordSalt))
                return (false, null, "Invalid credentials.");
            var token = ServerCrypto.NewToken(32);
            _sessions[token] = new SessionRecord { Token = token, Username = State.Admin.Username, ExpiresUtc = DateTime.UtcNow.AddHours(12) };
            return (true, token, null);
        }
    }

    public bool ValidateSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(token, out var s)) return false;
            if (s.ExpiresUtc < DateTime.UtcNow) { _sessions.Remove(token); return false; }
            s.ExpiresUtc = DateTime.UtcNow.AddHours(12);
            return true;
        }
    }

    public void Logout(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        lock (_gate) _sessions.Remove(token);
    }

    public bool ChangePassword(string current, string next)
    {
        lock (_gate)
        {
            if (!ServerCrypto.VerifyPassword(current, State.Admin.PasswordHash, State.Admin.PasswordSalt)) return false;
            var (hash, salt) = ServerCrypto.HashPassword(next);
            State.Admin.PasswordHash = hash; State.Admin.PasswordSalt = salt; State.Admin.MustChangePassword = false; State.SetupComplete = true;
            Save();
            return true;
        }
    }

    public ClientRecord AddPendingClient(string name, string machine)
    {
        lock (_gate)
        {
            var c = new ClientRecord { Name = name?.Trim() ?? "", Machine = machine?.Trim() ?? "", Status = nameof(ClientStatus.Pending) };
            State.Clients.Add(c);
            Save();
            return c;
        }
    }

    public (ClientRecord? client, string? token) ApproveClient(string id)
    {
        lock (_gate)
        {
            var c = State.Clients.FirstOrDefault(x => x.Id == id);
            if (c == null) return (null, null);
            var token = ServerCrypto.NewToken(24);
            c.Status = nameof(ClientStatus.Approved);
            c.TokenHash = ServerCrypto.Sha256Hex(token);
            c.PendingToken = token;
            c.ApprovedUtc = DateTime.UtcNow;
            Save();
            return (c, token);
        }
    }

    public string? ClaimClientToken(string id)
    {
        lock (_gate)
        {
            var c = State.Clients.FirstOrDefault(x => x.Id == id);
            if (c == null) return null;
            if (!string.Equals(c.Status, nameof(ClientStatus.Approved), StringComparison.OrdinalIgnoreCase)) return null;
            var token = c.PendingToken;
            if (!string.IsNullOrEmpty(token))
            {
                c.PendingToken = null;
                Save();
            }
            return token;
        }
    }

    public bool SetClientStatus(string id, ClientStatus status)
    {
        lock (_gate)
        {
            var c = State.Clients.FirstOrDefault(x => x.Id == id);
            if (c == null) return false;
            c.Status = status.ToString();
            if (status is ClientStatus.Revoked or ClientStatus.Declined) { c.TokenHash = ""; c.PendingToken = null; }
            Save();
            return true;
        }
    }

    public bool RemoveClient(string id)
    {
        lock (_gate)
        {
            var n = State.Clients.RemoveAll(x => x.Id == id);
            if (n == 0) return false;
            Save();
            return true;
        }
    }

    public ClientRecord? ValidateClientToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = ServerCrypto.Sha256Hex(token);
        lock (_gate)
        {
            var c = State.Clients.FirstOrDefault(x => x.Status == nameof(ClientStatus.Approved) && string.Equals(x.TokenHash, hash, StringComparison.OrdinalIgnoreCase));
            if (c != null) { c.LastSeenUtc = DateTime.UtcNow; }
            return c;
        }
    }

    public ClientRecord? GetClient(string id) { lock (_gate) return State.Clients.FirstOrDefault(x => x.Id == id); }

    public DriverPackage AddDriver(string fileName, string displayName, string version, byte[] data, string? silentArgs, string? notes)
    {
        var id = Guid.NewGuid().ToString("N");
        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(fileName) ? (id + ".bin") : fileName);
        var stored = id + "_" + safeName;
        File.WriteAllBytes(Path.Combine(DriversDir, stored), data);
        var pkg = new DriverPackage
        {
            Id = id, FileName = stored, DisplayName = string.IsNullOrWhiteSpace(displayName) ? safeName : displayName.Trim(),
            Version = version?.Trim() ?? "", Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            Size = data.LongLength, SilentArgs = silentArgs, Notes = notes
        };
        lock (_gate) { State.Drivers.Add(pkg); if (State.SelectedDriverId == null) State.SelectedDriverId = id; Save(); }
        return pkg;
    }

    public bool RemoveDriver(string id)
    {
        lock (_gate)
        {
            var d = State.Drivers.FirstOrDefault(x => x.Id == id);
            if (d == null) return false;
            try { File.Delete(Path.Combine(DriversDir, d.FileName)); } catch { }
            State.Drivers.Remove(d);
            if (State.SelectedDriverId == id) State.SelectedDriverId = State.Drivers.FirstOrDefault()?.Id;
            Save();
            return true;
        }
    }

    public DriverPackage? GetDriver(string id) { lock (_gate) return State.Drivers.FirstOrDefault(x => x.Id == id); }

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 120 ? name[^120..] : name;
    }
}
