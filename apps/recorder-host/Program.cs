using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhisperX.Atom.Recorder;
using WhisperX.Atom.Recorder.Host;

// The AudioGraph Host is a per-user runtime.  Its credentials and installation
// identity are protected in the interactive user's DPAPI scope; falling back
// to ProgramData here silently attaches the Host to the legacy Service Agent.
// Keep an explicit environment override for diagnostics and test harnesses.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATOM_AGENT_CONFIG_PATH")))
{
    var userConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperXAtom", "Agent", "agent-config.json");
    Environment.SetEnvironmentVariable("ATOM_AGENT_CONFIG_PATH", userConfigPath);
}

using var processGuard = RecorderHostProcessGuard.TryAcquire()
    ?? throw new InvalidOperationException("RECORDER_HOST_ALREADY_RUNNING");

var builder = Host.CreateApplicationBuilder(args);
// A current-user Recorder Host normally has no Event Log write privilege.
// Keep diagnostics from becoming a capture failure by using console output,
// which the development launcher captures and Windows can safely discard for
// the hidden installed process.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "O ";
});
var hostLogPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WhisperXAtom", "logs", "recorder-host.log");
builder.Logging.AddProvider(new RecorderHostFileLoggerProvider(hostLogPath));
var dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");

builder.Services.AddSingleton<AgentStorageSettings>();
builder.Services.AddSingleton(new SpoolStore(dataRoot));
builder.Services.AddSingleton<AgentApiClient>();
builder.Services.AddSingleton<LocalArchiveWriter>();
builder.Services.AddSingleton<RawChunkRecovery>();
builder.Services.AddSingleton<SessionFinalizationCoordinator>();
builder.Services.AddSingleton<RecordingDeliveryCoordinator>();
builder.Services.AddSingleton<AudioGraphDeviceCatalog>();
builder.Services.AddSingleton<IAudioDeviceCatalog>(services => services.GetRequiredService<AudioGraphDeviceCatalog>());
builder.Services.AddSingleton<AudioGraphCaptureEngine>();
builder.Services.AddSingleton<IAudioCaptureEngine>(services => services.GetRequiredService<AudioGraphCaptureEngine>());
builder.Services.AddSingleton<IAudioDeviceProbe, AudioGraphDeviceProbe>();
builder.Services.AddSingleton<IAudioCaptureEngineFactory, AudioGraphCaptureEngineFactory>();
builder.Services.AddSingleton<RecorderHostRuntime>();
builder.Services.AddHostedService<RecorderHostPipeServer>();
builder.Services.AddHostedService<RecorderHostWorker>();

await builder.Build().RunAsync();

public sealed class RecorderHostWorker(RecorderHostRuntime runtime, ILogger<RecorderHostWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await runtime.ReconcileBackgroundAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "Recorder Host background worker failed; local spool remains authoritative."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
