using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>
/// Produces a redacted operational support bundle. This class deliberately
/// does not serialize transcript/summary content, raw audio, cookies or any
/// credential-bearing configuration.
/// </summary>
public sealed class ClientRuntimeDiagnostics(FrontendServices services)
{
    public async Task<string> WriteAsync(CancellationToken cancellationToken = default)
    {
        var settings = services.Settings.Load();
        var service = await services.RecorderService.GetSnapshotAsync(cancellationToken);
        AgentIpcResponse? health = null;
        try { health = await services.Recorder.GetHealthAsync(cancellationToken); }
        catch (Exception exception)
        {
            health = new AgentIpcResponse(false, "UNAVAILABLE", null,
                exception is RecorderIpcException ipc ? ipc.ErrorCode : "RECORDER_IPC_UNAVAILABLE", null);
        }

        var serverReady = await services.Backend.CheckReadyAsync(cancellationToken);
        var processing = await services.Backend.GetProcessingReadinessAsync(cancellationToken);
        DesktopVoiceSnapshot? voice = null;
        try { voice = await new VoiceHostClient().GetStatusAsync(cancellationToken); }
        catch { /* Voice Host may be stopped; the bundle must still be usable. */ }

        var report = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            client = new
            {
                buildIdentity = CurrentBuildIdentity(),
                releaseVersion = CurrentReleaseVersion(),
                apiUrl = services.Backend.ApiUrl,
                authState = services.Backend.AuthState.ToString(),
                user = settings.Username,
                ownerUserId = settings.OwnerUserId,
                recordingProfile = settings.RecordingProfile,
                microphoneDeviceId = settings.MicrophoneDeviceId,
                systemAudioDeviceId = settings.SystemAudioDeviceId,
                agentBootstrapConfirmed = settings.AgentBootstrapConfirmed
            },
            recorderService = new
            {
                service.ServiceName,
                service.State,
                service.Exists,
                service.PipeReachable,
                service.Version,
                service.Error,
                service.ProcessId,
                service.BuildIdentity,
                service.ServerOrigin,
                service.AgentId,
                service.InstallationId
            },
            recorder = health is null ? null : new
            {
                health.Ok,
                health.State,
                health.Error,
                health.Health?.InstallationId,
                health.Health?.AgentId,
                health.Health?.ServerConnectionState,
                health.Health?.ServerOrigin,
                health.Health?.LastHeartbeatAtUtc,
                health.Health?.SelectedMicrophoneDeviceId,
                health.Health?.EffectiveMicrophoneDeviceId,
                health.Health?.EffectiveMicrophoneDeviceName,
                health.Health?.MicrophoneSignalState,
                health.Health?.MicrophonePeak,
                health.Health?.MicrophoneRmsDb,
                health.Health?.MicrophoneClipping,
                health.Health?.MicrophoneTelemetryStale,
                health.Health?.RawBacklogHealth,
                health.Health?.LastAudioGraphAttempt,
                health.Health?.RuntimeUser,
                health.Health?.RuntimeSid,
                health.Health?.WindowsSessionId,
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
            bootstrap = new
            {
                ready = services.AgentBootstrap.LastStatus.Ready,
                code = services.AgentBootstrap.LastStatus.Code,
                authenticated = services.AgentBootstrap.LastStatus.Authenticated,
                pipeReachable = services.AgentBootstrap.LastStatus.PipeReachable,
                installationIdPresent = services.AgentBootstrap.LastStatus.InstallationIdPresent,
                agentConfigured = services.AgentBootstrap.LastStatus.AgentConfigured,
                userLinked = services.AgentBootstrap.LastStatus.UserLinked,
                serverConnected = services.AgentBootstrap.LastStatus.ServerConnected,
                heartbeatFresh = services.AgentBootstrap.LastStatus.HeartbeatFresh,
                agentId = services.AgentBootstrap.LastStatus.AgentId,
                installationId = services.AgentBootstrap.LastStatus.InstallationId
            },
            voice = voice is null ? null : new
            {
                voice.State,
                voice.Enabled,
                voice.ModelReady,
                voice.ModelIntegrityReady,
                voice.NativeRuntimeReady,
                voice.MicrophoneReady,
                voice.RecorderPipeReady,
                voice.RecorderPipeError,
                voice.LastIntent,
                voice.LastErrorCode,
                voice.BuildIdentity,
                voice.WakeWordMode,
                voice.ProcessId,
                voice.HeartbeatAtUtc,
                voice.RequestedMicrophoneDeviceId,
                voice.EffectiveMicrophoneDeviceId,
                voice.EffectiveMicrophoneName,
                voice.TtsEngine,
                voice.TtsModel,
                voice.TtsReady,
                voice.TtsVoice,
                voice.TtsSampleRate,
                voice.TtsCpuThreads,
                voice.TtsHostProcessId,
                voice.TtsFallbackUsed,
                voice.TtsFallbackReason,
                voice.TtsRestartCount,
                voice.LiveAudioMode,
                voice.LiveRoomTrackState,
                voice.LiveSystemTrackState,
                voice.LiveAudioDrops,
                voice.LiveSegmentsPublished,
                voice.LiveSegmentsSuppressed
            },
            server = new { liveOrReady = serverReady, processingReadiness = processing },
            safety = new
            {
                audioIncluded = false,
                transcriptTextIncluded = false,
                summaryContentIncluded = false,
                cookiesIncluded = false,
                credentialsIncluded = false,
                tokensIncluded = false
            }
        };

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WhisperXAtom", "Diagnostics");
        var stamp = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var directory = Path.Combine(root, $"support-bundle-{stamp}");
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "report.json");
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        var manifest = new
        {
            schemaVersion = 1,
            generatedAtUtc = DateTimeOffset.UtcNow,
            contents = "redacted operational diagnostics",
            excluded = new[] { "audio", "transcript text", "summary content", "cookies", "credentials", "HF_TOKEN", "agent tokens" }
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "diagnostics-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        var archivePath = Path.Combine(root, $"support-bundle-{stamp}.zip");
        var archivePart = archivePath + ".part";
        try
        {
            if (File.Exists(archivePart)) File.Delete(archivePart);
            ZipFile.CreateFromDirectory(directory, archivePart, CompressionLevel.Fastest, includeBaseDirectory: false);
            File.Move(archivePart, archivePath, overwrite: true);
            return archivePath;
        }
        finally
        {
            try { if (File.Exists(archivePart)) File.Delete(archivePart); } catch { }
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static string CurrentBuildIdentity() => typeof(ClientRuntimeDiagnostics).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    private static string CurrentReleaseVersion() => typeof(ClientRuntimeDiagnostics).Assembly
        .GetName().Version?.ToString(3) ?? "0.0.0";
}
