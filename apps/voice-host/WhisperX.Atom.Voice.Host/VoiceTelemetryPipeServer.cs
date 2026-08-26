using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WhisperX.Atom.Voice.Host;

/// <summary>
/// Publishes only the newest microphone telemetry packet.  The pipe is deliberately
/// independent from the control pipe so a slow settings page cannot delay commands
/// or AudioGraph/Vosk processing.
/// </summary>
public sealed class VoiceTelemetryPipeServer(VoiceHostRuntime runtime, ILogger<VoiceTelemetryPipeServer> logger) : BackgroundService
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                VoiceHostIpc.TelemetryPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var lastSequence = -1L;
                while (pipe.IsConnected && !stoppingToken.IsCancellationRequested)
                {
                    var telemetry = runtime.AudioTelemetry;
                    if (telemetry.Sequence != lastSequence)
                    {
                        var packet = new VoiceTelemetryPacket(
                            telemetry.Sequence,
                            telemetry.AtUtc,
                            telemetry.Rms,
                            telemetry.Peak,
                            telemetry.Clipping,
                            telemetry.SignalState,
                            telemetry.DeviceId,
                            telemetry.DeviceName);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(packet, _json));
                        lastSequence = telemetry.Sequence;
                    }
                    await Task.Delay(100, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (IOException) { /* client closed the settings stream; accept a reconnect */ }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Voice telemetry pipe failed; retrying.");
                try { await Task.Delay(250, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
