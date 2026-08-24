using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperX.Atom.Voice;

namespace WhisperX.Atom.Voice.Host;

/// <summary>
/// Client for the isolated current-user refiner process. PCM is sent over a
/// private pipe and is never written to a file by this client.
/// </summary>
internal sealed class ResidentVoiceRefinerClient : IVoiceAsrRefiner
{
    private readonly string _executablePath;
    private readonly string _modelPath;
    private readonly string _nativeLibraryPath;
    private readonly string _modelHash;
    private readonly string _nativeHash;
    private readonly string _expectedBuildIdentity;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private Process? _process;
    private bool _disposed;

    public ResidentVoiceRefinerClient(string executablePath, string modelPath, string nativeLibraryPath, TimeSpan timeout, string expectedBuildIdentity)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _modelPath = Path.GetFullPath(modelPath);
        _nativeLibraryPath = Path.GetFullPath(nativeLibraryPath);
        var manifest = ReadManifestHashes(_modelPath, _nativeLibraryPath);
        _modelHash = ReadConfiguredHash("VOICE_ASR_REFINER_SHA256") ?? manifest.ModelHash ?? string.Empty;
        _nativeHash = ReadConfiguredHash("VOICE_ASR_REFINER_NATIVE_SHA256") ?? manifest.NativeHash ?? string.Empty;
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(8) : timeout;
        _expectedBuildIdentity = expectedBuildIdentity ?? string.Empty;
        Provider = "whisper.cpp-native";
        Model = Path.GetFileName(modelPath);
    }

    public string Provider { get; }
    public string Model { get; }
    public bool IsAvailable => HostIdentityMatches()
        && VerifyHash(_modelPath, _modelHash)
        && VerifyHash(_nativeLibraryPath, _nativeHash);

    public async Task<VoiceRefinementResult> RefineAsync(VoiceUtteranceEnvelope utterance, CancellationToken cancellationToken)
    {
        if (_disposed) return Failure(utterance, "VOICE_REFINER_DISPOSED");
        if (!IsAvailable) return Failure(utterance, "VOICE_REFINER_ASSETS_UNAVAILABLE");
        if (utterance.Pcm16kMono.Length is < 1600 or > VoiceRefinerProtocol.MaxPcmBytes)
            return Failure(utterance, utterance.Pcm16kMono.Length < 1600 ? "VOICE_REFINER_NO_SPEECH" : "VOICE_REFINER_UTTERANCE_TOO_LARGE");

        var started = Stopwatch.GetTimestamp();
        try
        {
            await EnsureProcessAsync(cancellationToken).ConfigureAwait(false);
            await using var pipe = new NamedPipeClientStream(".", VoiceRefinerProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)Math.Clamp(_timeout.TotalMilliseconds, 500, 30_000), cancellationToken).ConfigureAwait(false);
            var request = new VoiceRefinerRequest(VoiceRefinerProtocol.SchemaVersion, Guid.NewGuid().ToString("N"), "refine", _expectedBuildIdentity, utterance.UtteranceId, utterance.Sequence, 16_000, utterance.Pcm16kMono.Length);
            var header = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions) + "\n");
            await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(BitConverter.GetBytes(utterance.Pcm16kMono.Length), cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(utterance.Pcm16kMono, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            var line = await ReadLineAsync(pipe, cancellationToken).WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<VoiceRefinerResponse>(line ?? string.Empty, JsonOptions);
            if (response is null) return Failure(utterance, "VOICE_REFINER_INVALID_RESPONSE", started);
            return new(
                response.Ok && !string.IsNullOrWhiteSpace(response.Text) ? VoiceRefinementState.Ready : MapState(response.ErrorCode),
                response.Text,
                response.Provider ?? Provider,
                response.Model ?? Model,
                response.Confidence,
                response.ProcessingMs > 0 ? response.ProcessingMs : Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                response.ErrorCode,
                utterance.UtteranceId,
                utterance.Sequence,
                response.QueueWaitMs,
                null,
                response.Text is not null && ContainsWakeWord(response.Text));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(utterance, "VOICE_REFINER_CANCELLED", started);
        }
        catch (TimeoutException)
        {
            return Failure(utterance, "VOICE_REFINER_TIMEOUT", started, VoiceRefinementState.Timeout);
        }
        catch
        {
            await StopProcessAsync().ConfigureAwait(false);
            return Failure(utterance, "VOICE_REFINER_HOST_UNAVAILABLE", started);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopProcessAsync().GetAwaiter().GetResult();
    }

    private async Task EnsureProcessAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            if (!File.Exists(_executablePath)) throw new FileNotFoundException("VOICE_REFINER_HOST_MISSING", _executablePath);
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                Arguments = $"--server --parent-pid {Environment.ProcessId}",
                Environment =
                {
                    ["VOICE_REFINER_MODEL"] = _modelPath,
                    ["VOICE_REFINER_NATIVE_LIBRARY"] = _nativeLibraryPath,
                    ["VOICE_ASR_REFINER_SHA256"] = _modelHash,
                    ["VOICE_ASR_REFINER_NATIVE_SHA256"] = _nativeHash,
                    ["WHISPERX_BUILD_IDENTITY"] = _expectedBuildIdentity
                }
            });
            if (_process is null) throw new InvalidOperationException("VOICE_REFINER_HOST_START_FAILED");
            _ = DrainStderrAsync(_process.StandardError);
        }
        await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        if (_process is { HasExited: true }) throw new InvalidOperationException("VOICE_REFINER_HOST_EXITED");
    }

    private async Task StopProcessAsync()
    {
        Process? process;
        lock (_gate) { process = _process; _process = null; }
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        process.Dispose();
    }

    private static async Task DrainStderrAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } } catch { }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            if (one[0] == (byte)'\n') return System.Text.Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            if (buffer.Length > 16 * 1024) throw new InvalidDataException("VOICE_REFINER_RESPONSE_TOO_LARGE");
            buffer.WriteByte(one[0]);
        }
    }

    private VoiceRefinementResult Failure(VoiceUtteranceEnvelope u, string code, long? started = null, VoiceRefinementState state = VoiceRefinementState.Unavailable)
        => new(state, Provider: Provider, Model: Model, ProcessingMs: started is null ? 0 : Stopwatch.GetElapsedTime(started.Value).TotalMilliseconds, ErrorCode: code, UtteranceId: u.UtteranceId, Sequence: u.Sequence);

    private static VoiceRefinementResult Failure(VoiceUtteranceEnvelope u, string code, long started)
        => new(VoiceRefinementState.Failed, Provider: "whisper.cpp-native", Model: null, ProcessingMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds, ErrorCode: code, UtteranceId: u.UtteranceId, Sequence: u.Sequence);

    private static VoiceRefinementState MapState(string? error) => error switch
    {
        "VOICE_REFINER_TIMEOUT" => VoiceRefinementState.Timeout,
        "VOICE_REFINER_NO_SPEECH" => VoiceRefinementState.NoSpeech,
        _ => VoiceRefinementState.Failed
    };

    private static bool ContainsWakeWord(string text)
        => text.Contains("мифодий", StringComparison.OrdinalIgnoreCase) || text.Contains("мефодий", StringComparison.OrdinalIgnoreCase);

    private static bool VerifyHash(string path, string expected)
    {
        if (!File.Exists(path) || expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return false;
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadConfiguredHash(string name)
        => Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private bool HostIdentityMatches()
    {
        if (!File.Exists(_executablePath)) return false;
        if (string.IsNullOrWhiteSpace(_expectedBuildIdentity)) return true;
        try
        {
            var productVersion = FileVersionInfo.GetVersionInfo(_executablePath).ProductVersion;
            return string.Equals(productVersion, _expectedBuildIdentity, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static (string? ModelHash, string? NativeHash) ReadManifestHashes(string modelPath, string nativePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrWhiteSpace(directory)) return (null, null);
            var manifestPath = Path.Combine(directory, "voice-refiner.manifest.json");
            if (!File.Exists(manifestPath)) return (null, null);
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return (null, null);
            string? modelHash = null;
            string? nativeHash = null;
            var modelName = Path.GetFileName(modelPath);
            var nativeName = Path.GetFileName(nativePath);
            foreach (var entry in files.EnumerateArray())
            {
                if (!entry.TryGetProperty("path", out var pathValue) || !entry.TryGetProperty("sha256", out var hashValue)) continue;
                var name = pathValue.GetString();
                var hash = hashValue.GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(hash)) continue;
                if (string.Equals(Path.GetFileName(name), modelName, StringComparison.OrdinalIgnoreCase)) modelHash = hash.Trim();
                if (string.Equals(Path.GetFileName(name), nativeName, StringComparison.OrdinalIgnoreCase)) nativeHash = hash.Trim();
            }
            return (modelHash, nativeHash);
        }
        catch { return (null, null); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
