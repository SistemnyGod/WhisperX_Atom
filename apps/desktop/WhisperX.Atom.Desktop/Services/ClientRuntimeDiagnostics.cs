using System.Text.Json;
using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;

namespace WhisperX_Atom_Desktop.Services;

public sealed class ClientRuntimeDiagnostics(FrontendServices services)
{
    public async Task<string> WriteAsync(CancellationToken cancellationToken = default)
    {
        var settings = services.Settings.Load();
        var service = await services.RecorderService.GetSnapshotAsync(cancellationToken);
        AgentIpcResponse? health = null;
        try { health = await services.Recorder.GetHealthAsync(cancellationToken); }
        catch (Exception exception) { health = new AgentIpcResponse(false, "UNAVAILABLE", null, exception is RecorderIpcException ipc ? ipc.ErrorCode : "RECORDER_IPC_UNAVAILABLE", null); }
        var serverReady = await services.Backend.CheckReadyAsync(cancellationToken);
        var processing = await services.Backend.GetProcessingReadinessAsync(cancellationToken);

        var report = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            client = new
            {
                apiUrl = services.Backend.ApiUrl,
                authState = services.Backend.AuthState.ToString(),
                user = settings.Username,
                ownerUserId = settings.OwnerUserId,
                recordingProfile = settings.RecordingProfile,
                microphoneDeviceId = settings.MicrophoneDeviceId,
                systemAudioDeviceId = settings.SystemAudioDeviceId,
                agentBootstrapConfirmed = settings.AgentBootstrapConfirmed
            },
            recorderService = service,
            recorder = health is null ? null : new
            {
                health.Ok,
                health.State,
                health.Error,
                health.Health?.InstallationId,
                health.Health?.AgentId,
                health.Health?.ServerConnectionState,
                health.Health?.LastHeartbeatAtUtc,
                health.Health?.LastServerError,
                health.Health?.SelectedMicrophoneDeviceId,
                health.Health?.SelectedSystemAudioDeviceId,
                health.Health?.CaptureDeviceCount,
                health.Health?.RenderDeviceCount,
                health.Health?.RawChunksPending,
                health.Health?.RawChunksReady,
                health.Health?.RawChunksReadyForUpload,
                health.Health?.PendingUploadSessions,
                health.Health?.ActiveSessionId,
                health.Health?.BackgroundPendingSessions,
                health.Health?.BackgroundFailedSessions
            },
            bootstrap = services.AgentBootstrap.LastStatus,
            server = new { liveOrReady = serverReady, processingReadiness = processing }
        };

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Diagnostics");
        var directory = Path.Combine(root, $"client-runtime-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return path;
    }
}
