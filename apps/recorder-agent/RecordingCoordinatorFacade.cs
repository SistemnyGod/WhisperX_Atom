using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Neutral lifecycle facade used by the Service during the migration. The
/// legacy implementation is an adapter behind this boundary; the AudioGraph
/// Host has its own IAudioCaptureEngine runtime and shares the same spool and
/// delivery contracts.
/// </summary>
public sealed class RecordingCoordinator : IAsyncDisposable
{
    private readonly LegacyRecordingCoordinator _legacy;

    public RecordingCoordinator(SpoolStore spool, AgentStateMachine state, AgentStorageSettings storage, RawEncoderWakeSignal encoderWake, RawFinalizerQueueMetrics rawFinalizerMetrics, LocalArchiveWriter archive, ILogger<RecordingCoordinator> logger)
        => _legacy = new LegacyRecordingCoordinator(spool, state, storage, encoderWake, rawFinalizerMetrics, archive, logger);

    public string? SessionId => _legacy.SessionId;
    public AudioSourceTestResult? LastMicrophoneProbe => _legacy.LastMicrophoneProbe;
    public AudioSourceTestResult? LastSystemAudioProbe => _legacy.LastSystemAudioProbe;
    public long? CurrentMediaTimeMs => _legacy.CurrentMediaTimeMs;
    public IReadOnlyList<RecordingTrackInfo> ActiveTracks => _legacy.ActiveTracks;
    public AudioPeakSnapshot CurrentAudioPeaks => _legacy.CurrentAudioPeaks;
    public (bool Microphone, bool SystemAudio, string? Error) GetCaptureReadiness(string profile) => _legacy.GetCaptureReadiness(profile);
    public void ValidatePreflight() => _legacy.ValidatePreflight();
    public Task<string> StartAsync(Guid? meetingId = null, string? title = null, CancellationToken cancellationToken = default, Guid? ownerUserId = null, bool localOnly = false, string acousticProfile = "AUTO")
        => _legacy.StartAsync(meetingId, title, cancellationToken, ownerUserId, localOnly, acousticProfile);
    public Task<AudioSourceTestResult> TestAudioSourceAsync(string? deviceId, bool systemAudio, CancellationToken cancellationToken = default)
        => _legacy.TestAudioSourceAsync(deviceId, systemAudio, cancellationToken);
    public Task<AudioSourceTestResult> TestAudioSourceAsync(string? deviceId, bool systemAudio, TimeSpan duration, CancellationToken cancellationToken = default)
        => _legacy.TestAudioSourceAsync(deviceId, systemAudio, duration, cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken = default) => _legacy.PauseAsync(cancellationToken);
    public Task ResumeAsync(CancellationToken cancellationToken = default) => _legacy.ResumeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => _legacy.StopAsync(cancellationToken);
    public Task<RecordingStopHandle> RequestStopAsync(CancellationToken cancellationToken = default) => _legacy.RequestStopAsync(cancellationToken);
    public ValueTask DisposeAsync() => _legacy.DisposeAsync();
}
