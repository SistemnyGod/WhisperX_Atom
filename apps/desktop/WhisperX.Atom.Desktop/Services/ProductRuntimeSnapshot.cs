using WhisperX.Atom.Desktop;
using WhisperX.Atom.Recorder;

namespace WhisperX_Atom_Desktop.Services;

public enum ProductRuntimeStatus
{
    Unknown,
    Checking,
    Ready,
    Busy,
    Degraded,
    Unavailable,
    Disabled,
    Failed
}

public sealed record ProductRuntimeComponent(
    string Name,
    ProductRuntimeStatus Status,
    string? ErrorCode = null,
    string? Message = null,
    DateTimeOffset? LastSeenAtUtc = null,
    bool Required = false,
    IReadOnlyList<string>? Capabilities = null);

/// <summary>
/// One honest client-side runtime aggregate. It deliberately keeps optional
/// Qwen/summary components separate from the mandatory Record→Transcript path.
/// </summary>
public sealed record ProductRuntimeSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyDictionary<string, ProductRuntimeComponent> Components,
    bool CanRecord,
    bool CanUpload,
    bool CanTranscribe,
    bool CanSummarize)
{
    public bool MvpRuntimeReady => CanRecord && CanUpload && CanTranscribe;

    public static ProductRuntimeSnapshot From(
        DesktopAuthState authentication,
        AgentIpcResponse? recorder,
        DesktopSystemStatus? backend,
        DesktopProcessingReadiness? processing)
    {
        var now = DateTimeOffset.UtcNow;
        var map = new Dictionary<string, ProductRuntimeComponent>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authentication"] = new(
                "Authentication",
                authentication == DesktopAuthState.Authenticated || authentication == DesktopAuthState.Offline
                    ? ProductRuntimeStatus.Ready
                    : ProductRuntimeStatus.Unavailable,
                authentication == DesktopAuthState.LoginRequired ? "AUTHENTICATION_REQUIRED" : null,
                authentication.ToString(), now, true),
            ["Backend"] = new(
                "Backend",
                backend is null ? ProductRuntimeStatus.Unknown : backend.Ready ? ProductRuntimeStatus.Ready : ProductRuntimeStatus.Unavailable,
                backend is { Ready: false } ? "BACKEND_UNAVAILABLE" : null,
                backend is null ? "not checked" : null, backend?.CheckedAt, true),
            ["Recorder"] = new(
                "Recorder",
                recorder is null ? ProductRuntimeStatus.Unavailable : recorder.Ok ? ProductRuntimeStatus.Ready : ProductRuntimeStatus.Failed,
                recorder?.Error, recorder?.State, now, true),
            ["CaptureRuntime"] = new(
                "CaptureRuntime",
                recorder?.Health?.MicrophoneCaptureReady == true || recorder?.Health?.AudioGraphReady == true
                    ? ProductRuntimeStatus.Ready
                    : ProductRuntimeStatus.Unavailable,
                recorder?.Health?.MicrophoneCaptureReady == false ? "AUDIO_CAPTURE_NOT_READY" : null,
                recorder?.Health?.CaptureEngine, now, true),
            ["LocalStorage"] = new(
                "LocalStorage",
                recorder?.Health is null ? ProductRuntimeStatus.Unknown : recorder.Health.FreeBytes > 0 ? ProductRuntimeStatus.Ready : ProductRuntimeStatus.Failed,
                recorder?.Health is { FreeBytes: <= 0 } ? "AUDIO_STORAGE_WRITE_FAILED" : null,
                recorder?.Health?.ArchiveRoot, now, true),
            ["Delivery"] = new(
                "Delivery",
                recorder?.Health?.ServerConnectionState switch
                {
                    "CONNECTED" => ProductRuntimeStatus.Ready,
                    "NOT_CONFIGURED" => ProductRuntimeStatus.Unavailable,
                    "SERVER_UNAVAILABLE" => ProductRuntimeStatus.Degraded,
                    null => ProductRuntimeStatus.Unknown,
                    _ => ProductRuntimeStatus.Degraded
                },
                recorder?.Health?.LastServerError,
                recorder?.Health?.ServerConnectionState, recorder?.Health?.LastHeartbeatAtUtc, true),
            ["WhisperX"] = new(
                "WhisperX",
                processing is null ? ProductRuntimeStatus.Unknown : processing.Ready ? ProductRuntimeStatus.Ready : ProductRuntimeStatus.Unavailable,
                processing is { Ready: false } ? "WHISPERX_NOT_READY" : null,
                "TRANSCRIPT", processing?.CheckedAt, true),
            ["Qwen"] = new("Qwen", ProductRuntimeStatus.Disabled, "QWEN_OPTIONAL_DISABLED", "SUMMARY", now, false)
        };

        var canRecord = map["Recorder"].Status is ProductRuntimeStatus.Ready
            && map["CaptureRuntime"].Status is ProductRuntimeStatus.Ready
            && map["LocalStorage"].Status is ProductRuntimeStatus.Ready;
        var canUpload = canRecord && map["Backend"].Status is ProductRuntimeStatus.Ready
            && map["Delivery"].Status is ProductRuntimeStatus.Ready;
        var canTranscribe = canUpload && map["WhisperX"].Status is ProductRuntimeStatus.Ready;
        var canSummarize = canTranscribe && map["Qwen"].Status is ProductRuntimeStatus.Ready;
        return new ProductRuntimeSnapshot(now, map, canRecord, canUpload, canTranscribe, canSummarize);
    }
}
