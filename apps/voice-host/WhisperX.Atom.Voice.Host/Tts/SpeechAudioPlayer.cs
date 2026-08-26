using System.Diagnostics;
using NAudio.Wave;

namespace WhisperX.Atom.Voice.Host.Tts;

public sealed record AudioPlaybackResult(bool Played, bool Cancelled, long PlaybackMs, string? ErrorCode = null, bool FxApplied = false, string? FxFallbackReason = null);

public sealed class SpeechAudioPlayer : IAsyncDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;

    public bool IsPlaying { get { lock (_gate) return _output is not null; } }

    public async Task<AudioPlaybackResult> PlayAsync(string audioPath, int volume, Func<Task>? playbackStarted, CancellationToken cancellationToken, bool applyVoiceFx = false, string voiceProfile = TtsVoiceProfiles.MifodiyTech)
    {
        if (!File.Exists(audioPath)) return new(false, false, 0, "TTS_AUDIO_PLAYBACK_FAILED");
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var stopRegistration = cancellationToken.Register(Stop);
            var fxApplied = false;
            string? fxFallbackReason = null;
            lock (_gate)
            {
                _reader = new AudioFileReader(audioPath) { Volume = Math.Clamp(volume, 0, 100) / 100f };
                _output = new WaveOutEvent { DesiredLatency = 80 };
                NAudio.Wave.ISampleProvider source = _reader;
                if (applyVoiceFx)
                {
                    try
                    {
                        source = new MifodiyVoiceFxSampleProvider(source, voiceProfile);
                        fxApplied = true;
                    }
                    catch (Exception ex)
                    {
                        fxFallbackReason = $"{ex.GetType().Name}:VOICE_FX_UNAVAILABLE";
                    }
                }
                _output.Init(source);
            }
            if (playbackStarted is not null) await playbackStarted().ConfigureAwait(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            WaveOutEvent? output;
            lock (_gate) output = _output;
            if (output is null) return new(false, true, ElapsedMs(started), "TTS_AUDIO_CANCELLED", fxApplied, fxFallbackReason);
            void OnStopped(object? _, StoppedEventArgs args) => completion.TrySetResult(args.Exception is null);
            output.PlaybackStopped += OnStopped;
            output.Play();
            var completed = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            output.PlaybackStopped -= OnStopped;
            return completed
                ? new(true, false, ElapsedMs(started), null, fxApplied, fxFallbackReason)
                : new(false, false, ElapsedMs(started), "TTS_AUDIO_PLAYBACK_FAILED", fxApplied, fxFallbackReason);
        }
        catch (OperationCanceledException)
        {
            Stop();
            return new(false, true, ElapsedMs(started), "TTS_AUDIO_CANCELLED");
        }
        catch (Exception)
        {
            Stop();
            return new(false, false, ElapsedMs(started), "TTS_AUDIO_PLAYBACK_FAILED");
        }
        finally
        {
            lock (_gate)
            {
                _output?.Dispose();
                _reader?.Dispose();
                _output = null;
                _reader = null;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _output?.Stop(); } catch { }
        }
    }

    public void SetVolume(int volume)
    {
        lock (_gate)
        {
            if (_reader is not null) _reader.Volume = Math.Clamp(volume, 0, 100) / 100f;
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        lock (_gate)
        {
            _output?.Dispose();
            _reader?.Dispose();
            _output = null;
            _reader = null;
        }
        return ValueTask.CompletedTask;
    }

    private static long ElapsedMs(long start) => (long)(Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;
}
