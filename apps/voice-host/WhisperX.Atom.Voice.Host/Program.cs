using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WhisperX.Atom.Voice.Host;

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
