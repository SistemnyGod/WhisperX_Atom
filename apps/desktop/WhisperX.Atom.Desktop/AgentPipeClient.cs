using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Desktop;

public sealed class AgentPipeClient
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<AgentIpcResponse> SendAsync(string command, object? payload = null, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", AgentIpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, cancellationToken);
        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload ?? new { }, _json));
        var request = new AgentIpcRequest(command, document.RootElement.Clone());
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json));
        var line = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line)) throw new IOException("Recorder Service returned an empty IPC response.");
        return JsonSerializer.Deserialize<AgentIpcResponse>(line, _json)
            ?? throw new IOException("Recorder Service returned an invalid IPC response.");
    }
}
