using WhisperX.Atom.Recorder;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>
/// The single mutation path for recording. UI buttons and the voice broker
/// call these methods so preflight, error mapping and first-frame confirmation
/// cannot drift apart.
/// </summary>
public sealed class RecordingCommandService(IRecorderService recorder)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AgentIpcResponse> StartAsync(string title, Guid? ownerUserId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preflight = await recorder.PreflightAsync(cancellationToken).ConfigureAwait(false);
            var sessionBeforeStart = preflight.SessionId ?? preflight.Health?.ActiveSessionId;
            var captureReady = preflight.Preflight?.CaptureReady == true
                || preflight.Preflight?.Ready == true;
            if (!preflight.Ok || !captureReady)
                return preflight with { Ok = false, Error = preflight.Error ?? preflight.Preflight?.Errors.FirstOrDefault() ?? "AUDIO_CAPTURE_START_FAILED" };

            try
            {
                var response = await recorder.StartAsync(title, ownerUserId: ownerUserId, localOnly: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!response.Ok)
                {
                    await AbortStartAsync(sessionBeforeStart, response.SessionId).ConfigureAwait(false);
                    return response with
                    {
                        Ok = false,
                        Error = response.Error ?? "AUDIO_CAPTURE_START_FAILED",
                        Preflight = preflight.Preflight
                    };
                }

                // Recorder Host acknowledges START only after its writer
                // observes the first durable bytes. Do not perform a second
                // health-based handshake here: a transient health read must
                // not turn a live recording into a false failure.
                return response with { Preflight = preflight.Preflight };
            }
            catch (Exception ex)
            {
                await AbortStartAsync(sessionBeforeStart, null).ConfigureAwait(false);
                return new AgentIpcResponse(
                    false,
                    "Idle",
                    null,
                    "AUDIO_CAPTURE_START_FAILED",
                    null,
                    ErrorDetail: ex.Message,
                    Preflight: preflight.Preflight);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Best-effort, idempotent rollback for a START request which did not
    /// receive the durable-frame ACK. A pre-existing recording is never
    /// stopped; only a session observed after this request is eligible.
    /// </summary>
    private async Task AbortStartAsync(string? sessionBeforeStart, string? responseSessionId)
    {
        try
        {
            var health = await recorder.GetHealthAsync().ConfigureAwait(false);
            var active = responseSessionId ?? health.SessionId ?? health.Health?.ActiveSessionId;
            if (!string.IsNullOrWhiteSpace(active)
                && !string.Equals(active, sessionBeforeStart, StringComparison.OrdinalIgnoreCase)
                && health.State is "Starting" or "Recording" or "Paused" or "Finalizing")
            {
                await recorder.StopAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Rollback is deliberately best effort. The stable START failure
            // is already returned; the next health cycle will expose recovery.
        }
    }

    public async Task<AgentIpcResponse> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await recorder.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<AgentIpcResponse> PauseAsync(CancellationToken cancellationToken = default) =>
        await ExecuteSerializedAsync(recorder.PauseAsync, cancellationToken).ConfigureAwait(false);

    public async Task<AgentIpcResponse> ResumeAsync(CancellationToken cancellationToken = default) =>
        await ExecuteSerializedAsync(recorder.ResumeAsync, cancellationToken).ConfigureAwait(false);

    public async Task<AgentIpcResponse> StatusAsync(CancellationToken cancellationToken = default) =>
        await recorder.GetHealthAsync(cancellationToken).ConfigureAwait(false);

    public async Task<AgentIpcResponse> MarkerAsync(string eventType, CancellationToken cancellationToken = default) =>
        await ExecuteSerializedAsync(token => recorder.AddMarkerAsync(eventType, token), cancellationToken).ConfigureAwait(false);

    private async Task<AgentIpcResponse> ExecuteSerializedAsync(Func<CancellationToken, Task<AgentIpcResponse>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
