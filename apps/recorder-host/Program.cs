using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhisperX.Atom.Recorder;
using WhisperX.Atom.Recorder.Host;

using var processGuard = RecorderHostProcessGuard.TryAcquire()
    ?? throw new InvalidOperationException("RECORDER_HOST_ALREADY_RUNNING");

var builder = Host.CreateApplicationBuilder(args);
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
