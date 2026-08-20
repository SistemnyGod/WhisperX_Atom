using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WhisperX.Atom.Voice.Host.Tts;

public sealed class SileroTtsEngine : ITtsEngine
{
    private readonly TtsHostProcessClient _client;
    private readonly string _modelRoot;
    private readonly string _expectedModelSha256;
    private readonly string _expectedBuildIdentity;
    private readonly string _tempRoot;
    private int _failureBurst;

    public SileroTtsEngine(string executablePath, string modelRoot, string expectedModelSha256 = "", string expectedBuildIdentity = "", string? tempRoot = null)
    {
        _modelRoot = Path.GetFullPath(modelRoot);
        _expectedModelSha256 = string.IsNullOrWhiteSpace(expectedModelSha256) ? ReadManifestHash(_modelRoot) : expectedModelSha256.Trim().ToLowerInvariant();
        _expectedBuildIdentity = expectedBuildIdentity;
        _tempRoot = tempRoot ?? TtsPaths.GetWritableTempRoot();
        _client = new TtsHostProcessClient(executablePath, expectedBuildIdentity, _tempRoot);
        TtsPaths.CleanupTemp(TimeSpan.FromHours(24), _tempRoot);
    }

    public string EngineName => "SILERO";
    public string ModelName => "v5_5_ru";
    public string VoiceName => "aidar";
    public string VoiceCulture => "ru-RU";
    public bool IsReady { get; private set; }
    public int? ProcessId => _client.ProcessId;
    public int FailureBurst => Volatile.Read(ref _failureBurst);
    public int RestartCount { get; private set; }
    public long LastSynthesisMs { get; private set; }
    public long LastModelLoadMs { get; private set; }
    public string? LastErrorCode { get; private set; }

    public async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!ValidateModel()) { IsReady = false; LastErrorCode = "TTS_MODEL_INTEGRITY_FAILED"; return false; }
        try
        {
            var response = await _client.PingAsync(cancellationToken).ConfigureAwait(false);
            if (response?.Ok != true)
            {
                RestartCount++;
                await _client.RestartOnceAsync(cancellationToken).ConfigureAwait(false);
                response = await _client.PingAsync(cancellationToken).ConfigureAwait(false);
            }
            IsReady = response?.Ok == true && string.Equals(response.Model, ModelName, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(_expectedBuildIdentity) || string.Equals(response.BuildIdentity, _expectedBuildIdentity, StringComparison.Ordinal));
            LastModelLoadMs = response?.ModelLoadMs ?? 0;
            LastErrorCode = IsReady ? null : response?.ErrorCode ?? "TTS_HOST_START_FAILED";
            if (IsReady) Interlocked.Exchange(ref _failureBurst, 0);
            return IsReady;
        }
        catch (OperationCanceledException) { throw; }
        catch { IsReady = false; LastErrorCode = "TTS_HOST_START_FAILED"; return false; }
    }

    public async Task<bool> RestartOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsReady = false;
            RestartCount++;
            await _client.RestartOnceAsync(cancellationToken).ConfigureAwait(false);
            return await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            IsReady = false;
            LastErrorCode = "TTS_HOST_START_FAILED";
            return false;
        }
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken)
    {
        if (!IsReady && !await EnsureReadyAsync(cancellationToken).ConfigureAwait(false))
            return TtsSynthesisResult.Failed(EngineName, ModelName, options.Voice, LastErrorCode ?? "TTS_MODEL_LOAD_FAILED");
        var normalized = Normalize(text);
        if (normalized.Length is < 1 or > 600) return TtsSynthesisResult.Failed(EngineName, ModelName, options.Voice, "TTS_SYNTHESIS_FAILED");
        try
        {
            var response = await _client.SynthesizeAsync(normalized, options.Voice, options.NormalizedSampleRate, options.NormalizedCpuThreads, cancellationToken).ConfigureAwait(false);
            if (response?.Ok != true || string.IsNullOrWhiteSpace(response.AudioPath))
            {
                LastErrorCode = response?.ErrorCode ?? "TTS_SYNTHESIS_FAILED";
                IsReady = false;
                Interlocked.Increment(ref _failureBurst);
                return TtsSynthesisResult.Failed(EngineName, ModelName, options.Voice, LastErrorCode);
            }
            var path = Path.GetFullPath(response.AudioPath);
            var temp = Path.GetFullPath(_tempRoot);
            if (!path.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                LastErrorCode = "TTS_AUDIO_PLAYBACK_FAILED";
                return TtsSynthesisResult.Failed(EngineName, ModelName, options.Voice, LastErrorCode);
            }
            LastSynthesisMs = response.SynthesisMs;
            LastErrorCode = null;
            Interlocked.Exchange(ref _failureBurst, 0);
            return new(true, EngineName, response.Model ?? ModelName, response.Voice ?? options.Voice, path, response.DurationMs, response.SynthesisMs, IsStaticCache: response.CacheHit);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            IsReady = false;
            LastErrorCode = "TTS_HOST_TIMEOUT";
            Interlocked.Increment(ref _failureBurst);
            return TtsSynthesisResult.Failed(EngineName, ModelName, options.Voice, LastErrorCode);
        }
    }

    private bool ValidateModel()
    {
        var modelPath = Path.Combine(_modelRoot, "v5_5_ru.pt");
        if (!File.Exists(modelPath)) { LastErrorCode = "TTS_MODEL_MISSING"; return false; }
        if (string.IsNullOrWhiteSpace(_expectedModelSha256)) return true;
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(modelPath);
            return string.Equals(Convert.ToHexString(sha.ComputeHash(stream)), _expectedModelSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ReadManifestHash(string root)
    {
        try
        {
            var path = Path.Combine(root, "model-manifest.json");
            if (!File.Exists(path)) return string.Empty;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("sha256", out var value) ? value.GetString()?.Trim().ToLowerInvariant() ?? string.Empty : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormC).Trim();
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
