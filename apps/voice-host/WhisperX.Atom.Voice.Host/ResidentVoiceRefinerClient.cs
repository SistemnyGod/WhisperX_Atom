using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
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
    private readonly string _manifestPath;
    private readonly string _modelHash;
    private readonly string _nativeHash;
    private readonly string _expectedBuildIdentity;
    private readonly int _nativeAbiVersion;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _hostTimeout;
    private readonly object _gate = new();
    private readonly FileAttestationCache _attestations = new();
    private readonly string? _manifestError;
    private Process? _process;
    private int _restartCount;
    private bool _disposed;

    public ResidentVoiceRefinerClient(string executablePath, string modelPath, string nativeLibraryPath, TimeSpan timeout, string expectedBuildIdentity, TimeSpan? hostTimeout = null)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _modelPath = Path.GetFullPath(modelPath);
        _nativeLibraryPath = Path.GetFullPath(nativeLibraryPath);
        _manifestPath = Path.GetFullPath(Environment.GetEnvironmentVariable("VOICE_ASR_REFINER_MANIFEST") is { Length: > 0 } configuredManifest
            ? configuredManifest
            : Path.Combine(Path.GetDirectoryName(_modelPath) ?? AppContext.BaseDirectory, "voice-refiner.manifest.json"));
        var manifest = ReadManifestInfo(_modelPath, _nativeLibraryPath, _manifestPath);
        var configuredModelHash = ReadConfiguredHash("VOICE_ASR_REFINER_SHA256");
        var configuredNativeHash = ReadConfiguredHash("VOICE_ASR_REFINER_NATIVE_SHA256");
        _modelHash = configuredModelHash ?? manifest.ModelHash ?? string.Empty;
        _nativeHash = configuredNativeHash ?? manifest.NativeHash ?? string.Empty;
        _nativeAbiVersion = manifest.NativeAbiVersion ?? VoiceRefinerProtocol.NativeAbiVersion;
        _manifestError = manifest.Error
            ?? (configuredModelHash is not null && manifest.ModelHash is not null && !HashesEqual(configuredModelHash, manifest.ModelHash) ? "VOICE_REFINER_ASSET_CHANGED" : null)
            ?? (configuredNativeHash is not null && manifest.NativeHash is not null && !HashesEqual(configuredNativeHash, manifest.NativeHash) ? "VOICE_REFINER_ASSET_CHANGED" : null)
            ?? (manifest.BuildIdentity is not null && !string.IsNullOrWhiteSpace(expectedBuildIdentity) && !string.Equals(manifest.BuildIdentity, expectedBuildIdentity, StringComparison.OrdinalIgnoreCase) ? "VOICE_REFINER_BUILD_IDENTITY_MISMATCH" : null)
            ?? (BridgeRevisionMatchesIdentity(manifest.BridgeRevision, expectedBuildIdentity) ? null : "VOICE_REFINER_BRIDGE_IDENTITY_MISMATCH");
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(VoiceRefinerProtocol.ClientTimeoutMs) : timeout;
        _hostTimeout = hostTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : TimeSpan.FromMilliseconds(VoiceRefinerProtocol.HostInferenceTimeoutMs);
        _expectedBuildIdentity = expectedBuildIdentity ?? string.Empty;
        Provider = "whisper.cpp-native";
        Model = Path.GetFileName(modelPath);
    }

    public string Provider { get; }
    public string Model { get; }
    public string? AvailabilityError
    {
        get
        {
            if (_manifestError is not null) return _manifestError;
            if (_nativeAbiVersion != VoiceRefinerProtocol.NativeAbiVersion) return "VOICE_REFINER_NATIVE_ABI_MISMATCH";
            if (!HostIdentityMatches()) return "VOICE_REFINER_BUILD_IDENTITY_MISMATCH";
            if (!File.Exists(_modelPath) || !File.Exists(_nativeLibraryPath)) return "VOICE_REFINER_ASSETS_UNAVAILABLE";
            if (!VerifyHashCached(_modelPath, _modelHash) || !VerifyHashCached(_nativeLibraryPath, _nativeHash)) return "VOICE_REFINER_ASSET_CHANGED";
            return null;
        }
    }
    public int RestartCount => Volatile.Read(ref _restartCount);
    public bool IsAvailable => AvailabilityError is null;

    public async Task<VoiceRefinementResult> RefineAsync(VoiceUtteranceEnvelope utterance, CancellationToken cancellationToken)
    {
        if (_disposed) return Failure(utterance, "VOICE_REFINER_DISPOSED");
        if (_manifestError is not null) return Failure(utterance, _manifestError);
        if (_nativeAbiVersion != VoiceRefinerProtocol.NativeAbiVersion) return Failure(utterance, "VOICE_REFINER_NATIVE_ABI_MISMATCH");
        if (!IsAvailable) return Failure(utterance, AvailabilityError ?? "VOICE_REFINER_ASSETS_UNAVAILABLE");
        if (utterance.Pcm16kMono.Length is < 1600 or > VoiceRefinerProtocol.MaxPcmBytes)
            return Failure(utterance, utterance.Pcm16kMono.Length < 1600 ? "VOICE_REFINER_NO_SPEECH" : "VOICE_REFINER_UTTERANCE_TOO_LARGE");

        var started = Stopwatch.GetTimestamp();
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(_timeout);
        var operationToken = operationTimeout.Token;
        try
        {
            await EnsureProcessAsync(operationToken).ConfigureAwait(false);
            await using var pipe = new NamedPipeClientStream(".", VoiceRefinerProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(operationToken).ConfigureAwait(false);
            var request = new VoiceRefinerRequest(VoiceRefinerProtocol.SchemaVersion, Guid.NewGuid().ToString("N"), "refine", _expectedBuildIdentity, utterance.UtteranceId, utterance.Sequence, 16_000, utterance.Pcm16kMono.Length, VoiceRefinerProtocol.NativeAbiVersion);
            var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions) + "\n");
            await pipe.WriteAsync(header, operationToken).ConfigureAwait(false);
            await pipe.WriteAsync(BitConverter.GetBytes(utterance.Pcm16kMono.Length), operationToken).ConfigureAwait(false);
            await pipe.WriteAsync(utterance.Pcm16kMono, operationToken).ConfigureAwait(false);
            await pipe.FlushAsync(operationToken).ConfigureAwait(false);
            var line = await ReadLineAsync(pipe, operationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<VoiceRefinerResponse>(line ?? string.Empty, JsonOptions);
            if (response is null) return Failure(utterance, "VOICE_REFINER_INVALID_RESPONSE", started);
            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                return Failure(utterance, "VOICE_REFINER_RESPONSE_CORRELATION_FAILED", started);
            if (response.NativeAbiVersion is { } responseAbi && responseAbi != VoiceRefinerProtocol.NativeAbiVersion)
                return Failure(utterance, "VOICE_REFINER_NATIVE_ABI_MISMATCH", started);
            if (response.SampleRate is { } responseSampleRate && responseSampleRate != 16_000)
                return Failure(utterance, "VOICE_REFINER_SAMPLE_RATE_INVALID", started);
            if (response.Ok && (!string.Equals(response.BuildIdentity, _expectedBuildIdentity, StringComparison.Ordinal)
                || !string.Equals(response.UtteranceId, utterance.UtteranceId, StringComparison.Ordinal)
                || response.Sequence != utterance.Sequence
                || response.SampleRate != 16_000))
                return Failure(utterance, "VOICE_REFINER_RESPONSE_CORRELATION_FAILED", started);
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
        catch (TimeoutException)
        {
            await StopProcessAsync().ConfigureAwait(false);
            return Failure(utterance, "VOICE_REFINER_TIMEOUT", started, VoiceRefinementState.Timeout);
        }
        catch (EndOfStreamException)
        {
            var exitCode = CurrentExitCode();
            await StopProcessAsync().ConfigureAwait(false);
            return Failure(utterance, exitCode == VoiceRefinerProtocol.InferenceTimeoutExitCode ? "VOICE_REFINER_TIMEOUT" : "VOICE_REFINER_HOST_UNAVAILABLE", started, exitCode == VoiceRefinerProtocol.InferenceTimeoutExitCode ? VoiceRefinementState.Timeout : VoiceRefinementState.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(utterance, "VOICE_REFINER_CANCELLED", started);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync().ConfigureAwait(false);
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
                    ["VOICE_REFINER_MANIFEST"] = _manifestPath,
                    ["VOICE_REFINER_NATIVE_ABI_VERSION"] = VoiceRefinerProtocol.NativeAbiVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["VOICE_REFINER_HOST_TIMEOUT_MS"] = ((int)_hostTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["WHISPERX_BUILD_IDENTITY"] = _expectedBuildIdentity
                }
            });
            if (_process is null) throw new InvalidOperationException("VOICE_REFINER_HOST_START_FAILED");
            _restartCount++;
            _ = DrainStderrAsync(_process.StandardError);
        }
        await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        if (_process is { HasExited: true }) throw new InvalidOperationException("VOICE_REFINER_HOST_EXITED");
    }

    private int? CurrentExitCode()
    {
        lock (_gate)
        {
            try { return _process is { HasExited: true } process ? process.ExitCode : null; }
            catch { return null; }
        }
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
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            if (buffer.Length > 16 * 1024) throw new InvalidDataException("VOICE_REFINER_RESPONSE_TOO_LARGE");
            buffer.WriteByte(one[0]);
        }
    }

    private VoiceRefinementResult Failure(VoiceUtteranceEnvelope u, string code, long? started = null, VoiceRefinementState state = VoiceRefinementState.Unavailable)
        => new(state, Provider: Provider, Model: Model, ProcessingMs: started is null ? 0 : Stopwatch.GetElapsedTime(started.Value).TotalMilliseconds, ErrorCode: code, UtteranceId: u.UtteranceId, Sequence: u.Sequence);

    private static VoiceRefinementResult Failure(VoiceUtteranceEnvelope u, string code, long started, VoiceRefinementState state = VoiceRefinementState.Failed)
        => new(state, Provider: "whisper.cpp-native", Model: null, ProcessingMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds, ErrorCode: code, UtteranceId: u.UtteranceId, Sequence: u.Sequence);

    private static VoiceRefinementState MapState(string? error) => error switch
    {
        "VOICE_REFINER_TIMEOUT" => VoiceRefinementState.Timeout,
        "VOICE_REFINER_NO_SPEECH" => VoiceRefinementState.NoSpeech,
        "VOICE_REFINER_NATIVE_ABI_MISMATCH" or
        "VOICE_REFINER_MANIFEST_UPGRADE_REQUIRED" or
        "VOICE_REFINER_ASSET_CHANGED" or
        "VOICE_REFINER_ASSETS_UNAVAILABLE" or
        "VOICE_REFINER_BUILD_IDENTITY_MISMATCH" => VoiceRefinementState.Unavailable,
        _ => VoiceRefinementState.Failed
    };

    private static bool ContainsWakeWord(string text)
        => text.Contains("мифодий", StringComparison.OrdinalIgnoreCase) || text.Contains("мефодий", StringComparison.OrdinalIgnoreCase);

    private bool VerifyHashCached(string path, string expected) => _attestations.Verify(path, expected);

    private static string? ReadConfiguredHash(string name)
        => Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private bool HostIdentityMatches()
    {
        if (!File.Exists(_executablePath)) return false;
        if (string.IsNullOrWhiteSpace(_expectedBuildIdentity)) return true;
        try { return string.Equals(FileVersionInfo.GetVersionInfo(_executablePath).ProductVersion, _expectedBuildIdentity, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // Kept as a named compatibility helper for existing contract checks.
    private static (string? ModelHash, string? NativeHash) ReadManifestHashes(string modelPath, string nativePath)
    {
        var info = ReadManifestInfo(modelPath, nativePath, Path.Combine(Path.GetDirectoryName(modelPath) ?? AppContext.BaseDirectory, "voice-refiner.manifest.json"));
        return (info.ModelHash, info.NativeHash);
    }

    private static ManifestInfo ReadManifestInfo(string modelPath, string nativePath, string manifestPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrWhiteSpace(directory)) return new(null, null, null, "VOICE_REFINER_MANIFEST_MISSING");
            if (!File.Exists(manifestPath)) return new(null, null, null, "VOICE_REFINER_MANIFEST_MISSING");
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 2)
                return new(null, null, null, "VOICE_REFINER_MANIFEST_UPGRADE_REQUIRED");
            if (!root.TryGetProperty("provider", out var provider) || !string.Equals(provider.GetString(), "whisper.cpp-native", StringComparison.Ordinal))
                return new(null, null, null, "VOICE_REFINER_MANIFEST_INVALID");
            var model = root.GetProperty("model");
            var native = root.GetProperty("native");
            var modelFile = model.GetProperty("file").GetString();
            var nativeFile = native.GetProperty("file").GetString();
            if (!string.Equals(Path.GetFileName(modelFile), Path.GetFileName(modelPath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(nativeFile), Path.GetFileName(nativePath), StringComparison.OrdinalIgnoreCase))
                return new(null, null, null, "VOICE_REFINER_MANIFEST_INVALID");
            var modelHash = model.GetProperty("sha256").GetString();
            var nativeHash = native.GetProperty("sha256").GetString();
            var abi = native.GetProperty("abiVersion").GetInt32();
            if (!IsSha256(modelHash) || !IsSha256(nativeHash)
                || string.IsNullOrWhiteSpace(model.GetProperty("source").GetString())
                || !IsRevision(model.GetProperty("revision").GetString())
                || !IsRevision(native.GetProperty("whisperCppRevision").GetString())
                || !IsRevision(native.GetProperty("bridgeRevision").GetString()))
                return new(null, null, null, "VOICE_REFINER_MANIFEST_INVALID");
            var identity = root.TryGetProperty("buildIdentity", out var identityElement) ? identityElement.GetString() : null;
            return new(modelHash, nativeHash, abi, null, identity, native.GetProperty("bridgeRevision").GetString());
        }
        catch { return new(null, null, null, "VOICE_REFINER_MANIFEST_INVALID"); }
    }

    private sealed record ManifestInfo(string? ModelHash, string? NativeHash, int? NativeAbiVersion, string? Error, string? BuildIdentity = null, string? BridgeRevision = null);

    private sealed class FileAttestationCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Attestation> _entries = new(StringComparer.OrdinalIgnoreCase);

        public bool Verify(string path, string expected)
        {
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !File.Exists(path)) return false;
            try
            {
                var fullPath = Path.GetFullPath(path);
                var info = new FileInfo(fullPath);
                var key = fullPath + "|" + expected.ToLowerInvariant();
                lock (_gate)
                {
                    if (_entries.TryGetValue(key, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
                        return cached.Valid;
                }
                using var stream = File.OpenRead(fullPath);
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                var valid = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                lock (_gate) _entries[key] = new(info.Length, info.LastWriteTimeUtc, valid);
                return valid;
            }
            catch { return false; }
        }

        private sealed record Attestation(long Length, DateTime LastWriteUtc, bool Valid);
    }

    private static bool HashesEqual(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool BridgeRevisionMatchesIdentity(string? bridgeRevision, string? identity)
    {
        if (string.IsNullOrWhiteSpace(bridgeRevision) || string.IsNullOrWhiteSpace(identity)) return true;
        var marker = System.Text.RegularExpressions.Regex.Match(identity, @"\+([0-9a-fA-F]{40})(?:$|-)");
        return !marker.Success || string.Equals(marker.Groups[1].Value, bridgeRevision, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsRevision(string? value) => value is not null && value.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
