using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// The single controlled WASAPI probe used by both the interactive probe runner
/// and Recorder Service. It intentionally measures the stream, not just endpoint
/// enumeration, so "device exists" cannot be reported as capture readiness.
/// </summary>
public static class AudioRuntimeProbe
{
    public static async Task<AudioSourceTestResult> RunAsync(
        string? requestedDeviceId,
        bool systemAudio,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var flow = systemAudio ? DataFlow.Render : DataFlow.Capture;
        var process = Process.GetCurrentProcess();
        var processUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var processSid = TryGetWindowsSid();
        var sessionId = process.SessionId;
        var processId = process.Id;
        var endpointFound = false;
        var endpointActive = false;
        var accessGranted = false;
        var formatResolved = false;
        var streamOpened = false;
        var streamStarted = false;
        var packetCount = 0;
        long bytesReceived = 0;
        long? firstPacketLatencyMs = null;
        string? selectedEndpointId = null;
        string? friendlyName = null;
        string? defaultMultimediaEndpointId = null;
        string? defaultCommunicationsEndpointId = null;
        string? normalizedFormat = null;
        int? sampleRate = null;
        int? channels = null;
        var clipping = false;
        var rmsSum = 0d;
        var peak = 0d;
        var measuredPackets = 0;
        string? errorCode = null;
        string? errorDetail = null;
        MMDevice? endpoint = null;
        IWaveIn? capture = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            defaultMultimediaEndpointId = TryGetDefaultEndpointId(enumerator, flow, Role.Multimedia);
            defaultCommunicationsEndpointId = TryGetDefaultEndpointId(enumerator, flow, Role.Communications);

            endpoint = string.IsNullOrWhiteSpace(requestedDeviceId)
                ? TryGetDefaultEndpoint(enumerator, flow, Role.Multimedia)
                : TryGetEndpoint(enumerator, requestedDeviceId);
            endpointFound = endpoint is not null;
            endpointActive = endpoint?.State == DeviceState.Active;
            selectedEndpointId = endpoint?.ID ?? requestedDeviceId;
            friendlyName = endpoint?.FriendlyName;
            if (!endpointFound)
                throw new InvalidOperationException("AUDIO_DEFAULT_ENDPOINT_MISSING");
            if (!endpointActive)
                throw new InvalidOperationException("DEVICE_INACTIVE");

            capture = systemAudio ? new WasapiLoopbackCapture(endpoint) : new WasapiCapture(endpoint);
            streamOpened = true;
            accessGranted = true;
            var format = capture.WaveFormat;
            var descriptor = AudioSampleFormatResolver.Resolve(format);
            formatResolved = true;
            normalizedFormat = descriptor.CanonicalEncoding;
            sampleRate = descriptor.SampleRate;
            channels = descriptor.Channels;

            var gate = new object();
            void OnData(object? _, WaveInEventArgs args)
            {
                lock (gate)
                {
                    packetCount++;
                    bytesReceived += args.BytesRecorded;
                    firstPacketLatencyMs ??= ElapsedMilliseconds(started);
                    var telemetry = Measure(args.Buffer, args.BytesRecorded, descriptor);
                    if (!telemetry.HasValue) return;
                    measuredPackets++;
                    rmsSum += telemetry.Value.Rms;
                    peak = Math.Max(peak, telemetry.Value.Peak);
                    clipping |= telemetry.Value.Clipping;
                }
            }

            capture.DataAvailable += OnData;
            try
            {
                capture.StartRecording();
                streamStarted = true;
                await Task.Delay(duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(3) : duration, cancellationToken);
            }
            finally
            {
                try { capture.StopRecording(); } catch (InvalidOperationException) { }
                capture.DataAvailable -= OnData;
            }

            var hasPackets = packetCount > 0 && bytesReceived > 0;
            var averageRms = measuredPackets == 0 ? 0 : rmsSum / measuredPackets;
            var averageRmsDb = ToDb(averageRms);
            var peakDb = ToDb(peak);
            var signalDetected = measuredPackets > 0 && averageRms >= 0.003d;
            return Result(
                success: hasPackets,
                requestedDeviceId,
                friendlyName,
                signalDetected,
                averageRmsDb,
                peakDb,
                clipping,
                ElapsedMilliseconds(started),
                hasPackets ? null : "AUDIO_NO_DATA",
                streamOpened,
                streamStarted,
                packetCount,
                bytesReceived,
                firstPacketLatencyMs,
                sampleRate,
                channels,
                normalizedFormat,
                hasPackets ? signalDetected ? "READY" : "READY_NO_SIGNAL" : "NO_PACKETS",
                processUser,
                processSid,
                sessionId,
                processId,
                defaultMultimediaEndpointId,
                defaultCommunicationsEndpointId,
                selectedEndpointId,
                endpointFound,
                endpointActive,
                accessGranted,
                formatResolved,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            errorCode = "AUDIO_TEST_CANCELLED";
            errorDetail = "Probe cancelled by caller.";
        }
        catch (Exception exception)
        {
            errorCode = ClassifyError(exception);
            errorDetail = exception.Message;
        }
        finally
        {
            capture?.Dispose();
            endpoint?.Dispose();
        }

        return Result(
            success: false,
            requestedDeviceId,
            friendlyName,
            false,
            null,
            null,
            clipping,
            ElapsedMilliseconds(started),
            errorCode ?? "AUDIO_TEST_FAILED",
            streamOpened,
            streamStarted,
            packetCount,
            bytesReceived,
            firstPacketLatencyMs,
            sampleRate,
            channels,
            normalizedFormat,
            errorCode == "AUDIO_TEST_CANCELLED" ? "CANCELLED" : "OPEN_FAILED",
            processUser,
            processSid,
            sessionId,
            processId,
            defaultMultimediaEndpointId,
            defaultCommunicationsEndpointId,
            selectedEndpointId,
            endpointFound,
            endpointActive,
            accessGranted,
            formatResolved,
            errorDetail);
    }

    private static AudioSourceTestResult Result(
        bool success,
        string? requestedDeviceId,
        string? friendlyName,
        bool signalDetected,
        double? averageRmsDb,
        double? peakDb,
        bool clipping,
        long durationMs,
        string? errorCode,
        bool streamOpened,
        bool streamStarted,
        int packetCount,
        long bytesReceived,
        long? firstPacketLatencyMs,
        int? sampleRate,
        int? channels,
        string? normalizedFormat,
        string captureState,
        string processUser,
        string? processSid,
        int sessionId,
        int processId,
        string? defaultMultimediaEndpointId,
        string? defaultCommunicationsEndpointId,
        string? selectedEndpointId,
        bool endpointFound,
        bool endpointActive,
        bool accessGranted,
        bool formatResolved,
        string? errorDetail)
        => new(
            success,
            requestedDeviceId,
            friendlyName,
            signalDetected,
            averageRmsDb,
            peakDb,
            clipping,
            durationMs,
            errorCode,
            streamOpened,
            streamStarted,
            packetCount,
            bytesReceived,
            firstPacketLatencyMs,
            sampleRate,
            channels,
            normalizedFormat,
            captureState,
            processUser,
            processSid,
            sessionId,
            processId,
            defaultMultimediaEndpointId,
            defaultCommunicationsEndpointId,
            selectedEndpointId,
            endpointFound,
            endpointActive,
            accessGranted,
            formatResolved,
            errorDetail);

    private static MMDevice? TryGetEndpoint(MMDeviceEnumerator enumerator, string deviceId)
    {
        try { return enumerator.GetDevice(deviceId); }
        catch (Exception exception) when (exception is COMException or ArgumentException)
        { return null; }
    }

    private static MMDevice? TryGetDefaultEndpoint(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try { return enumerator.GetDefaultAudioEndpoint(flow, role); }
        catch (COMException) { return null; }
    }

    private static string? TryGetDefaultEndpointId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        using var endpoint = TryGetDefaultEndpoint(enumerator, flow, role);
        return endpoint?.ID;
    }

    private static string ClassifyError(Exception exception) => exception switch
    {
        NotSupportedException => "AUDIO_FORMAT_UNSUPPORTED",
        UnauthorizedAccessException => "AUDIO_DEVICE_ACCESS_DENIED",
        COMException => "AUDIO_DEVICE_ACCESS_DENIED",
        _ when exception.Message.Contains("AUDIO_DEFAULT_ENDPOINT_MISSING", StringComparison.OrdinalIgnoreCase) => "AUDIO_DEFAULT_ENDPOINT_MISSING",
        _ when exception.Message.Contains("inactive", StringComparison.OrdinalIgnoreCase) => "DEVICE_INACTIVE",
        _ => "AUDIO_TEST_FAILED"
    };

    private static long ElapsedMilliseconds(long started)
        => (long)Math.Round((Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency);

    private static double? ToDb(double rms)
        => rms <= 0 ? -60d : Math.Clamp(20d * Math.Log10(rms), -60d, 0d);

    private static string? TryGetWindowsSid()
    {
        try
        {
            var type = Type.GetType("System.Security.Principal.WindowsIdentity, System.Security.Principal.Windows");
            var identity = type?.GetMethod("GetCurrent", Type.EmptyTypes)?.Invoke(null, null);
            var sid = type?.GetProperty("User")?.GetValue(identity)?.GetType().GetProperty("Value")?.GetValue(type?.GetProperty("User")?.GetValue(identity));
            (identity as IDisposable)?.Dispose();
            return sid as string;
        }
        catch { return null; }
    }

    private static Telemetry? Measure(byte[] buffer, int bytesRecorded, AudioSampleFormatDescriptor descriptor)
    {
        var width = descriptor.BitsPerSample / 8;
        var frameBytes = width * descriptor.Channels;
        if (width <= 0 || frameBytes <= 0 || bytesRecorded < width) return null;
        var sampleCount = 0;
        var sumSquares = 0d;
        var maximum = 0d;
        var clipped = false;
        for (var offset = 0; offset + width <= bytesRecorded; offset += width)
        {
            var value = descriptor.Kind switch
            {
                RawAudioSampleFormat.Pcm16 => BitConverter.ToInt16(buffer, offset) / 32768d,
                RawAudioSampleFormat.Pcm24 => ReadPcm24(buffer, offset) / 8388608d,
                RawAudioSampleFormat.Pcm32 => BitConverter.ToInt32(buffer, offset) / 2147483648d,
                RawAudioSampleFormat.Float32 => BitConverter.ToSingle(buffer, offset),
                _ => 0d
            };
            if (double.IsNaN(value) || double.IsInfinity(value)) continue;
            value = Math.Clamp(value, -1d, 1d);
            maximum = Math.Max(maximum, Math.Abs(value));
            sumSquares += value * value;
            clipped |= Math.Abs(value) >= 0.999d;
            sampleCount++;
        }
        return sampleCount == 0 ? null : new Telemetry(Math.Sqrt(sumSquares / sampleCount), maximum, clipped);
    }

    private static int ReadPcm24(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private readonly record struct Telemetry(double Rms, double Peak, bool Clipping);
}
