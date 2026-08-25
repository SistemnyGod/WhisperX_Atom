using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WhisperX.Atom.Voice.Host.Tts;

/// <summary>
/// Optional, local Piper bridge for the experimental English J.A.R.V.I.S.-style
/// voice.  It is deliberately separate from TtsHost/Silero: a missing Piper
/// payload must never make the Russian voice unavailable.
/// </summary>
public sealed class PiperTtsEngine : ITtsEngine
{
    private readonly string _executablePath;
    private readonly string _modelPath;
    private readonly string _configPath;
    private readonly string _expectedModelSha256;
    private readonly string _expectedConfigSha256;
    private readonly string _tempRoot;
    private readonly object _gate = new();
    private Process? _activeProcess;

    public PiperTtsEngine(string executablePath, string modelPath, string configPath,
        string expectedModelSha256 = "", string expectedConfigSha256 = "", string? tempRoot = null)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _modelPath = Path.GetFullPath(modelPath);
        _configPath = Path.GetFullPath(configPath);
        _expectedModelSha256 = expectedModelSha256.Trim().ToLowerInvariant();
        _expectedConfigSha256 = expectedConfigSha256.Trim().ToLowerInvariant();
        _tempRoot = Path.GetFullPath(tempRoot ?? TtsPaths.GetWritableTempRoot());
        TtsPaths.CleanupTemp(TimeSpan.FromHours(24), _tempRoot);
    }

    public string EngineName => "PIPER_JARVIS";
    public string ModelName => "jarvis-medium";
    public string VoiceName => "jarvis";
    public string VoiceCulture => "en-GB";
    public bool IsReady { get; private set; }
    public int? ProcessId { get { lock (_gate) return _activeProcess is { HasExited: false } p ? p.Id : null; } }
    public long LastSynthesisMs { get; private set; }
    public long LastModelLoadMs => 0;
    public int RestartCount { get; private set; }
    public string? LastErrorCode { get; private set; }

    public Task<bool> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsReady = ValidateAssets();
        return Task.FromResult(IsReady);
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken)
    {
        if (!IsReady && !await EnsureReadyAsync(cancellationToken).ConfigureAwait(false))
            return TtsSynthesisResult.Failed(EngineName, ModelName, VoiceName, LastErrorCode ?? "PIPER_MODEL_MISSING");

        var normalized = text.Normalize(NormalizationForm.FormC).Trim();
        if (normalized.Length is < 1 or > 600)
            return TtsSynthesisResult.Failed(EngineName, ModelName, VoiceName, "TTS_TEXT_INVALID");
        // The community J.A.R.V.I.S. model is en-GB.  Do not emit a garbled
        // pseudo-Russian pronunciation: the router will use the configured
        // Russian Silero/Windows fallback for Cyrillic responses.
        if (ContainsCyrillic(normalized))
            return TtsSynthesisResult.Failed(EngineName, ModelName, VoiceName, "PIPER_LANGUAGE_UNSUPPORTED");

        Directory.CreateDirectory(_tempRoot);
        var output = TtsPaths.CreateTempWavPath(_tempRoot);
        var started = Stopwatch.GetTimestamp();
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_modelPath);
            startInfo.ArgumentList.Add("--output_file");
            startInfo.ArgumentList.Add(output);
            process = Process.Start(startInfo);
            if (process is null) return Fail(output, "PIPER_START_FAILED");
            lock (_gate) _activeProcess = process;
            _ = DrainAsync(process.StandardOutput);
            _ = DrainAsync(process.StandardError);
            await process.StandardInput.WriteLineAsync(normalized).WaitAsync(cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) return Fail(output, "PIPER_SYNTHESIS_FAILED");
            if (!IsSafeTempFile(output) || !File.Exists(output) || new FileInfo(output).Length < 44)
                return Fail(output, "PIPER_OUTPUT_INVALID");

            LastSynthesisMs = ElapsedMs(started);
            LastErrorCode = null;
            return new(true, EngineName, ModelName, VoiceName, output, ReadDurationMs(output), LastSynthesisMs);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            WindowsTtsEngine.TryDelete(output);
            throw;
        }
        catch (TimeoutException)
        {
            Kill(process);
            return Fail(output, "PIPER_TIMEOUT");
        }
        catch
        {
            return Fail(output, "PIPER_SYNTHESIS_FAILED");
        }
        finally
        {
            lock (_gate) _activeProcess = null;
            process?.Dispose();
        }
    }

    private TtsSynthesisResult Fail(string output, string code)
    {
        WindowsTtsEngine.TryDelete(output);
        LastErrorCode = code;
        IsReady = code is not "PIPER_MODEL_MISSING" and not "PIPER_ASSET_CHANGED";
        return TtsSynthesisResult.Failed(EngineName, ModelName, VoiceName, code);
    }

    private bool ValidateAssets()
    {
        if (!File.Exists(_executablePath) || !File.Exists(_modelPath) || !File.Exists(_configPath))
        {
            LastErrorCode = "PIPER_MODEL_MISSING";
            return false;
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(_expectedModelSha256) && !HashMatches(_modelPath, _expectedModelSha256) ||
                !string.IsNullOrWhiteSpace(_expectedConfigSha256) && !HashMatches(_configPath, _expectedConfigSha256))
            {
                LastErrorCode = "PIPER_ASSET_CHANGED";
                return false;
            }
            LastErrorCode = null;
            return true;
        }
        catch
        {
            LastErrorCode = "PIPER_ASSET_INVALID";
            return false;
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSafeTempFile(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(_tempRoot) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } } catch { }
    }

    private static void Kill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
    }

    private static long ReadDurationMs(string path)
    {
        try { using var reader = new NAudio.Wave.AudioFileReader(path); return (long)reader.TotalTime.TotalMilliseconds; }
        catch { return 0; }
    }

    private static long ElapsedMs(long start) => (long)(Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;

    private static bool ContainsCyrillic(string value)
    {
        foreach (var character in value)
            if (character is >= '\u0400' and <= '\u052F') return true;
        return false;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) Kill(_activeProcess);
        return ValueTask.CompletedTask;
    }
}
