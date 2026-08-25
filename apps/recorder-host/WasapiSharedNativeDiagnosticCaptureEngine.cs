using System.Security.Cryptography;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WhisperX.Atom.Recorder;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// Diagnostic-only WASAPI shared capture that preserves the endpoint mix
/// format. It is intentionally not registered as IAudioCaptureEngine. The
/// canonical AudioGraph path remains the production default until a measured
/// capture/transcription gate promotes this candidate.
/// </summary>
public sealed class WasapiSharedNativeDiagnosticCaptureEngine
{
    public async Task<AudioCaptureAbResult> CaptureAsync(
        string? deviceId,
        int durationSeconds,
        bool keepAudio,
        string diagnosticDirectory,
        CancellationToken cancellationToken = default)
    {
        durationSeconds = Math.Clamp(durationSeconds, 1, 40);
        Directory.CreateDirectory(diagnosticDirectory);
        using var endpoint = ResolveEndpoint(deviceId);
        if (endpoint is null)
            return Failure(deviceId, durationSeconds, "SHARED_NATIVE_ENDPOINT_UNAVAILABLE", diagnosticDirectory, keepAudio);

        AudioClient? client = null;
        try
        {
            client = await AudioClient.ActivateAsync(endpoint.ID, new AudioClientProperties
            {
                cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                eCategory = AudioStreamCategory.Media,
                Options = AudioClientStreamOptions.None
            }).ConfigureAwait(false);
            var native = client.MixFormat;
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                10_000_000, 0, native, Guid.Empty);
            var capture = client.AudioCaptureClient;
            var bytes = new List<byte>(Math.Max(1, native.AverageBytesPerSecond * durationSeconds));
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
                        var count = checked(frames * native.BlockAlign);
                        if (count > 0 && flags.HasFlag(AudioClientBufferFlags.Silent))
                            bytes.AddRange(new byte[count]);
                        else if (pointer != IntPtr.Zero && count > 0)
                        {
                            var block = new byte[count];
                            Marshal.Copy(pointer, block, 0, count);
                            bytes.AddRange(block);
                        }
                    }
                    finally { capture.ReleaseBuffer(frames); }
                    packets = capture.GetNextPacketSize();
                }
            }
            client.Stop();
            var path = Path.Combine(diagnosticDirectory, "shared-native.wav");
            using (var writer = new WaveFileWriter(path, native))
                writer.Write(bytes.ToArray(), 0, bytes.Count);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var descriptor = AudioSampleFormatResolver.Resolve(native);
            return new AudioCaptureAbResult(
                Success: bytes.Count > 0,
                DeviceId: endpoint.ID,
                DurationSeconds: durationSeconds,
                RawModeSupported: true,
                ErrorCode: bytes.Count > 0 ? null : "SHARED_NATIVE_EMPTY",
                DiagnosticDirectory: keepAudio ? diagnosticDirectory : null,
                AudioDeletedByDefault: !keepAudio,
                CaptureVariant: "WASAPI_SHARED_NATIVE",
                NativeSampleRate: descriptor.SampleRate,
                NativeChannels: descriptor.Channels,
                NativeSampleFormat: descriptor.CanonicalEncoding,
                NativeSha256: hash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(endpoint.ID, durationSeconds, "SHARED_NATIVE_UNSUPPORTED", diagnosticDirectory, keepAudio, ex.GetType().Name);
        }
        finally
        {
            try { client?.Dispose(); } catch { }
            if (!keepAudio) TryDeleteDirectory(diagnosticDirectory);
        }
    }

    private static MMDevice? ResolveEndpoint(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try { return enumerator.GetDevice(deviceId); } catch { return null; }
        }
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
        catch
        {
            try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
            catch { return null; }
        }
    }

    private static AudioCaptureAbResult Failure(string? id, int seconds, string code, string directory, bool keep, string? detail = null)
    {
        if (!keep) TryDeleteDirectory(directory);
        return new AudioCaptureAbResult(false, id, seconds, false, code,
            DiagnosticDirectory: keep ? directory : null,
            AudioDeletedByDefault: !keep,
            CaptureVariant: "WASAPI_SHARED_NATIVE",
            PhaseMetadata: detail is null ? "UNCONFIRMED" : $"{code}:{detail}");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
