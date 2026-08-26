namespace WhisperX.Atom.Recorder;

public enum StorageWatermarkState
{
    NORMAL,
    WARNING,
    CRITICAL,
    BLOCK_RECORDING,
    EMERGENCY
}

public sealed record StorageWatermark(
    StorageWatermarkState State,
    long FreeBytes,
    long TotalBytes,
    double FreePercent,
    long BlockFreeBytes,
    string? Reason = null,
    long CaptureReserveBytes = 0,
    long PostProcessingReserveBytes = 0,
    long EmergencyStopFreeBytes = 0)
{
    public bool AllowsRecording => State is not (StorageWatermarkState.BLOCK_RECORDING or StorageWatermarkState.EMERGENCY);
    public bool IsEmergency => State == StorageWatermarkState.EMERGENCY;
}

public sealed record StorageRetentionPolicy(
    TimeSpan TransportGrace,
    TimeSpan RawRecoveryGrace,
    TimeSpan TemporaryProcessing,
    TimeSpan LogRetention,
    TimeSpan DiagnosticRetention,
    TimeSpan LocalMasterRetention,
    TimeSpan ServerArchiveRetention,
    TimeSpan PlayableAudioRetention,
    int WarningPercent,
    int CriticalPercent,
    long BlockFreeBytes,
    long CaptureReserveBytes = 0,
    long PostProcessingReserveBytes = 0,
    long EmergencyStopFreeBytes = 0)
{
    public static StorageRetentionPolicy FromEnvironment()
    {
        static int ReadPercent(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? Math.Clamp(value, 1, 99) : fallback;
        static long ReadBytes(string name, long fallback) =>
            long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
        static int ReadInt(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? Math.Clamp(value, 1, 8) : fallback;
        static TimeSpan ReadHours(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0
                ? TimeSpan.FromHours(value)
                : TimeSpan.FromHours(fallback);
        static TimeSpan ReadDays(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0
                ? TimeSpan.FromDays(value)
                : TimeSpan.FromDays(fallback);

        var explicitBlockBytes = ReadBytes("WHISPERX_STORAGE_BLOCK_FREE_BYTES", 512L * 1024 * 1024);
        if (long.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_MIN_FREE_BYTES"), out var legacy) && legacy > 0)
            explicitBlockBytes = legacy;
        // Keep enough room for a recoverable recording even when the static
        // reserve is left at its legacy 512 MiB value.  Two mono 48 kHz
        // PCM16 tracks for two hours are about 1.3 GiB; add a configurable
        // overhead for SQLite/WAV parts and use the larger of the explicit
        // reserve and the calculated floor.  Set the expected hours/tracks
        // to match a deployment's recording profile.
        var expectedHours = ReadHours("WHISPERX_STORAGE_EXPECTED_RECORDING_HOURS", 2).TotalHours;
        var trackCount = ReadInt("WHISPERX_STORAGE_EXPECTED_TRACKS", 2);
        var overheadPercent = ReadPercent("WHISPERX_STORAGE_RESERVE_OVERHEAD_PERCENT", 25);
        var rawBytes = 48000d * 2d * trackCount * expectedHours * 3600d;
        var calculatedReserve = rawBytes * (1d + overheadPercent / 100d);
        var calculatedReserveBytes = checked((long)Math.Ceiling(calculatedReserve));
        var captureReserveBytes = Math.Max(explicitBlockBytes, calculatedReserveBytes);
        var postProcessingReserveBytes = ReadBytes(
            "WHISPERX_STORAGE_POSTPROCESSING_RESERVE_BYTES", 1024L * 1024 * 1024);
        var requiredFreeBytes = checked(captureReserveBytes + postProcessingReserveBytes);
        var emergencyStopFreeBytes = ReadBytes(
            "WHISPERX_STORAGE_EMERGENCY_STOP_FREE_BYTES",
            checked(postProcessingReserveBytes + 128L * 1024 * 1024));
        return new StorageRetentionPolicy(
            ReadHours("WHISPERX_RETENTION_TRANSPORT_GRACE_HOURS", 24),
            ReadHours("WHISPERX_RETENTION_RAW_GRACE_HOURS", 24),
            ReadHours("WHISPERX_RETENTION_TEMP_HOURS", 72),
            ReadDays("WHISPERX_RETENTION_LOG_DAYS", 30),
            ReadDays("WHISPERX_RETENTION_DIAGNOSTIC_DAYS", 14),
            ReadDays("WHISPERX_RETENTION_LOCAL_MASTER_DAYS", 0),
            ReadDays("WHISPERX_RETENTION_SERVER_ARCHIVE_DAYS", 0),
            ReadHours("WHISPERX_RETENTION_PLAYABLE_HOURS", 24),
            ReadPercent("WHISPERX_STORAGE_WARNING_PERCENT", 15),
            ReadPercent("WHISPERX_STORAGE_CRITICAL_PERCENT", 8),
            requiredFreeBytes,
            captureReserveBytes,
            postProcessingReserveBytes,
            emergencyStopFreeBytes);
    }

    public StorageWatermark Evaluate(long freeBytes, long totalBytes)
    {
        var freePercent = totalBytes > 0 ? Math.Clamp(freeBytes * 100d / totalBytes, 0d, 100d) : 0d;
        if (freeBytes < BlockFreeBytes)
            return new(StorageWatermarkState.BLOCK_RECORDING, freeBytes, totalBytes, freePercent, BlockFreeBytes, "absolute_free_space_reserve", CaptureReserveBytes, PostProcessingReserveBytes, EmergencyStopFreeBytes);
        if (freePercent < CriticalPercent)
            return new(StorageWatermarkState.CRITICAL, freeBytes, totalBytes, freePercent, BlockFreeBytes, "free_space_below_critical_percent", CaptureReserveBytes, PostProcessingReserveBytes, EmergencyStopFreeBytes);
        if (freePercent < WarningPercent)
            return new(StorageWatermarkState.WARNING, freeBytes, totalBytes, freePercent, BlockFreeBytes, "free_space_below_warning_percent", CaptureReserveBytes, PostProcessingReserveBytes, EmergencyStopFreeBytes);
        return new(StorageWatermarkState.NORMAL, freeBytes, totalBytes, freePercent, BlockFreeBytes, null, CaptureReserveBytes, PostProcessingReserveBytes, EmergencyStopFreeBytes);
    }

    /// <summary>Watermark used while a durable recording is active.</summary>
    public StorageWatermark EvaluateDuringRecording(long freeBytes, long totalBytes)
    {
        var freePercent = totalBytes > 0 ? Math.Clamp(freeBytes * 100d / totalBytes, 0d, 100d) : 0d;
        if (freeBytes < EmergencyStopFreeBytes)
            return new(StorageWatermarkState.EMERGENCY, freeBytes, totalBytes, freePercent, BlockFreeBytes, "recording_emergency_free_space", CaptureReserveBytes, PostProcessingReserveBytes, EmergencyStopFreeBytes);
        if (freePercent < CriticalPercent)
            return new(StorageWatermarkState.CRITICAL, freeBytes, totalBytes, freePercent, BlockFreeBytes, "free_space_below_critical_percent", CaptureReserveBytes, PostProcessingReserveBytes, EmergencyStopFreeBytes);
        if (freePercent < WarningPercent)
            return new(StorageWatermarkState.WARNING, freeBytes, totalBytes, freePercent, BlockFreeBytes, "free_space_below_warning_percent", CaptureReserveBytes, PostProcessingReserveBytes, EmergencyStopFreeBytes);
        return new(StorageWatermarkState.NORMAL, freeBytes, totalBytes, freePercent, BlockFreeBytes, null, CaptureReserveBytes, PostProcessingReserveBytes, EmergencyStopFreeBytes);
    }
}
