using System.Diagnostics;
using System.Windows;

namespace WhisperX_Atom_Desktop.Services;

/// <summary>Owns the hidden, current-user Voice Host lifetime.</summary>
public sealed class VoiceHostController : IAsyncDisposable
{
    private readonly FrontendServices _services;
    private Process? _process;
    private DesktopVoiceBrokerServer? _broker;

    public VoiceHostController(FrontendServices services) => _services = services;
    public string State { get; private set; } = "OFF";
    public string? LastErrorCode { get; private set; }
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false }) return true;
        var executable = ResolveExecutable();
        if (executable is null)
        {
            State = "NEEDS_SETUP";
            LastErrorCode = "VOICE_HOST_NOT_INSTALLED";
            return false;
        }

        _broker ??= new DesktopVoiceBrokerServer(_services.RecordingCommands);
        _broker.Start();
        var settings = _services.Settings.Load();
        var arguments = $"--managed --parent-pid {Environment.ProcessId} --broker-pipe WhisperXAtomDesktopVoiceBroker";
        if (!string.IsNullOrWhiteSpace(settings.MicrophoneDeviceId))
            arguments += $" --microphone-id \"{settings.MicrophoneDeviceId.Replace("\"", string.Empty)}\"";
        try
        {
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (_process is null) throw new InvalidOperationException("Process.Start returned null.");
            State = "STARTING";
            LastErrorCode = null;
            await WaitForPipeAsync(cancellationToken).ConfigureAwait(false);
            State = "LISTENING";
            return true;
        }
        catch (Exception ex)
        {
            LastErrorCode = ex is System.ComponentModel.Win32Exception ? "VOICE_HOST_START_FAILED" : "VOICE_HOST_UNAVAILABLE";
            State = "ERROR";
            return false;
        }
    }

    private async Task WaitForPipeAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var client = new WhisperX.Atom.Desktop.VoiceHostClient();
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status is not null) return;
            }
            catch { }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("VOICE_HOST_PIPE_TIMEOUT");
    }

    public async Task StopAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                try
                {
                    var client = new WhisperX.Atom.Desktop.VoiceHostClient();
                    await client.SendAsync("SHUTDOWN").ConfigureAwait(false);
                }
                catch { }
                if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
            State = "OFF";
            if (_broker is not null) await _broker.DisposeAsync().ConfigureAwait(false);
            _broker = null;
        }
    }

    private static string? ResolveExecutable()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WhisperX Atom", "VoiceHost", "WhisperX.Atom.Voice.Host.exe");
        return File.Exists(installed) ? installed : null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
