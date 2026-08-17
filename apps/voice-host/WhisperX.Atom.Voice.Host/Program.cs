using System.Drawing;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WhisperX.Atom.Voice;
using WhisperX.Atom.Voice.Host;

if (args.Length >= 2 && string.Equals(args[0], "--model-smoke", StringComparison.OrdinalIgnoreCase))
{
    using var recognizer = new VoskRecognizer(args[1], grammar: new[] { "атом", "[unk]" });
    recognizer.Accept(new byte[32000]);
    recognizer.FinalizeSessionResult();
    Console.WriteLine("Vosk model/native smoke passed.");
    return;
}
if (args.Any(argument => string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase)))
{
    VoiceCoreSelfTest.Run();
    VoiceHostSelfTest.Run();
    return;
}

if (args.Any(argument => string.Equals(argument, "--replay", StringComparison.OrdinalIgnoreCase)))
{
    var replayIndex = Array.FindIndex(args, argument => string.Equals(argument, "--replay", StringComparison.OrdinalIgnoreCase));
    if (replayIndex < 0 || replayIndex + 1 >= args.Length || !args.Any(argument => string.Equals(argument, "--dry-run", StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine("Usage: --replay <audio> --dry-run");
        Environment.ExitCode = 2;
        return;
    }
    Environment.ExitCode = await VoiceAcceptanceRunner.ReplayAsync(args[replayIndex + 1], CancellationToken.None);
    return;
}
if (args.Any(argument => string.Equals(argument, "--mic-acceptance", StringComparison.OrdinalIgnoreCase)))
{
    var acceptanceIndex = Array.FindIndex(args, argument => string.Equals(argument, "--mic-acceptance", StringComparison.OrdinalIgnoreCase));
    var target = acceptanceIndex >= 0 && acceptanceIndex + 1 < args.Length && int.TryParse(args[acceptanceIndex + 1], out var parsedTarget)
        ? Math.Clamp(parsedTarget, 1, 500)
        : 50;
    var microphoneIdIndex = Array.FindIndex(args, argument => string.Equals(argument, "--microphone-id", StringComparison.OrdinalIgnoreCase));
    var acceptanceMicrophoneId = microphoneIdIndex >= 0 && microphoneIdIndex + 1 < args.Length ? args[microphoneIdIndex + 1] : null;
    using var acceptanceCancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; acceptanceCancellation.Cancel(); };
    Environment.ExitCode = await VoiceAcceptanceRunner.MicrophoneAsync(target, TimeSpan.FromMinutes(15), acceptanceCancellation.Token, acceptanceMicrophoneId);
    return;
}
if (args.Any(argument => string.Equals(argument, "--doctor", StringComparison.OrdinalIgnoreCase)))
{
    await using var doctorRuntime = new VoiceHostRuntime();
    var doctor = await doctorRuntime.RunDoctorAsync();
    Console.WriteLine(JsonSerializer.Serialize(doctor, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    Environment.ExitCode = doctor.ModelReady && doctor.ModelIntegrityReady && doctor.NativeRuntimeReady && doctor.MicrophoneReady && doctor.RecorderPipeReady ? 0 : 5;
    return;
}

using var singleInstance = VoiceHostRuntimeLease.TryAcquire();
if (singleInstance is null) return;
var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "VoiceHost");
Directory.CreateDirectory(dataRoot);
Log.Logger = new LoggerConfiguration().WriteTo.File(Path.Combine(dataRoot, "voice-host-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14).CreateLogger();

try
{
    var host = Host.CreateApplicationBuilder(args);
    host.Services.AddSingleton<VoiceHostRuntime>();
    host.Services.AddHostedService<VoiceHostPipeServer>();
    host.Services.AddHostedService<VoiceTelemetryPipeServer>();
    host.Services.AddSerilog();
    using var built = host.Build();
    var runtime = built.Services.GetRequiredService<VoiceHostRuntime>();
    var managed = args.Any(argument => string.Equals(argument, "--managed", StringComparison.OrdinalIgnoreCase));
    var brokerIndex = Array.FindIndex(args, argument => string.Equals(argument, "--broker-pipe", StringComparison.OrdinalIgnoreCase));
    var brokerPipe = brokerIndex >= 0 && brokerIndex + 1 < args.Length ? args[brokerIndex + 1] : null;
    var microphoneIndex = Array.FindIndex(args, argument => string.Equals(argument, "--microphone-id", StringComparison.OrdinalIgnoreCase));
    var microphoneId = microphoneIndex >= 0 && microphoneIndex + 1 < args.Length ? args[microphoneIndex + 1] : null;
    if (managed) runtime.ConfigureManagedBroker(brokerPipe);
    runtime.ConfigureMicrophone(microphoneId);
    runtime.Start();
    await built.StartAsync();
    if (managed)
    {
        using var parentCancellation = new CancellationTokenSource();
        var parentPid = ReadIntArgument(args, "--parent-pid");
        var monitor = ParentMonitorAsync(parentPid, parentCancellation.Token);
        await Task.WhenAny(monitor, runtime.WaitForShutdownAsync());
        parentCancellation.Cancel();
        await built.StopAsync(TimeSpan.FromSeconds(2));
        return;
    }
    using var context = new VoiceTrayContext(runtime, built);
    Application.Run(context);
}
finally
{
    await Log.CloseAndFlushAsync();
}

static int? ReadIntArgument(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length && int.TryParse(arguments[index + 1], out var value) ? value : null;
}

static async Task ParentMonitorAsync(int? parentPid, CancellationToken cancellationToken)
{
    if (parentPid is not int pid || pid <= 0) return;
    try
    {
        using var parent = Process.GetProcessById(pid);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (parent.HasExited) return;
            await Task.Delay(1000, cancellationToken);
        }
    }
    catch (ArgumentException) { }
    catch (InvalidOperationException) { }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
}

internal sealed class VoiceTrayContext(VoiceHostRuntime runtime, IHost host) : ApplicationContext
{
    private readonly NotifyIcon _icon = CreateIcon(runtime);

    private static NotifyIcon CreateIcon(VoiceHostRuntime runtime)
    {
        var menu = new ContextMenuStrip();
        var state = new ToolStripMenuItem("Состояние");
        state.Click += (_, _) => MessageBox.Show(runtime.Snapshot.State.ToString(), "WhisperX Atom");
        var toggle = new ToolStripMenuItem("Включить помощник") { Checked = true, CheckOnClick = true };
        toggle.CheckedChanged += (_, _) => runtime.SetEnabled(toggle.Checked);
        var quit = new ToolStripMenuItem("Выход");
        quit.Click += (_, _) => Application.Exit();
        menu.Items.Add(state); menu.Items.Add(toggle); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(quit);
        return new NotifyIcon { Icon = SystemIcons.Application, Visible = true, Text = "WhisperX Atom — голосовой помощник", ContextMenuStrip = menu };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            host.Dispose();
        }
        base.Dispose(disposing);
    }
}
