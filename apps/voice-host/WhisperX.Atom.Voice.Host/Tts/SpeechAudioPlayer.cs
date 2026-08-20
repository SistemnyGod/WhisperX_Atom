using System.Diagnostics;
using NAudio.Wave;

namespace WhisperX.Atom.Voice.Host.Tts;

public sealed record AudioPlaybackResult(bool Played, bool Cancelled, long PlaybackMs, string? ErrorCode = null);

public sealed class SpeechAudioPlayer : IAsyncDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;

    public bool IsPlaying { get { lock (_gate) return _output is not null; } }

    public async Task<AudioPlaybackResult> PlayAsync(string audioPath, int volume, Func<Task>? playbackStarted, CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath)) return new(false, false, 0, "TTS_AUDIO_PLAYBACK_FAILED");
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var stopRegistration = cancellationToken.Register(Stop);
            lock (_gate)
            {
                _reader = new AudioFileReader(audioPath) { Volume = Math.Clamp(volume, 0, 100) / 100f };
                _output = new WaveOutEvent { DesiredLatency = 80 };
                _output.Init(_reader);
            }
            if (playbackStarted is not null) await playbackStarted().ConfigureAwait(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            WaveOutEvent? output;
            lock (_gate) output = _output;
            if (output is null) return new(false, true, ElapsedMs(started), "TTS_AUDIO_CANCELLED");
            void OnStopped(object? _, StoppedEventArgs args) => completion.TrySetResult(args.Exception is null);
            output.PlaybackStopped += OnStopped;
            output.Play();
            var completed = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            output.PlaybackStopped -= OnStopped;
            return completed
                ? new(true, false, ElapsedMs(started))
                : new(false, false, ElapsedMs(started), "TTS_AUDIO_PLAYBACK_FAILED");
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
