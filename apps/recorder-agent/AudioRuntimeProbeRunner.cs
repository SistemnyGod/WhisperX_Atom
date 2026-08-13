using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WhisperX.Atom.Recorder;

public static class AudioRuntimeProbeRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var mode = Read(args, "mode")?.ToLowerInvariant() ?? "interactive";
        var deviceId = Read(args, "device-id");
        var systemAudio = Has(args, "system-audio");
        var seconds = int.TryParse(Read(args, "seconds"), out var parsed) ? Math.Clamp(parsed, 1, 10) : 3;
        AudioSourceTestResult? result;
        if (mode == "service")
            result = await RunServiceAsync(deviceId, systemAudio, seconds);
        else
            result = await AudioRuntimeProbe.RunAsync(deviceId, systemAudio, TimeSpan.FromSeconds(seconds));

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return result?.Success == true ? 0 : 1;
    }

    private static async Task<AudioSourceTestResult?> RunServiceAsync(string? deviceId, bool systemAudio, int seconds)
    {
        await using var pipe = new NamedPipeClientStream(".", AgentIpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        var payload = JsonSerializer.SerializeToElement(new { deviceId, systemAudio, durationSeconds = seconds });
        await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentIpcRequest("TEST_AUDIO_SOURCE", payload)));
        var responseLine = await reader.ReadLineAsync() ?? throw new InvalidOperationException("AUDIO_PROBE_NO_SERVICE_RESPONSE");
        var response = JsonSerializer.Deserialize<AgentIpcResponse>(responseLine, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return response?.AudioSourceTest;
    }

    private static string? Read(string[] args, string name)
    {
        var prefix = "--" + name + "=";
        var value = args.FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value is null ? null : value[prefix.Length..];
    }

    private static bool Has(string[] args, string name) => args.Any(item => string.Equals(item, "--" + name, StringComparison.OrdinalIgnoreCase));
}
