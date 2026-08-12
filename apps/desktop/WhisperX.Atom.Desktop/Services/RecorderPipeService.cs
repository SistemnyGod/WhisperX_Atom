using WhisperX.Atom.Recorder;
using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

public sealed class RecorderPipeService : IRecorderService
{
    private readonly AgentPipeClient _client = new();

    public Task<AgentIpcResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        _client.SendAsync("HEALTH", cancellationToken: cancellationToken);

    public Task<AgentIpcResponse> PreflightAsync(CancellationToken cancellationToken = default) =>
        _client.SendAsync("PREFLIGHT", cancellationToken: cancellationToken);

    public Task<AgentIpcResponse> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _client.SendAsync("GET_SESSION_STATUS", new { sessionId }, cancellationToken);

    public Task<AgentIpcResponse> StartAsync(string title, Guid? meetingId = null, CancellationToken cancellationToken = default) =>
        _client.SendAsync("START", new { title, meetingId }, cancellationToken);

    public Task<AgentIpcResponse> PauseAsync(CancellationToken cancellationToken = default) =>
        _client.SendAsync("PAUSE", cancellationToken: cancellationToken);

    public Task<AgentIpcResponse> ResumeAsync(CancellationToken cancellationToken = default) =>
        _client.SendAsync("RESUME", cancellationToken: cancellationToken);

    public Task<AgentIpcResponse> AddMarkerAsync(string eventType = "MARKER", CancellationToken cancellationToken = default) =>
        _client.SendAsync(eventType switch
        {
            "DECISION" => "DECISION",
            "ACTION_ITEM" => "ACTION_ITEM",
            _ => "MARKER"
        }, new { source = "desktop" }, cancellationToken);

    public Task<AgentIpcResponse> StopAsync(CancellationToken cancellationToken = default) =>
        _client.SendAsync("STOP", cancellationToken: cancellationToken);

    public Task<AgentIpcResponse> RetryUploadAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _client.SendAsync("RETRY_UPLOAD", new { sessionId }, cancellationToken);

    public Task<AgentIpcResponse> ConfigureAgentAsync(string serverUrl, Guid agentId, string token, string archiveRoot, string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default) =>
        _client.SendAsync("CONFIGURE", new { serverUrl, agentId, token, archiveRoot, microphoneDeviceId, systemAudioDeviceId }, cancellationToken);

    public Task<AgentIpcResponse> SetAudioDevicesAsync(string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default) =>
        _client.SendAsync("SET_AUDIO_DEVICES", new { microphoneDeviceId, systemAudioDeviceId }, cancellationToken);

    public Task<AgentIpcResponse> SetRecordingProfileAsync(string recordingProfile, CancellationToken cancellationToken = default) =>
        _client.SendAsync("SET_RECORDING_PROFILE", new { recordingProfile }, cancellationToken);

    public Task<AgentIpcResponse> TestAudioSourceAsync(string? deviceId, bool systemAudio = false, CancellationToken cancellationToken = default) =>
        _client.SendAsync("TEST_AUDIO_SOURCE", new { deviceId, systemAudio }, cancellationToken);

    public Task<AgentIpcResponse> SetArchiveRootAsync(string archiveRoot, CancellationToken cancellationToken = default) =>
        _client.SendAsync("SET_ARCHIVE_ROOT", new { archiveRoot }, cancellationToken);
}
