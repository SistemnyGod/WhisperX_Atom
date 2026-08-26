using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperX.Atom.Voice.Host;

internal sealed record LiveAudioFrameDto(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("meetingId")] Guid? MeetingId,
    [property: JsonPropertyName("trackId")] string TrackId,
    [property: JsonPropertyName("trackType")] string TrackType,
    [property: JsonPropertyName("channelRole")] string ChannelRole,
    [property: JsonPropertyName("startMs")] long StartMs,
    [property: JsonPropertyName("durationMs")] int DurationMs,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("sampleRate")] int SampleRate,
    [property: JsonPropertyName("channels")] int Channels,
    [property: JsonPropertyName("pcm16Base64")] string Pcm16Base64,
    [property: JsonPropertyName("gap")] bool Gap = false,
    [property: JsonPropertyName("droppedBefore")] long DroppedBefore = 0,
    [property: JsonPropertyName("localSessionId")] string? LocalSessionId = null);

internal sealed record LiveAudioSessionDto(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("meetingId")] Guid? MeetingId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("capturedAtUtc")] DateTimeOffset CapturedAtUtc,
    [property: JsonPropertyName("systemAudioEnabled")] bool SystemAudioEnabled = false,
    [property: JsonPropertyName("paused")] bool Paused = false,
    [property: JsonPropertyName("localSessionId")] string? LocalSessionId = null);

/// <summary>Read-only client for Recorder Host's provisional audio stream.</summary>
internal sealed class LiveAudioClient : IAsyncDisposable
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private long _drops;

    public event Action<LiveAudioFrameDto>? FrameReceived;
    public event Action<LiveAudioSessionDto>? SessionChanged;
    public event Action<bool>? ConnectionChanged;
    public long Drops => Interlocked.Read(ref _drops);
    public bool Connected { get; private set; }

    public void Start() => _worker ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", "WhisperXAtomLiveAudioV1", PipeDirection.In, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1000, _shutdown.Token).ConfigureAwait(false);
                Connected = true;
                try { ConnectionChanged?.Invoke(true); } catch { Interlocked.Increment(ref _drops); }
                using var reader = new StreamReader(pipe);
                while (!_shutdown.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) break;
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var protocol = root.TryGetProperty("protocol", out var p) ? p.GetString() : null;
                    if (!string.Equals(protocol, "LIVE_AUDIO_V1", StringComparison.OrdinalIgnoreCase)) continue;
                    if (root.TryGetProperty("event", out _))
                    {
                        var session = JsonSerializer.Deserialize<LiveAudioSessionDto>(line, _json);
                        if (session is not null)
                        {
                            try { SessionChanged?.Invoke(session); }
                            catch { Interlocked.Increment(ref _drops); }
                        }
                        continue;
                    }
                    var frame = JsonSerializer.Deserialize<LiveAudioFrameDto>(line, _json);
                    if (frame is null) continue;
                    // VoiceHostRuntime owns the bounded processing queue. A
                    // second unconsumed queue here would silently suppress
                    // callbacks after it fills during a long meeting.
                    try { FrameReceived?.Invoke(frame); }
                    catch { Interlocked.Increment(ref _drops); }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (IOException) { }
            catch (TimeoutException) { }
            catch (Exception) { }
            finally
            {
                var wasConnected = Connected;
                Connected = false;
                if (wasConnected)
                {
                    try { ConnectionChanged?.Invoke(false); } catch { Interlocked.Increment(ref _drops); }
                }
                try { await Task.Delay(500, _shutdown.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { if (_worker is not null) await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _shutdown.Dispose();
    }
}
