using System.Diagnostics;
using System.Text.Json;

namespace WhisperX.Atom.Voice.Host.Tts;

internal sealed class TtsHostProcessClient : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly string _expectedBuildIdentity;
    private readonly string _tempRoot;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly TimeSpan _startupTimeout = TimeSpan.FromSeconds(120);
    private readonly TimeSpan _synthesisTimeout = TimeSpan.FromSeconds(45);
    private bool _warm;

    public TtsHostProcessClient(string executablePath, string expectedBuildIdentity, string tempRoot)
    {
        _executablePath = executablePath;
        _expectedBuildIdentity = expectedBuildIdentity ?? string.Empty;
        _tempRoot = Path.GetFullPath(tempRoot);
    }

    public int? ProcessId { get { lock (_gate) return _process is { HasExited: false } p ? p.Id : null; } }

    public async Task<TtsHostResponse?> PingAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(new { schemaVersion = 1, id = Guid.NewGuid().ToString("N"), op = "ping", buildIdentity = _expectedBuildIdentity }, _warm ? TimeSpan.FromSeconds(3) : _startupTimeout, cancellationToken).ConfigureAwait(false);
        if (response?.Ok == true) _warm = true;
        return response;
    }

    public async Task<TtsHostResponse?> SynthesizeAsync(string text, string speaker, int sampleRate, int cpuThreads, CancellationToken cancellationToken)
        => await SendAsync(new { schemaVersion = 1, id = Guid.NewGuid().ToString("N"), op = "synthesize", text, speaker, sampleRate, cpuThreads }, _synthesisTimeout, cancellationToken).ConfigureAwait(false);

    public async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_executablePath)) return false;
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var response = await PingAsync(cancellationToken).ConfigureAwait(false);
        return response?.Ok == true && (string.IsNullOrWhiteSpace(_expectedBuildIdentity) || string.Equals(response.BuildIdentity, _expectedBuildIdentity, StringComparison.Ordinal));
    }

    public async Task<bool> RestartOnceAsync(CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        return await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            if (!Path.IsPathFullyQualified(_executablePath)) throw new InvalidOperationException("TTS_HOST_START_FAILED");
            var psi = new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"--parent-pid {Environment.ProcessId}"
            };
            // Keep the host and Voice Host on the same private temp root. This
            // is important when LocalAppData is redirected or unavailable: a
            // host writing to a different root would return an audioPath that
            // the parent must (correctly) reject.
            psi.Environment["ATOM_TTS_TEMP_ROOT"] = _tempRoot;
            psi.Environment["WHISPERX_BUILD_IDENTITY"] = _expectedBuildIdentity;
            _process = Process.Start(psi) ?? throw new InvalidOperationException("TTS_HOST_START_FAILED");
            _stdin = _process.StandardInput;
            _stdout = _process.StandardOutput;
            _ = DrainStderrAsync(_process.StandardError);
        }
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        if (_process is { HasExited: true }) throw new InvalidOperationException("TTS_HOST_EXITED");
    }

    private async Task<TtsHostResponse?> SendAsync(object request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_stdin is null || _stdout is null || _process is null || _process.HasExited) throw new InvalidOperationException("TTS_HOST_EXITED");
                _stdin.WriteLine(JsonSerializer.Serialize(request));
                _stdin.Flush();
            }
            var line = await _stdout!.ReadLineAsync(cancellationToken).AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidOperationException("TTS_HOST_EXITED");
            return JsonSerializer.Deserialize<TtsHostResponse>(line, JsonOptions);
        }
        finally { _requestGate.Release(); }
    }

    private static async Task DrainStderrAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } } catch { }
    }

    public async Task StopAsync()
    {
        Process? process;
        lock (_gate) process = _process;
        if (process is null) return;
        var gateHeld = false;
        try
        {
            gateHeld = await _requestGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!gateHeld) throw new TimeoutException("TTS_HOST_SHUTDOWN_BUSY");
            lock (_gate) { _stdin?.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, id = Guid.NewGuid().ToString("N"), op = "shutdown" })); _stdin?.Flush(); }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
        finally
        {
            if (gateHeld) _requestGate.Release();
            lock (_gate) { _stdin = null; _stdout = null; _process = null; _warm = false; }
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _requestGate.Dispose(); }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed record TtsHostResponse(
    bool Ok,
    string? State = null,
    string? Engine = null,
    string? Model = null,
    string? Voice = null,
    string? AudioPath = null,
    long DurationMs = 0,
    long SynthesisMs = 0,
    string? ErrorCode = null,
    string? BuildIdentity = null,
    long ModelLoadMs = 0,
    bool CacheHit = false);
