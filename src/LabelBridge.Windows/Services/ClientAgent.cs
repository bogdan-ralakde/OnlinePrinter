using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LabelBridge.Core;

namespace LabelBridge.Services;

/// <summary>
/// Windows client agent: captures local print jobs, queues them durably, polls
/// server for approval/config, and uploads jobs to the Online Printer Server.
/// </summary>
public sealed class ClientAgent : IAsyncDisposable
{
    public const string AppVersion = "1.1.2";

    private readonly AppSettings _settings;
    private readonly JobStore _queue;
    private readonly JobStore _history;
    private readonly object _gate = new();
    private readonly List<Task> _tasks = new();
    private CancellationTokenSource? _cts;
    private LoopbackCapture? _capture;
    private ServerApiClient? _api;
    private string _status = "Stopped";

    public event Action<string>? StatusChanged;
    public event Action<PrintJob, string>? JobEvent; // job, message
    public event Action<ServerClientConfig>? ConfigUpdated;

    private string _latestVersion = "";
    private bool _clientUpdateAvailable;
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;
    private readonly object _updateGate = new();

    public string LatestVersion { get { lock (_updateGate) return _latestVersion; } }
    public bool ClientUpdateAvailable { get { lock (_updateGate) return _clientUpdateAvailable; } }
    public event Action<string, bool>? ClientUpdateInfo; // (message, hasUpdate)

    public ClientAgent(AppSettings settings)
    {
        _settings = settings;
        _queue = new JobStore(Path.Combine(AppPaths.Root, "client-queue"));
        _history = new JobStore(Path.Combine(AppPaths.Root, "history"), 500);
    }

    public bool IsRunning { get; private set; }
    public string Status { get { lock (_gate) return _status; } }
    public int PendingCount => _queue.Count;

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _tasks.Clear();
        EnsureApi();

        _capture = new LoopbackCapture(_settings.CapturePort, OnCapturedAsync, probe => Log.Debug("Driver probe: " + probe));
        _capture.Start();
        _tasks.Add(Task.Run(() => _capture.RunAsync(ct), ct));
        _tasks.Add(Task.Run(() => UploadLoopAsync(ct), ct));
        _tasks.Add(Task.Run(() => PollLoopAsync(ct), ct));
        _tasks.Add(Task.Run(() => ClientUpdateLoopAsync(ct), ct));

        IsRunning = true;
        SetStatus("Client started");
        Log.Info("Client agent started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning && _cts == null) return;
        try { _cts?.Cancel(); } catch { }
        try { _capture?.Stop(); } catch { }
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _capture?.Dispose(); _capture = null;
        _api?.Dispose(); _api = null;
        _cts?.Dispose(); _cts = null;
        _tasks.Clear();
        IsRunning = false;
        SetStatus("Stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void EnsureApi()
    {
        var url = (_settings.ServerUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url)) { _api?.Dispose(); _api = null; return; }
        if (_api != null && string.Equals(_api.BaseUrl, url, StringComparison.OrdinalIgnoreCase)) return;
        _api?.Dispose();
        _api = new ServerApiClient(url);
    }

    private Task OnCapturedAsync(PrintJob job)
    {
        _queue.Enqueue(job);
        Log.Info($"Captured job {job.Id} ({job.Size} bytes)");
        JobEvent?.Invoke(job, "Captured");
        _queue.Wake();
        return Task.CompletedTask;
    }

    public (bool ok, string message) QueueFile(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length == 0) return (false, "File is empty.");
            var job = new PrintJob { Name = Path.GetFileName(path), Source = "file", Data = data };
            _queue.Enqueue(job);
            _queue.Wake();
            JobEvent?.Invoke(job, "Imported");
            return (true, "Queued " + job.Name);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool ok, string message)> RegisterAsync(CancellationToken ct = default)
    {
        EnsureApi();
        if (_api == null) return (false, "Server URL is not set.");
        try
        {
            var (ok, requestId, status, message) = await _api.RegisterAsync(_settings.DeviceName, Environment.MachineName, ct);
            if (!ok) return (false, message);
            _settings.ClientRequestId = requestId;
            _settings.ClientStatus = status;
            _settings.ClientToken = "";
            _settings.Save();
            SetStatus($"Registration {status}. Waiting for admin approval...");
            return (true, $"Request submitted ({status}). Ask admin to approve this client.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<ServerClientConfig?> FetchConfigAsync(CancellationToken ct = default)
    {
        EnsureApi();
        if (_api == null || string.IsNullOrWhiteSpace(_settings.ClientToken)) return null;
        try
        {
            var cfg = await _api.GetConfigAsync(_settings.ClientToken, ct);
            if (cfg.Ok)
            {
                _settings.LastServerPrinterName = cfg.PrinterName;
                _settings.LastServerConfigUtc = DateTime.UtcNow;
                _settings.ClientStatus = "Approved";
                if (cfg.Driver != null) _settings.LastServerDriverId = cfg.Driver.Id;
                _settings.Save();
                ConfigUpdated?.Invoke(cfg);
            }
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Warn("Config fetch failed: " + ex.Message);
            return null;
        }
    }

    public async Task<(bool ok, string message, string? path)> DownloadDriverAsync(ServerDriverInfo driver, CancellationToken ct = default)
    {
        EnsureApi();
        if (_api == null || string.IsNullOrWhiteSpace(_settings.ClientToken)) return (false, "Not connected/approved.", null);
        try
        {
            var dir = Path.Combine(AppPaths.Root, "drivers");
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(driver.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
            var dest = Path.Combine(dir, San(driver.Id + "_" + driver.DisplayName) + ext);
            var (ok, path, err) = await _api.DownloadDriverAsync(_settings.ClientToken, driver.Id, dest, ct);
            if (!ok) return (false, err ?? "Download failed", null);
            if (!string.IsNullOrWhiteSpace(driver.Sha256))
            {
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, ct))).ToLowerInvariant();
                if (!hash.Equals(driver.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (false, "Downloaded driver hash mismatch.", path);
            }
            return (true, "Downloaded: " + path, path);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    public (bool ok, string message) InstallDownloadedDriver(string path, string? silentArgs)
    {
        try
        {
            if (!File.Exists(path)) return (false, "File not found: " + path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            ProcessStartInfo psi;
            if (ext == ".msi")
            {
                var args = $"/i \"{path}\" ";
                if (!string.IsNullOrWhiteSpace(silentArgs)) args += silentArgs; else args += "/qn /norestart";
                psi = new ProcessStartInfo("msiexec.exe", args) { UseShellExecute = true, Verb = "runas" };
            }
            else if (ext == ".exe")
            {
                var args = silentArgs ?? "";
                psi = new ProcessStartInfo(path, args) { UseShellExecute = true, Verb = "runas" };
            }
            else if (ext == ".inf")
            {
                psi = new ProcessStartInfo("pnputil.exe", $"/add-driver \"{path}\" /install") { UseShellExecute = true, Verb = "runas" };
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return (false, "Downloaded package saved. Extract/install it manually, then install/repair the Online Printer printer. Path: " + path);
            }
            var p = Process.Start(psi);
            return (true, "Installer started (approve UAC if prompted). After installation, use Install/Repair Printer. PID: " + (p?.Id.ToString() ?? "?"));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool ok, string message)> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var (ok, msg, _, _) = await CheckForUpdatesDetailedAsync(ct);
        return (ok, msg);
    }

    public async Task<(bool ok, string message, bool hasUpdate, IReadOnlyList<GitHubReleaseAsset> assets)> CheckForUpdatesDetailedAsync(CancellationToken ct = default)
    {
        using var api = new ServerApiClient("https://github.com");
        var repo = "bogdan-ralakde/OnlinePrinter";
        var (ok, cur, latest, url, has, err, assets) = await api.CheckUpdateAsync(repo, ct);

        var message = !ok
            ? $"Client update check failed: {(err ?? "unknown error")}"
            : has
                ? $"Client update available: {latest} (current {cur}). {url}"
                : $"Client up to date ({cur}). Latest {latest}. {url}";

        lock (_updateGate)
        {
            if (ok && !string.IsNullOrWhiteSpace(latest)) _latestVersion = latest;
            _clientUpdateAvailable = ok && has;
            _lastUpdateCheckUtc = DateTime.UtcNow;
        }
        try { ClientUpdateInfo?.Invoke(message, ok && has); } catch { }

        if (!ok) return (false, message, false, Array.Empty<GitHubReleaseAsset>());
        return (true, message, has, assets);
    }

    private async Task ClientUpdateLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await CheckForUpdatesDetailedAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Debug("Client update check: " + ex.Message); }

            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch { break; }
        }
    }

    public async Task<(bool ok, string message)> DownloadAndApplyClientUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var api = new ServerApiClient("https://github.com");
            var repo = "bogdan-ralakde/OnlinePrinter";
            var (ok, cur, latest, url, has, err, assets) = await api.CheckUpdateAsync(repo, ct);
            if (!ok) return (false, err ?? "Update check failed.");
            if (!has) return (true, $"Already up to date ({cur}).");

            var asset = ServerApiClient.SelectWindowsClientAsset(assets);
            if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return (false, "Latest release does not include a Windows client package/installer asset. Download manually from: " + (url ?? "GitHub releases"));

            var updatesDir = Path.Combine(AppPaths.Root, "updates");
            Directory.CreateDirectory(updatesDir);
            var work = Path.Combine(updatesDir, "client-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var downloadPath = Path.Combine(work, asset.Name);
            var (dok, _, derr) = await api.DownloadUrlToFileAsync(asset.BrowserDownloadUrl, downloadPath, ct);
            if (!dok) return (false, "Download failed: " + (derr ?? "unknown"));

            var expected = await api.TryGetExpectedSha256Async(assets, asset.Name, ct);
            if (!string.IsNullOrWhiteSpace(expected))
            {
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(downloadPath, ct))).ToLowerInvariant();
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return (false, "Downloaded update checksum verification failed. Aborting.");
            }

            // The release package contains one self-elevating setup EXE. Launch it directly.
            // No PowerShell or CMD helper is used for updates.
            var extractDir = Path.Combine(work, "pkg");
            Directory.CreateDirectory(extractDir);
            if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
            }
            else
            {
                File.Copy(downloadPath, Path.Combine(extractDir, Path.GetFileName(downloadPath)), true);
            }

            var setup = Directory.EnumerateFiles(extractDir, "OnlinePrinterClient-Setup.exe", SearchOption.AllDirectories).FirstOrDefault()
                        ?? Directory.EnumerateFiles(extractDir, "*Setup*.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(setup))
                return (false, "Update package does not contain OnlinePrinterClient-Setup.exe. Download the installer manually from: " + (url ?? "GitHub releases"));

            var installDir = AppContext.BaseDirectory.TrimEnd('\\');
            var psi = new System.Diagnostics.ProcessStartInfo(setup, $"--silent --launch --dir \"{installDir}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setup) ?? work
            };
            try
            {
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                return (false, "Update downloaded and verified, but could not start the setup: " + ex.Message + "\n\nPackage staged at: " + work);
            }

            _ = Task.Run(async () => { try { await Task.Delay(1500); Environment.Exit(0); } catch { } });
            return (true, $"Update {latest} downloaded{(expected != null ? " and verified" : "")}. Setup started; Online Printer Client will close to complete the update.");
        }
        catch (Exception ex)
        {
            return (false, "Update apply failed: " + ex.Message);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                EnsureApi();
                if (_api != null && !string.IsNullOrWhiteSpace(_settings.ClientRequestId) && string.IsNullOrWhiteSpace(_settings.ClientToken))
                {
                    var (ok, status, canClaim, err) = await _api.GetStatusAsync(_settings.ClientRequestId, ct);
                    if (ok)
                    {
                        _settings.ClientStatus = status;
                        if (string.Equals(status, "Approved", StringComparison.OrdinalIgnoreCase) && canClaim)
                        {
                            var (cok, token, st2, cerr) = await _api.ClaimTokenAsync(_settings.ClientRequestId, ct);
                            if (cok && !string.IsNullOrWhiteSpace(token))
                            {
                                _settings.ClientToken = token;
                                _settings.ClientStatus = st2;
                                _settings.Save();
                                SetStatus("Approved. Fetching printer configuration...");
                                await FetchConfigAsync(ct);
                            }
                            else if (!string.IsNullOrWhiteSpace(cerr))
                            {
                                SetStatus("Approved but token claim failed: " + cerr);
                            }
                        }
                        else if (string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase))
                        {
                            SetStatus("Waiting for admin approval...");
                        }
                        else if (string.Equals(status, "Revoked", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Declined", StringComparison.OrdinalIgnoreCase))
                        {
                            SetStatus("Client " + status.ToLowerInvariant() + " by server admin.");
                        }
                        _settings.Save();
                    }
                    else if (!string.IsNullOrWhiteSpace(err))
                    {
                        Log.Debug("Status poll: " + err);
                    }
                }
                else if (_api != null && !string.IsNullOrWhiteSpace(_settings.ClientToken))
                {
                    // periodic config refresh + best-effort server-pushed provisioning apply
                    var cfg = await FetchConfigAsync(ct);
                    if (cfg != null && !cfg.Ok && (cfg.Error ?? "").Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                    {
                        _settings.ClientToken = "";
                        _settings.ClientStatus = "Revoked";
                        _settings.Save();
                        SetStatus("Access revoked or token invalid. Request approval again.");
                    }
                    else if (cfg != null && cfg.Ok)
                    {
                        _ = TryAutoApplyProvisioningAsync(cfg, ct); // fire-and-forget, guarded internally
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Debug("Poll error: " + ex.Message); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
        }
    }

    private async Task UploadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                EnsureApi();
                if (_api == null || string.IsNullOrWhiteSpace(_settings.ClientToken))
                {
                    await Task.Delay(1500, ct);
                    continue;
                }

                if (!_queue.TryPeek(DateTime.UtcNow, out var path, out var job))
                {
                    try { await _queue.Signal.WaitAsync(1500, ct); } catch (OperationCanceledException) { }
                    continue;
                }

                var (ok, jobId, msg) = await _api.UploadJobAsync(_settings.ClientToken, job.Name, job.Data, ct);
                if (ok)
                {
                    _queue.Remove(path);
                    _queue.Archive(job, _history.Folder);
                    _settings.LifetimeJobsSent++;
                    _settings.Save();
                    SetStatus($"Uploaded job ({job.Size} bytes)");
                    JobEvent?.Invoke(job, "Uploaded " + (jobId ?? ""));
                }
                else
                {
                    var delay = Backoff(job.Attempts);
                    _queue.MarkFailed(path, job, msg, delay);
                    _settings.LifetimeJobsFailed++;
                    _settings.Save();
                    SetStatus($"Upload failed, retry in {delay.TotalSeconds:F0}s: {msg}");
                    JobEvent?.Invoke(job, "Upload failed: " + msg);
                    if (msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                    {
                        _settings.ClientToken = ""; _settings.ClientStatus = "Revoked"; _settings.Save();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Warn("Upload loop error: " + ex.Message);
                try { await Task.Delay(2000, ct); } catch { }
            }
        }
    }

    private static TimeSpan Backoff(int attempts)
    {
        var secs = Math.Min(120, Math.Pow(2, Math.Min(attempts, 6)));
        return TimeSpan.FromSeconds(Math.Max(2, secs));
    }

    private static string San(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 80 ? s[..80] : s;
    }

    private int _provisioningBusy;

    private async Task TryAutoApplyProvisioningAsync(ServerClientConfig cfg, CancellationToken ct)
    {
        if (cfg == null || !cfg.Ok) return;
        if (Interlocked.CompareExchange(ref _provisioningBusy, 1, 0) != 0) return;
        try
        {
            var changedProfile = _settings.LastServerPrinterName != (cfg.PrinterName ?? "") ||
                                 _settings.LastServerDriverId != (cfg.Driver?.Id ?? "");
            _settings.LastServerPrinterName = cfg.PrinterName ?? "";
            _settings.LastServerDriverId = cfg.Driver?.Id ?? "";
            _settings.LastServerConfigUtc = DateTime.UtcNow;
            _settings.Save();

            // Auto-download driver package if new and not yet downloaded/installed for this id
            string? downloadedPath = null;
            if (cfg.Driver != null && !string.IsNullOrWhiteSpace(cfg.Driver.Id))
            {
                var marker = Path.Combine(AppPaths.Root, "drivers", ".installed-" + San(cfg.Driver.Id));
                var needInstall = !File.Exists(marker) || _settings.LastInstalledDriverId != cfg.Driver.Id;
                if (needInstall)
                {
                    var (dok, dmsg, path) = await DownloadDriverAsync(cfg.Driver, ct);
                    if (dok && !string.IsNullOrWhiteSpace(path)) downloadedPath = path;
                    if (!dok) Log.Warn("Auto driver download: " + dmsg);

                    // Best-effort: if silent args provided or MSI, try installer once (may prompt UAC)
                    if (downloadedPath != null && ShouldAttemptAutoDriverInstall(cfg.Driver, downloadedPath, marker))
                    {
                        var (iok, imsg) = InstallDownloadedDriver(downloadedPath, cfg.Driver.SilentArgs);
                        Log.Info("Auto driver install attempt: " + imsg);
                        if (iok)
                        {
                            try { Directory.CreateDirectory(Path.GetDirectoryName(marker)!); File.WriteAllText(marker, DateTime.UtcNow.ToString("o")); } catch { }
                            _settings.LastInstalledDriverId = cfg.Driver.Id;
                            _settings.Save();
                        }
                    }
                }
            }

            // Best-effort printer setup/repair when server provides driver hint or profile changed
            var profile = cfg.Profile;
            var driverHint = profile?.DriverHint ?? cfg.Driver?.DisplayName;
            var make = profile?.Make ?? "";
            var model = profile?.Model ?? "";
            var language = profile?.PrintLanguage ?? "";
            if (!string.IsNullOrWhiteSpace(driverHint) || !string.IsNullOrWhiteSpace(make) || !string.IsNullOrWhiteSpace(model))
            {
                try
                {
                    var catalog = PrinterManager.LoadCatalog(force: true);
                    var installed = catalog.Find(_settings.VirtualPrinterName);
                    var desired = PrinterManager.PickDriver(catalog, driverHint ?? "", make, model, language) ?? driverHint;
                    if (installed == null || (!string.IsNullOrWhiteSpace(installed.Driver) && !string.Equals(installed.Driver, desired, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Attempt install/repair (admin required; may prompt UAC). Do only when changed/missing.
                        if (changedProfile || installed == null)
                        {
                            var res = PrinterManager.Install(_settings, includeRawPrinter: false,
                                driverOverride: desired, make: make, model: model, language: language, allowAnyDriver: true);
                            Log.Info("Auto printer install/repair: " + (res.Ok ? "OK" : "FAILED") + " - " + res.Output);
                            if (res.Ok) SetStatus($"Virtual printer installed/repaired: {make} {model} ({desired}).");
                        }
                    }
                }
                catch (Exception ex) { Log.Warn("Auto printer provisioning: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Provisioning apply error: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _provisioningBusy, 0);
        }
    }

    private static bool ShouldAttemptAutoDriverInstall(ServerDriverInfo driver, string path, string markerPath)
    {
        if (File.Exists(markerPath)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        // Auto-attempt for MSI with known args or INF; for EXE only when silent args are explicitly provided.
        if (ext == ".msi" || ext == ".inf") return true;
        if (ext == ".exe" && !string.IsNullOrWhiteSpace(driver.SilentArgs)) return true;
        return false;
    }

    private void SetStatus(string status)
    {
        bool changed;
        lock (_gate) { changed = _status != status; _status = status; }
        if (changed) { Log.Info(status); StatusChanged?.Invoke(status); }
    }
}
