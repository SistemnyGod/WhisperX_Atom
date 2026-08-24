using System.Diagnostics;
using System.Security.Cryptography;
using WhisperX.Atom.Voice;

namespace WhisperX.Atom.Voice.Host;

/// <summary>
/// Optional CPU-only second pass.  It is intentionally isolated from the
/// command arbiter: a failure or disagreement is diagnostic only.
/// </summary>
public sealed class WhisperCppVoiceRefiner : IVoiceAsrRefiner
{
    private readonly WhisperCppRecognizer? _recognizer;
    private readonly string _temporaryDirectory;
    private readonly TimeSpan _timeout;
    private readonly string? _unavailableError;

    public WhisperCppVoiceRefiner(string executablePath, string modelPath, string temporaryDirectory, TimeSpan timeout)
    {
        Provider = "whisper.cpp";
        Model = Path.GetFileName(modelPath);
        _temporaryDirectory = Path.GetFullPath(temporaryDirectory);
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(8) : timeout;
        var expectedHash = Environment.GetEnvironmentVariable("VOICE_ASR_REFINER_SHA256")?.Trim();
        if (!File.Exists(executablePath)) { _unavailableError = "VOICE_REFINER_EXECUTABLE_MISSING"; return; }
        if (!File.Exists(modelPath)) { _unavailableError = "VOICE_REFINER_MODEL_MISSING"; return; }
        if (string.IsNullOrWhiteSpace(expectedHash)) { _unavailableError = "VOICE_REFINER_MODEL_SHA256_MISSING"; return; }
        if (!IsSha256(modelPath, expectedHash))
        {
            _unavailableError = "VOICE_REFINER_MODEL_INTEGRITY_FAILED";
            return;
        }
        try
        {
            Directory.CreateDirectory(_temporaryDirectory);
            CleanupOrphans(_temporaryDirectory);
            _recognizer = new WhisperCppRecognizer(executablePath, modelPath, _temporaryDirectory, "voice-shadow");
        }
        catch { _unavailableError = "VOICE_REFINER_INIT_FAILED"; }
    }

    public string Provider { get; }
    public string Model { get; }
    public bool IsAvailable => _recognizer is not null && _unavailableError is null;

    public async Task<VoiceRefinementResult> RefineAsync(VoiceUtteranceEnvelope utterance, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return new(VoiceRefinementState.Unavailable, Provider: Provider, Model: Model, ErrorCode: _unavailableError);
        if (utterance.Pcm16kMono.Length < 1600)
            return new(VoiceRefinementState.NoSpeech, Provider: Provider, Model: Model, ErrorCode: "VOICE_REFINER_NO_SPEECH");

        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var text = await _recognizer!.RecognizeAsync(utterance.Pcm16kMono, timeout.Token).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (string.IsNullOrWhiteSpace(text))
                return new(VoiceRefinementState.NoSpeech, Provider: Provider, Model: Model, ProcessingMs: elapsed, ErrorCode: "VOICE_REFINER_NO_SPEECH");
            return new(VoiceRefinementState.Ready, text.Trim(), Provider, Model, ProcessingMs: elapsed);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new(VoiceRefinementState.Timeout, Provider: Provider, Model: Model,
                ProcessingMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds, ErrorCode: "VOICE_REFINER_TIMEOUT");
        }
        catch
        {
            return new(VoiceRefinementState.Failed, Provider: Provider, Model: Model,
                ProcessingMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds, ErrorCode: "VOICE_REFINER_FAILED");
        }
    }

    public void Dispose() => _recognizer?.Dispose();

    private static bool IsSha256(string path, string expected)
    {
        if (expected.Length != 64 || expected.Any(static c => !Uri.IsHexDigit(c))) return false;
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupOrphans(string directory)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "voice-shadow-*.wav", SearchOption.TopDirectoryOnly))
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-1)) File.Delete(path);
        }
        catch { /* diagnostics must never prevent Voice Host startup */ }
    }
}
