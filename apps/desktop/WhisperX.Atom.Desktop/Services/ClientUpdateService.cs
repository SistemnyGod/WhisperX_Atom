using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

public enum ClientUpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    BlockedRecording,
    Incompatible,
    Installing,
    Deferred,
    Failed
}

public sealed class ClientUpdateService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly FrontendServices _services;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly object _gate = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private CancellationTokenSource _shutdown = new();
    private Task? _monitor;
    private bool _disposed;
    private ClientUpdateManifest? _manifest;
    private DateTimeOffset? _deferredUntilUtc;
    private CancellationTokenSource? _downloadCts;

    public ClientUpdateService(FrontendServices services)
    {
        _services = services;
        LoadPersistedState();
    }

    public ClientUpdateState State { get; private set; } = ClientUpdateState.Idle;
    public ClientUpdateManifest? Manifest { get { lock (_gate) return _manifest; } private set { lock (_gate) _manifest = value; } }
    public string? ErrorCode { get; private set; }
    public int DownloadPercent { get; private set; }
    public DateTimeOffset? LastCheckedAtUtc { get; private set; }
    public DateTimeOffset? DeferredUntilUtc => _deferredUntilUtc;
    public string CurrentVersion => typeof(ClientUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string CurrentBuildIdentity => typeof(ClientUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    public event Action? StateChanged;

    public void CancelDownload() => _downloadCts?.Cancel();

    public void StartMonitoring()
    {
        if (_monitor is not null) return;
        _monitor = Task.Run(async () =>
        {
            await CheckAsync().ConfigureAwait(false);
            using var timer = new PeriodicTimer(CheckInterval);
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
                await CheckAsync().ConfigureAwait(false);
        }, _shutdown.Token);
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        SetState(ClientUpdateState.Checking, null);
        try
        {
            var server = await _services.Backend.GetSystemVersionAsync(cancellationToken).ConfigureAwait(false);
            if (server is null || !Uri.TryCreate(_services.Backend.ApiUrl + "/", UriKind.Absolute, out var origin))
            {
                SetState(ClientUpdateState.Idle, "UPDATE_SERVER_UNAVAILABLE");
                return;
            }

            var channel = NormalizeChannel(_services.Settings.Load().UpdateChannel);
            var builder = new UriBuilder(new Uri(origin, "api/client-updates/latest"));
            builder.Query = $"channel={Uri.EscapeDataString(channel)}&currentVersion={Uri.EscapeDataString(CurrentVersion)}&currentBuildIdentity={Uri.EscapeDataString(CurrentBuildIdentity)}";
            using var response = await _http.GetAsync(builder.Uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            LastCheckedAtUtc = DateTimeOffset.UtcNow;
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                Manifest = null;
                SetState(ClientUpdateState.UpToDate, null);
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                SetState(ClientUpdateState.Failed, "UPDATE_MANIFEST_HTTP_ERROR");
                return;
            }
            var manifest = await response.Content.ReadFromJsonAsync<ClientUpdateManifest>(JsonOptions, cancellationToken).ConfigureAwait(false);
            if (manifest is null || !IsSafeManifest(manifest) || !IsCompatible(manifest, server.ReleaseVersion))
            {
                SetState(ClientUpdateState.Incompatible, "UPDATE_MANIFEST_INCOMPATIBLE");
                return;
            }
            if (string.Equals(manifest.BuildIdentity, CurrentBuildIdentity, StringComparison.Ordinal))
            {
                Manifest = null;
                SetState(ClientUpdateState.UpToDate, null);
                return;
            }
            if (_deferredUntilUtc is { } deferredUntil
                && deferredUntil > DateTimeOffset.UtcNow
                && string.Equals(manifest.BuildIdentity, Manifest?.BuildIdentity, StringComparison.Ordinal))
            {
                Manifest = manifest;
                SetState(ClientUpdateState.Deferred, null);
                return;
            }
            if (_deferredUntilUtc is not null && _deferredUntilUtc <= DateTimeOffset.UtcNow)
                _deferredUntilUtc = null;
            Manifest = manifest;
            SetState(await HasStagedPackageAsync(manifest, cancellationToken).ConfigureAwait(false) ? ClientUpdateState.ReadyToInstall : ClientUpdateState.Available, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { SetState(ClientUpdateState.Idle, null); }
        catch (HttpRequestException) { SetState(ClientUpdateState.Idle, "UPDATE_SERVER_UNAVAILABLE"); }
        catch (JsonException) { SetState(ClientUpdateState.Failed, "UPDATE_MANIFEST_INVALID"); }
        catch { SetState(ClientUpdateState.Failed, "UPDATE_CHECK_FAILED"); }
        }
        finally { _checkGate.Release(); }
    }

    public async Task<bool> DownloadAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var manifest = Manifest;
        if (manifest is null) return false;
        if (!await EnsureNotRecordingAsync(cancellationToken).ConfigureAwait(false)) return false;
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _downloadCts = downloadCts;
        var operationToken = downloadCts.Token;
        var staging = GetStagingDirectory(manifest.BuildIdentity);
        Directory.CreateDirectory(staging);
        var partPath = Path.Combine(staging, manifest.Package.FileName + ".part");
        var targetPath = Path.Combine(staging, manifest.Package.FileName);
        SetState(ClientUpdateState.Downloading, null);
        try
        {
            var baseUri = new Uri(new Uri(_services.Backend.ApiUrl.TrimEnd('/') + "/"), manifest.Package.DownloadUrl.TrimStart('/'));
            var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken).ConfigureAwait(false);
            if (existing > 0 && (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable
                || (response.StatusCode == System.Net.HttpStatusCode.PartialContent
                    && response.Content.Headers.ContentRange?.From != existing)))
            {
                existing = 0;
                File.Delete(partPath);
                response.Dispose();
                using var restartRequest = new HttpRequestMessage(HttpMethod.Get, baseUri);
                response = await _http.SendAsync(restartRequest, HttpCompletionOption.ResponseHeadersRead, operationToken).ConfigureAwait(false);
            }
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                response.Dispose();
                SetState(ClientUpdateState.Failed, "UPDATE_DOWNLOAD_HTTP_ERROR");
                return false;
            }
            if (response.StatusCode == System.Net.HttpStatusCode.OK && existing > 0)
            {
                existing = 0;
                File.Delete(partPath);
            }
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(operationToken).ConfigureAwait(false))
                await using (var destination = new FileStream(partPath, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    var buffer = new byte[128 * 1024];
                    long completed = existing;
                    int read;
                    while ((read = await source.ReadAsync(buffer, operationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), operationToken).ConfigureAwait(false);
                        completed += read;
                        DownloadPercent = manifest.Package.SizeBytes <= 0 ? 0 : (int)Math.Clamp(completed * 100 / manifest.Package.SizeBytes, 0, 100);
                        progress?.Report(DownloadPercent);
                    }
                    await destination.FlushAsync(operationToken).ConfigureAwait(false);
                }
            }
            finally { response.Dispose(); }
            if (new FileInfo(partPath).Length != manifest.Package.SizeBytes || !string.Equals(await ComputeSha256Async(partPath, operationToken), manifest.Package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(partPath); } catch { }
                SetState(ClientUpdateState.Failed, "UPDATE_PACKAGE_CHECKSUM_MISMATCH");
                return false;
            }
            if (!IsX64Pe(partPath)) { try { File.Delete(partPath); } catch { } SetState(ClientUpdateState.Failed, "UPDATE_PACKAGE_ARCHITECTURE_MISMATCH"); return false; }
            if (manifest.Package.AuthenticodeRequired && !await IsAuthenticodeValidAsync(partPath, operationToken).ConfigureAwait(false))
            {
                try { File.Delete(partPath); } catch { }
                SetState(ClientUpdateState.Failed, "UPDATE_SIGNATURE_INVALID");
                return false;
            }
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(partPath, targetPath);
            await PersistStateAsync(manifest, targetPath, operationToken).ConfigureAwait(false);
            SetState(ClientUpdateState.ReadyToInstall, null);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || operationToken.IsCancellationRequested) { SetState(ClientUpdateState.Available, "UPDATE_DOWNLOAD_CANCELLED"); return false; }
        catch (HttpRequestException) { SetState(ClientUpdateState.Failed, "UPDATE_DOWNLOAD_FAILED"); return false; }
        catch (IOException) { SetState(ClientUpdateState.Failed, "UPDATE_STAGING_IO_FAILED"); return false; }
        finally
        {
            if (ReferenceEquals(_downloadCts, downloadCts)) _downloadCts = null;
        }
    }

    public async Task<bool> InstallAsync(bool allowUnsignedPilot = false, CancellationToken cancellationToken = default)
    {
        var manifest = Manifest;
        if (manifest is null) return false;
        var unsignedPilot = string.Equals(manifest.Channel, "pilot", StringComparison.OrdinalIgnoreCase)
            && !manifest.Package.AuthenticodeRequired;
        if (unsignedPilot && !allowUnsignedPilot)
        {
            SetState(ClientUpdateState.Failed, "UPDATE_UNSIGNED_PILOT_CONFIRMATION_REQUIRED");
            return false;
        }
        if (!await EnsureNotRecordingAsync(cancellationToken).ConfigureAwait(false)) return false;
        var package = Path.Combine(GetStagingDirectory(manifest.BuildIdentity), manifest.Package.FileName);
        if (!File.Exists(package)) { SetState(ClientUpdateState.Failed, "UPDATE_PACKAGE_NOT_DOWNLOADED"); return false; }
        var updater = Path.Combine(AppContext.BaseDirectory, "WhisperX.Atom.Updater.exe");
        if (!File.Exists(updater)) { SetState(ClientUpdateState.Failed, "UPDATER_NOT_INSTALLED"); return false; }
        try
        {
            var start = new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(updater) ?? AppContext.BaseDirectory
            };
            start.ArgumentList.Add("--desktop-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add("--installer");
            start.ArgumentList.Add(package);
            start.ArgumentList.Add("--expected-identity");
            start.ArgumentList.Add(manifest.BuildIdentity);
            start.ArgumentList.Add("--expected-sha256");
            start.ArgumentList.Add(manifest.Package.Sha256);
            start.ArgumentList.Add("--expected-size");
            start.ArgumentList.Add(manifest.Package.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Process.Start(start);
            SetState(ClientUpdateState.Installing, null);
            return true;
        }
        catch { SetState(ClientUpdateState.Failed, "UPDATER_START_FAILED"); return false; }
    }

    public void Defer()
    {
        if (Manifest?.Mandatory == true)
        {
            SetState(ClientUpdateState.Available, null);
            return;
        }
        _deferredUntilUtc = DateTimeOffset.UtcNow.AddHours(24);
        _ = PersistDeferredStateAsync();
        SetState(ClientUpdateState.Deferred, null);
    }

    private async Task<bool> EnsureNotRecordingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _services.Recorder.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            var state = response.SessionStatus?.CaptureState
                ?? (response.Health?.ActiveSessionId is not null ? "RECORDING" : response.State);
            if (state is not null && (state.Equals("STARTING", StringComparison.OrdinalIgnoreCase)
                || state.Equals("RECORDING", StringComparison.OrdinalIgnoreCase)
                || state.Equals("PAUSED", StringComparison.OrdinalIgnoreCase)
                || state.Equals("FINALIZING", StringComparison.OrdinalIgnoreCase)))
            {
                SetState(ClientUpdateState.BlockedRecording, "UPDATE_BLOCKED_ACTIVE_RECORDING");
                return false;
            }
            return true;
        }
        catch { return true; }
    }

    private string GetStagingDirectory(string identity) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "Updates", "staging", Sanitize(identity));

    private async Task<bool> HasStagedPackageAsync(ClientUpdateManifest manifest, CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(GetStagingDirectory(manifest.BuildIdentity), manifest.Package.FileName);
            return File.Exists(path)
                && new FileInfo(path).Length == manifest.Package.SizeBytes
                && string.Equals(await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), manifest.Package.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async Task PersistStateAsync(ClientUpdateManifest manifest, string path, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(GetStagingDirectory(manifest.BuildIdentity), "update-state.json");
        var payload = new { manifest, path, progress = 100, downloadedAtUtc = DateTimeOffset.UtcNow, deferredUntilUtc = _deferredUntilUtc };
        var part = statePath + ".part";
        await File.WriteAllTextAsync(part, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(part, statePath, true);
        await PersistRootStateAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistDeferredStateAsync()
    {
        try
        {
            var manifest = Manifest;
            if (manifest is null) return;
            var payload = new { manifest, path = (string?)null, progress = DownloadPercent, downloadedAtUtc = (DateTimeOffset?)null, deferredUntilUtc = _deferredUntilUtc };
            await PersistRootStateAsync(payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* update deferral must never affect the application */ }
    }

    private void LoadPersistedState()
    {
        try
        {
            var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "Updates", "update-state.json");
            if (!File.Exists(statePath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = document.RootElement;
            if (root.TryGetProperty("deferredUntilUtc", out var deferred)
                && deferred.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(deferred.GetString(), out var deferredUntil)
                && deferredUntil > DateTimeOffset.UtcNow)
            {
                _deferredUntilUtc = deferredUntil;
                if (root.TryGetProperty("manifest", out var manifestElement))
                {
                    var manifest = manifestElement.Deserialize<ClientUpdateManifest>(JsonOptions);
                    if (manifest is not null && IsSafeManifest(manifest)) Manifest = manifest;
                }
            }
        }
        catch { /* stale/corrupt update state is ignored and replaced on next check */ }
    }

    private static async Task PersistRootStateAsync(object payload, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "Updates");
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, "update-state.json");
        var part = statePath + ".part";
        await File.WriteAllTextAsync(part, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(part, statePath, true);
    }

    private void SetState(ClientUpdateState state, string? errorCode)
    {
        State = state;
        ErrorCode = errorCode;
        StateChanged?.Invoke();
    }

    private static bool IsSafeManifest(ClientUpdateManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.ApiVersion != 1
            || !string.Equals(manifest.Product, "WhisperX Atom", StringComparison.Ordinal)
            || (!string.Equals(manifest.Channel, "stable", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(manifest.Channel, "pilot", StringComparison.OrdinalIgnoreCase))
            || !IsSemver(manifest.Version) || !IsSemver(manifest.MinServerVersion)
            || string.IsNullOrWhiteSpace(manifest.BuildIdentity)
            || !manifest.BuildIdentity.Contains('+', StringComparison.Ordinal)
            || manifest.BuildIdentity.Contains("dev", StringComparison.OrdinalIgnoreCase)
            || manifest.BuildIdentity.Contains("dirty", StringComparison.OrdinalIgnoreCase)
            || manifest.Package is null)
            return false;

        var package = manifest.Package;
        if (!IsSha256(package.PackageId) || !IsSha256(package.Sha256)
            || !string.Equals(package.PackageId, package.Sha256, StringComparison.OrdinalIgnoreCase)
            || package.SizeBytes <= 0 || string.IsNullOrWhiteSpace(package.FileName)
            || !string.Equals(package.FileName, Path.GetFileName(package.FileName), StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(package.FileName), ".exe", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(package.DownloadUrl)
            || !package.DownloadUrl.StartsWith("/api/client-updates/packages/", StringComparison.OrdinalIgnoreCase))
            return false;

        return !string.Equals(manifest.Channel, "stable", StringComparison.OrdinalIgnoreCase)
            || package.AuthenticodeRequired;
    }

    private static bool IsCompatible(ClientUpdateManifest manifest, string serverVersion)
    {
        if (!TryVersion(manifest.Version, out var target) || !TryVersion(CurrentReleaseVersion(), out var current)) return false;
        if (target < current) return false;
        return !TryVersion(manifest.MinServerVersion, out var minServer)
            ? false
            : TryVersion(serverVersion, out var server) && server >= minServer;
    }

    private static string CurrentReleaseVersion() => typeof(ClientUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private static bool TryVersion(string? value, out Version version)
    {
        var normalized = (value ?? "").Split('+', '-')[0];
        if (Version.TryParse(normalized, out var parsed))
        {
            version = parsed;
            return true;
        }
        version = new Version(0, 0);
        return false;
    }
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsSemver(string? value) => TryVersion(value, out _);
    private static string NormalizeChannel(string? channel) => string.Equals(channel, "pilot", StringComparison.OrdinalIgnoreCase) ? "pilot" : "stable";
    private static string Sanitize(string value) => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '+' or '-' ? ch : '_'));
    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
    private static bool IsX64Pe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            stream.Seek(0x3c, SeekOrigin.Begin);
            var peOffset = reader.ReadInt32();
            stream.Seek(peOffset + 4, SeekOrigin.Begin);
            return reader.ReadUInt16() == 0x8664;
        }
        catch { return false; }
    }
    private static async Task<bool> IsAuthenticodeValidAsync(string path, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("(Get-AuthenticodeSignature -LiteralPath $args[0]).Status"); start.ArgumentList.Add(path);
        using var process = Process.Start(start);
        if (process is null) return false;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 && output.Trim().Equals("Valid", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _http.Dispose();
        _checkGate.Dispose();
        _shutdown.Dispose();
    }
}
