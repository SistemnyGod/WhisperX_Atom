using System.IO.Pipes;
using System.Text.Json;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Voice.Host;

internal sealed class RecorderPipeClient
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<AgentIpcResponse> SendAsync(string command, object? payload, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", AgentIpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(300, cancellationToken);
        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload ?? new { }, _json));
        await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentIpcRequest(command, document.RootElement.Clone()), _json));
        var line = await reader.ReadLineAsync(cancellationToken);
        return JsonSerializer.Deserialize<AgentIpcResponse>(line ?? "", _json)
            ?? throw new IOException("Recorder Service returned an invalid response.");
    }
}
