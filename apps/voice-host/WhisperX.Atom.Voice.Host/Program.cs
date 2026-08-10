using System.Drawing;
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
    using var acceptanceCancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; acceptanceCancellation.Cancel(); };
    Environment.ExitCode = await VoiceAcceptanceRunner.MicrophoneAsync(target, TimeSpan.FromMinutes(15), acceptanceCancellation.Token);
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

using var singleInstance = new Mutex(initiallyOwned: true, @"Local\WhisperXAtomVoiceHost", out var ownsInstance);
if (!ownsInstance) return;
var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "VoiceHost");
Directory.CreateDirectory(dataRoot);
Log.Logger = new LoggerConfiguration().WriteTo.File(Path.Combine(dataRoot, "voice-host-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14).CreateLogger();

try
{
    var host = Host.CreateApplicationBuilder(args);
    host.Services.AddSingleton<VoiceHostRuntime>();
    host.Services.AddHostedService<VoiceHostPipeServer>();
    host.Services.AddSerilog();
    using var built = host.Build();
    var runtime = built.Services.GetRequiredService<VoiceHostRuntime>();
    runtime.Start();
    await built.StartAsync();
    using var context = new VoiceTrayContext(runtime, built);
    Application.Run(context);
}
finally
{
    await Log.CloseAndFlushAsync();
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
