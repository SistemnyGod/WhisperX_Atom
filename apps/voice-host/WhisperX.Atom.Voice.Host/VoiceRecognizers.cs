using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace WhisperX.Atom.Voice.Host;

public interface IVoiceRecognizer : IDisposable
{
    Task<string?> RecognizeAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken);
}

public sealed class VoskRecognizer : IVoiceRecognizer
{
    private readonly Model _model;
    private readonly float _sampleRate;
    private readonly string? _grammar;

    public VoskRecognizer(string modelPath, float sampleRate = 16000, IEnumerable<string>? grammar = null)
    {
        if (!Directory.Exists(modelPath)) throw new DirectoryNotFoundException($"Vosk model not found: {modelPath}");
        Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(modelPath);
        _sampleRate = sampleRate;
        _grammar = grammar is null ? null : JsonSerializer.Serialize(grammar.Append("[unk]").ToArray());
    }

    public Task<string?> RecognizeAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var recognizer = string.IsNullOrWhiteSpace(_grammar)
            ? new global::Vosk.VoskRecognizer(_model, _sampleRate)
            : new global::Vosk.VoskRecognizer(_model, _sampleRate, _grammar);
        recognizer.AcceptWaveform(pcm16kMono.ToArray(), pcm16kMono.Length);
        var json = recognizer.FinalResult();
        using var document = JsonDocument.Parse(json);
        return Task.FromResult(document.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null);
    }

    public void Dispose() => _model.Dispose();
}

public sealed class WhisperCppRecognizer(string executablePath, string modelPath, string tempDirectory) : IVoiceRecognizer
{
    public async Task<string?> RecognizeAsync(ReadOnlyMemory<byte> pcm16kMono, CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath) || !File.Exists(modelPath)) return null;
        Directory.CreateDirectory(tempDirectory);
        var wavPath = Path.Combine(tempDirectory, $"voice-{Guid.NewGuid():N}.wav");
        try
        {
            await WriteWavAsync(wavPath, pcm16kMono, cancellationToken);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-m"); process.StartInfo.ArgumentList.Add(modelPath);
            process.StartInfo.ArgumentList.Add("-f"); process.StartInfo.ArgumentList.Add(wavPath);
            process.StartInfo.ArgumentList.Add("-nt");
            process.StartInfo.ArgumentList.Add("-l"); process.StartInfo.ArgumentList.Add("ru");
            process.StartInfo.ArgumentList.Add("--no-timestamps");
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch (IOException) { }
        }
    }

    private static async Task WriteWavAsync(string path, ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        await WriteHeaderAsync(stream, pcm.Length, cancellationToken);
        await stream.WriteAsync(pcm, cancellationToken);
    }

    private static async Task WriteHeaderAsync(Stream stream, int dataLength, CancellationToken cancellationToken)
    {
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BitConverter.GetBytes(36 + dataLength).CopyTo(header, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BitConverter.GetBytes(16).CopyTo(header, 16);
        BitConverter.GetBytes((short)1).CopyTo(header, 20);
        BitConverter.GetBytes((short)1).CopyTo(header, 22);
        BitConverter.GetBytes(16000).CopyTo(header, 24);
        BitConverter.GetBytes(32000).CopyTo(header, 28);
        BitConverter.GetBytes((short)2).CopyTo(header, 32);
        BitConverter.GetBytes((short)16).CopyTo(header, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BitConverter.GetBytes(dataLength).CopyTo(header, 40);
        await stream.WriteAsync(header, cancellationToken);
    }
    public void Dispose() { }
}