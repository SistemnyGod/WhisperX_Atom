using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Keeps the device snapshot event-driven. CoreAudio notifications invalidate
/// the snapshot immediately; a 45-second reconciliation protects against a
/// missed notification or a driver that does not emit one.
/// </summary>
public sealed class DeviceHealthMonitor : IDisposable
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(45);
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly NotificationClient? _notificationClient;
    private DeviceHealthSnapshot? _snapshot;
    private long _revision = 1;
    private long _snapshotRevision;
    private DateTimeOffset _snapshotAt = DateTimeOffset.MinValue;
    private string? _snapshotDataRoot;
    private string? _snapshotMicrophoneId;
    private string? _snapshotSystemAudioId;

    public DeviceHealthMonitor()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _notificationClient = new NotificationClient(() => Interlocked.Increment(ref _revision));
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        }
        catch (Exception)
        {
            // DeviceHealthSnapshot.Collect remains the safe fallback when the
            // notification API is unavailable on a broken audio stack.
        }
    }

    public DeviceHealthSnapshot Collect(string dataRoot, AgentStorageSettings? storage = null)
    {
        var microphoneId = storage?.MicrophoneDeviceId;
        var systemAudioId = storage?.SystemAudioDeviceId;
        var revision = Interlocked.Read(ref _revision);
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var canReuse = _snapshot is not null
                && _snapshotRevision == revision
                && string.Equals(_snapshotDataRoot, dataRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_snapshotMicrophoneId, microphoneId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_snapshotSystemAudioId, systemAudioId, StringComparison.OrdinalIgnoreCase)
                && now - _snapshotAt < ReconciliationInterval;
            if (canReuse) return _snapshot!;

            var snapshot = DeviceHealthSnapshot.Collect(dataRoot, storage);
            _snapshot = snapshot;
            _snapshotRevision = Interlocked.Read(ref _revision);
            _snapshotAt = now;
            _snapshotDataRoot = dataRoot;
            _snapshotMicrophoneId = microphoneId;
            _snapshotSystemAudioId = systemAudioId;
            return snapshot;
        }
    }

    public void Dispose()
    {
        if (_enumerator is null || _notificationClient is null) return;
        try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); }
        catch (Exception) { }
        _enumerator.Dispose();
    }

    private sealed class NotificationClient(Action invalidate) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => invalidate();
        public void OnDeviceAdded(string pwstrDeviceId) => invalidate();
        public void OnDeviceRemoved(string deviceId) => invalidate();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => invalidate();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => invalidate();
    }
}
