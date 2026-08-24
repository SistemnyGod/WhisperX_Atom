using WhisperX.Atom.Voice;

namespace WhisperX.Atom.Voice.Host;

/// <summary>
/// Backward-compatible type name for older integrations. The production
/// implementation is the resident named-pipe client; it never starts a
/// per-utterance process and never creates diagnostic WAV files.
/// </summary>
[Obsolete("Use ResidentVoiceRefinerClient through VoiceHostRuntime.")]
public sealed class WhisperCppVoiceRefiner : IVoiceAsrRefiner
{
    private readonly ResidentVoiceRefinerClient _inner;

    public WhisperCppVoiceRefiner(string hostExecutablePath, string modelPath, string unusedTemporaryDirectory, TimeSpan timeout)
    {
        var nativePath = Environment.GetEnvironmentVariable("VOICE_ASR_REFINER_NATIVE_LIBRARY")
            ?? Path.Combine(Path.GetDirectoryName(modelPath) ?? AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "whisperx-refiner.dll" : "libwhisperx-refiner.so");
        _inner = new ResidentVoiceRefinerClient(hostExecutablePath, modelPath, nativePath, timeout,
            Environment.GetEnvironmentVariable("WHISPERX_BUILD_IDENTITY") ?? string.Empty);
    }

    public string Provider => _inner.Provider;
    public string Model => _inner.Model;
    public bool IsAvailable => _inner.IsAvailable;
    public Task<VoiceRefinementResult> RefineAsync(VoiceUtteranceEnvelope utterance, CancellationToken cancellationToken) => _inner.RefineAsync(utterance, cancellationToken);
    public void Dispose() => _inner.Dispose();
}
