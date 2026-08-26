using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Voice.Host;

public sealed class VoiceHostPipeServer(VoiceHostRuntime runtime, ILogger<VoiceHostPipeServer> logger) : BackgroundService
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(VoiceHostIpc.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(pipe);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var line = await reader.ReadLineAsync(stoppingToken);
                var request = string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<VoiceHostRequest>(line, _json);
                var response = request is null ? new VoiceHostResponse(false, Error: "invalid_request") : await runtime.HandleIpcAsync(request.Command, request.Payload, stoppingToken);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Voice Host IPC request failed."); }
        }
    }
}
