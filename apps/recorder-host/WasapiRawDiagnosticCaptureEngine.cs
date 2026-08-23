using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// Diagnostic-only WASAPI RAW capture. It uses AudioClient RAW activation and
/// a controlled native-format -> mono 48 kHz PCM16 conversion. This class is
/// never registered as IAudioCaptureEngine, so canonical AudioGraph recording
/// remains unchanged until measured A/B evidence justifies a migration.
/// </summary>
public sealed class WasapiRawDiagnosticCaptureEngine
{
    public async Task<AudioCaptureAbResult> CaptureAsync(
        string? deviceId,
        int durationSeconds,
        bool keepAudio,
        string diagnosticDirectory,
        double silenceSeconds = 3,
        double speechSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        silenceSeconds = Math.Clamp(silenceSeconds, 0, 10);
        speechSeconds = Math.Clamp(speechSeconds, 1, 30);
        durationSeconds = Math.Clamp(durationSeconds, (int)Math.Ceiling(silenceSeconds + speechSeconds), 40);
        Directory.CreateDirectory(diagnosticDirectory);
        var endpoint = ResolveEndpoint(deviceId);
        if (endpoint is null)
            return Failure(deviceId, durationSeconds, "RAW_ENDPOINT_UNAVAILABLE", diagnosticDirectory, keepAudio);

        AudioClient? client = null;
        try
        {
            var properties = new AudioClientProperties
            {
                cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                eCategory = AudioStreamCategory.Media,
                Options = AudioClientStreamOptions.Raw
            };
            client = await AudioClient.ActivateAsync(endpoint.ID, properties).ConfigureAwait(false);
            var native = client.MixFormat;
            // This diagnostic reader polls GetNextPacketSize. EventCallback
            // requires SetEventHandle before Initialize and would make RAW
            // capture fail with AUDCLNT_E_EVENTHANDLE_NOT_SET on otherwise
            // supported devices. Use polling mode deliberately.
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                10_000_000, 0, native, Guid.Empty);
            var capture = client.AudioCaptureClient;
            var nativeBytes = new List<byte>(Math.Max(1, native.AverageBytesPerSecond * durationSeconds));
            client.Start();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(durationSeconds);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packets = capture.GetNextPacketSize();
                if (packets <= 0)
                {
                    await Task.Delay(8, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                while (packets > 0)
                {
                    var flags = AudioClientBufferFlags.None;
                    var frames = 0;
                    var position = 0L;
                    var qpc = 0L;
                    var pointer = capture.GetBuffer(out frames, out flags, out position, out qpc);
                    try
                    {
                        var byteCount = checked(frames * native.BlockAlign);
                        if (pointer != IntPtr.Zero && byteCount > 0)
                        {
                            var buffer = new byte[byteCount];
                            Marshal.Copy(pointer, buffer, 0, byteCount);
                            nativeBytes.AddRange(buffer);
                        }
                    }
                    finally { capture.ReleaseBuffer(frames); }
                    packets = capture.GetNextPacketSize();
                }
            }
            client.Stop();

            var pcm = ConvertToCanonical(nativeBytes.ToArray(), native);
            var path = Path.Combine(diagnosticDirectory, "raw.wav");
            using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
                writer.Write(pcm, 0, pcm.Length);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var samples = new short[pcm.Length / 2];
            Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
            var silenceCount = Math.Min(samples.Length, (int)Math.Round(48000 * silenceSeconds));
            var speechStart = Math.Min(samples.Length, silenceCount);
            var speechCount = Math.Min(samples.Length - speechStart, (int)Math.Round(48000 * speechSeconds));
            var quality = AudioQualityAnalyzer.AnalyzePcm16(
                samples.AsSpan(speechStart, speechCount),
                samples.AsSpan(0, silenceCount));
            if (!keepAudio) TryDeleteDirectory(diagnosticDirectory);
            return new AudioCaptureAbResult(true, endpoint.ID, durationSeconds, true, null,
                RawSha256: hash, RawQuality: quality,
                DiagnosticDirectory: keepAudio ? diagnosticDirectory : null,
                AudioDeletedByDefault: !keepAudio,
                SilenceSeconds: silenceSeconds,
                SpeechSeconds: speechCount / 48000d,
                NoiseWindowConfirmed: silenceCount > 0 && speechCount > 0,
                PhaseMetadata: "SILENCE_THEN_SPEECH");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(endpoint.ID, durationSeconds, "RAW_MODE_UNSUPPORTED", diagnosticDirectory, keepAudio);
        }
        finally
        {
            try { client?.Dispose(); } catch { }
            if (!keepAudio) TryDeleteDirectory(diagnosticDirectory);
            endpoint.Dispose();
        }
    }

    private static MMDevice? ResolveEndpoint(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try { return enumerator.GetDevice(deviceId); }
            catch { return null; }
        }
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
        catch
        {
            try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
            catch { return null; }
        }
    }

    private static byte[] ConvertToCanonical(byte[] bytes, WaveFormat format)
    {
        if (bytes.Length == 0) return Array.Empty<byte>();
        var sourceFrames = bytes.Length / Math.Max(1, format.BlockAlign);
        var source = new float[sourceFrames];
        for (var frame = 0; frame < sourceFrames; frame++)
        {
            var sum = 0d;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * format.BlockAlign + channel * format.BitsPerSample / 8;
                sum += ReadSample(bytes, offset, format);
            }
            source[frame] = (float)Math.Clamp(sum / Math.Max(1, format.Channels), -1d, 1d);
        }
        var targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * 48000d / Math.Max(1, format.SampleRate)));
        var result = new byte[targetFrames * 2];
        for (var i = 0; i < targetFrames; i++)
        {
            var position = i * (source.Length - 1d) / Math.Max(1, targetFrames - 1);
            var left = (int)Math.Floor(position);
            var right = Math.Min(source.Length - 1, left + 1);
            var fraction = position - left;
            var sample = source[left] + (source[right] - source[left]) * fraction;
            var value = (short)Math.Clamp(Math.Round(sample * 32767d), short.MinValue, short.MaxValue);
            result[i * 2] = (byte)(value & 0xff);
            result[i * 2 + 1] = (byte)((value >> 8) & 0xff);
        }
        return result;
    }

    private static float ReadSample(byte[] data, int offset, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return BitConverter.ToSingle(data, offset);
        if (format.BitsPerSample == 16)
            return BitConverter.ToInt16(data, offset) / 32768f;
        if (format.BitsPerSample == 24)
        {
            var value = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
            if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000);
            return value / 8388608f;
        }
        if (format.BitsPerSample == 32)
            return BitConverter.ToInt32(data, offset) / 2147483648f;
        return 0;
    }

    private static AudioCaptureAbResult Failure(string? id, int seconds, string code, string directory, bool keep)
    {
        if (!keep) TryDeleteDirectory(directory);
        return new AudioCaptureAbResult(false, id, seconds, false, code,
            DiagnosticDirectory: keep ? directory : null, AudioDeletedByDefault: !keep);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
