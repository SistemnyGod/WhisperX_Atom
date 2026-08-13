using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Desktop;

public sealed class RecorderIpcException(string errorCode, bool transient, Exception? inner = null)
    : IOException(errorCode, inner)
{
    public string ErrorCode { get; } = errorCode;
    public bool Transient { get; } = transient;
}

public sealed class AgentPipeClient
{
    private static readonly TimeSpan ControlRequestTimeout = TimeSpan.FromSeconds(8);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<AgentIpcResponse> SendAsync(string command, object? payload = null, CancellationToken cancellationToken = default)
    {
        // The server owns the security boundary and grants the exact user SID
        // (or the configured legacy SID). Do not combine that ACL with the
        // client-side CurrentUserOnly flag: on Windows it can reject a valid
        // elevated/non-elevated client pair even when both resolve to the same
        // interactive user.
        await using var pipe = new NamedPipeClientStream(".", RecorderPipeNames.ForCurrentProcess(), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ControlRequestTimeout);
        var requestCancellation = timeout.Token;
        try
        {
            await pipe.ConnectAsync(3000, requestCancellation);
        }
        catch (TimeoutException ex)
        {
            throw new RecorderIpcException("RECORDER_IPC_TIMEOUT", true, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RecorderIpcException("RECORDER_IPC_TIMEOUT", true, ex);
        }
        catch (IOException ex)
        {
            throw new RecorderIpcException("RECORDER_IPC_UNAVAILABLE", false, ex);
        }
        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload ?? new { }, _json));
        var request = new AgentIpcRequest(command, document.RootElement.Clone()) { ProtocolVersion = AgentIpcProtocol.Version };
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), requestCancellation);
            var line = await reader.ReadLineAsync(requestCancellation);
            if (string.IsNullOrWhiteSpace(line)) throw new IOException("Recorder Service returned an empty IPC response.");
            var response = JsonSerializer.Deserialize<AgentIpcResponse>(line, _json)
                ?? throw new IOException("Recorder Service returned an invalid IPC response.");
            if (response.Error == "IPC_VERSION_INCOMPATIBLE")
                throw new RecorderIpcException("IPC_VERSION_INCOMPATIBLE", false);
            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RecorderIpcException("RECORDER_IPC_TIMEOUT", true, ex);
        }
    }

    /// <summary>
    /// Keeps a dedicated IPC connection for AudioGraph device notifications.
    /// Control requests remain short-lived, so device changes cannot block
    /// START/STOP/HEALTH traffic.
    /// </summary>
    public async IAsyncEnumerable<AgentIpcResponse> SubscribeAsync(
        string command,
        object? payload = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", RecorderPipeNames.ForCurrentProcess(), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new RecorderIpcException("RECORDER_IPC_TIMEOUT", true, ex);
        }
        catch (IOException ex)
        {
            throw new RecorderIpcException("RECORDER_IPC_UNAVAILABLE", false, ex);
        }

        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload ?? new { }, _json));
        var request = new AgentIpcRequest(command, document.RootElement.Clone()) { ProtocolVersion = AgentIpcProtocol.Version };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json).AsMemory(), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) yield break;
            var response = JsonSerializer.Deserialize<AgentIpcResponse>(line, _json)
                ?? throw new IOException("Recorder Host returned an invalid device event.");
            if (response.Error == "IPC_VERSION_INCOMPATIBLE")
                throw new RecorderIpcException("IPC_VERSION_INCOMPATIBLE", false);
            yield return response;
        }
    }
}
