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
            var captureReady = preflight.Preflight?.CaptureReady == true
                || preflight.Preflight?.Ready == true;
            if (!preflight.Ok || !captureReady)
                return preflight with { Ok = false, Error = preflight.Error ?? preflight.Preflight?.Errors.FirstOrDefault() ?? "AUDIO_CAPTURE_START_FAILED" };

            var response = await recorder.StartAsync(title, ownerUserId: ownerUserId, localOnly: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!response.Ok) return response;

            // Recorder Host acknowledges START only after its writer observes
            // the first durable bytes. Do not perform a second health-based
            // handshake here: a transient health read must not turn a live
            // recording into a false failure or leave hidden capture active.
            return response with { Preflight = preflight.Preflight };
        }
        finally { _gate.Release(); }
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
