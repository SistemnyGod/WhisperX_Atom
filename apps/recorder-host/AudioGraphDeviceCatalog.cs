using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace WhisperX.Atom.Recorder.Host;

/// <summary>
/// User-session device catalog. DeviceWatcher is the source of truth; the
/// reconciliation pass is only a safety net for missed notifications.
/// </summary>
public sealed class AudioGraphDeviceCatalog : IAudioDeviceCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceInformation> _deviceInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioDeviceDescriptor> _devices = new(StringComparer.OrdinalIgnoreCase);
    private DeviceWatcher? _watcher;
    private string? _defaultDeviceId;
    private TaskCompletionSource<bool>? _enumerationCompleted;
    private DateTimeOffset _lastReconcileAtUtc = DateTimeOffset.MinValue;

    public IReadOnlyList<AudioDeviceDescriptor> Devices
    {
        get { lock (_gate) return _devices.Values.OrderBy(x => x.Name).ToArray(); }
    }

    public bool IsReady { get; private set; }

    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsReady) return;
        cancellationToken.ThrowIfCancellationRequested();
        var selector = MediaDevice.GetAudioCaptureSelector();
        _enumerationCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher = DeviceInformation.CreateWatcher(selector);
        _watcher.Added += OnAdded;
        _watcher.Updated += OnUpdated;
        _watcher.Removed += OnRemoved;
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        _watcher.Stopped += OnStopped;
        _watcher.Start();

        using var registration = cancellationToken.Register(() => _enumerationCompleted.TrySetCanceled(cancellationToken));
        // DeviceWatcher can fail to deliver EnumerationCompleted in an
        // unpackaged/current-user process (for example immediately after a
        // Windows audio endpoint restart). Do not keep the Recorder Host in
        // a half-started state with no IPC pipe forever. The direct
        // FindAllAsync reconciliation is authoritative and provides a safe
        // fallback when the watcher notification is lost.
        var completed = await Task.WhenAny(
            _enumerationCompleted.Task,
            Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false);
        if (completed != _enumerationCompleted.Task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
            IsReady = true;
            return;
        }

        await _enumerationCompleted.Task.ConfigureAwait(false);
        IsReady = true;
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (IsReady && DateTimeOffset.UtcNow - _lastReconcileAtUtc < TimeSpan.FromSeconds(30)) return;
        }
        var defaultId = TryGetDefaultDeviceId();
        DeviceInformationCollection discovered;
        try
        {
            discovered = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("AUDIO_DEVICE_DISCOVERY_FAILED", ex);
        }

        var seen = new HashSet<string>(discovered.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
        List<AudioDeviceChangedEventArgs> changes = [];
        string? previousDefault;
        lock (_gate)
        {
            previousDefault = _defaultDeviceId;
            _defaultDeviceId = defaultId;
            _lastReconcileAtUtc = DateTimeOffset.UtcNow;
            foreach (var information in discovered)
            {
                _deviceInfo[information.Id] = information;
                var descriptor = CreateDescriptor(information, defaultId);
                if (!_devices.TryGetValue(information.Id, out var previous) || previous != descriptor)
                {
                    _devices[information.Id] = descriptor;
                    changes.Add(new AudioDeviceChangedEventArgs(
                        previous is null ? "DEVICE_ADDED" : "DEVICE_STATE_CHANGED",
                        descriptor,
                        DateTimeOffset.UtcNow));
                }
            }

            foreach (var removed in _devices.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                var previous = _devices[removed];
                _devices.Remove(removed);
                _deviceInfo.Remove(removed);
                changes.Add(new AudioDeviceChangedEventArgs("DEVICE_REMOVED", previous with { RuntimeStatus = "DEVICE_LOST" }, DateTimeOffset.UtcNow));
            }

            if (!string.Equals(previousDefault, defaultId, StringComparison.OrdinalIgnoreCase)
                && defaultId is not null
                && _devices.TryGetValue(defaultId, out var defaultDevice))
                changes.Add(new AudioDeviceChangedEventArgs("DEFAULT_DEVICE_CHANGED", defaultDevice, DateTimeOffset.UtcNow));
        }

        foreach (var change in changes) DeviceChanged?.Invoke(this, change);
    }

    public AudioDeviceDescriptor? Resolve(AudioSelectionMode mode, string? deviceId)
    {
        if (mode == AudioSelectionMode.Default)
            RefreshDefaultId();
        lock (_gate)
        {
            if (mode == AudioSelectionMode.Fixed)
                return string.IsNullOrWhiteSpace(deviceId) ? null : _devices.GetValueOrDefault(deviceId);
            return _defaultDeviceId is not null && _devices.TryGetValue(_defaultDeviceId, out var selected)
                ? selected
                : _devices.Values.FirstOrDefault(x => x.IsDefault);
        }
    }

    private void RefreshDefaultId()
    {
        var current = TryGetDefaultDeviceId();
        AudioDeviceChangedEventArgs? change = null;
        lock (_gate)
        {
            if (string.Equals(_defaultDeviceId, current, StringComparison.OrdinalIgnoreCase)) return;
            _defaultDeviceId = current;
            foreach (var key in _devices.Keys.ToArray())
            {
                var item = _devices[key];
                _devices[key] = item with { IsDefault = current is not null && string.Equals(key, current, StringComparison.OrdinalIgnoreCase) };
            }
            if (current is not null && _devices.TryGetValue(current, out var device))
            {
                change = new AudioDeviceChangedEventArgs("DEFAULT_DEVICE_CHANGED", device, DateTimeOffset.UtcNow);
            }
        }
        if (change is not null) DeviceChanged?.Invoke(this, change);
    }

    public DeviceInformation? GetDeviceInformation(string deviceId)
    {
        lock (_gate) return _deviceInfo.GetValueOrDefault(deviceId);
    }

    public ValueTask DisposeAsync()
    {
        if (_watcher is not null)
        {
            _watcher.Added -= OnAdded;
            _watcher.Updated -= OnUpdated;
            _watcher.Removed -= OnRemoved;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stopped -= OnStopped;
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                _watcher.Stop();
        }

        _watcher = null;
        lock (_gate)
        {
            _deviceInfo.Clear();
            _devices.Clear();
        }
        return ValueTask.CompletedTask;
    }

    private void OnAdded(DeviceWatcher _, DeviceInformation information)
    {
        AudioDeviceChangedEventArgs? change = null;
        lock (_gate)
        {
            _deviceInfo[information.Id] = information;
            var descriptor = CreateDescriptor(information, _defaultDeviceId);
            if (!_devices.TryGetValue(information.Id, out var previous) || previous != descriptor)
            {
                _devices[information.Id] = descriptor;
                change = new AudioDeviceChangedEventArgs(previous is null ? "DEVICE_ADDED" : "DEVICE_STATE_CHANGED", descriptor, DateTimeOffset.UtcNow);
            }
        }
        if (change is not null) DeviceChanged?.Invoke(this, change);
    }

    private void OnUpdated(DeviceWatcher _, DeviceInformationUpdate update)
    {
        AudioDeviceChangedEventArgs? change = null;
        lock (_gate)
        {
            if (!_deviceInfo.TryGetValue(update.Id, out var information)) return;
            information.Update(update);
            var descriptor = CreateDescriptor(information, _defaultDeviceId);
            _devices[update.Id] = descriptor;
            change = new AudioDeviceChangedEventArgs("DEVICE_STATE_CHANGED", descriptor, DateTimeOffset.UtcNow);
        }
        if (change is not null) DeviceChanged?.Invoke(this, change);
    }

    private void OnRemoved(DeviceWatcher _, DeviceInformationUpdate update)
    {
        AudioDeviceChangedEventArgs? change = null;
        lock (_gate)
        {
            if (_devices.Remove(update.Id, out var previous))
            {
                _deviceInfo.Remove(update.Id);
                change = new AudioDeviceChangedEventArgs("DEVICE_REMOVED", previous with { RuntimeStatus = "DEVICE_LOST" }, DateTimeOffset.UtcNow);
            }
        }
        if (change is not null) DeviceChanged?.Invoke(this, change);
    }

    private void OnEnumerationCompleted(DeviceWatcher _, object __) => _enumerationCompleted?.TrySetResult(true);

    private void OnStopped(DeviceWatcher _, object __)
    {
        if (!IsReady) _enumerationCompleted?.TrySetException(new InvalidOperationException("AUDIO_DEVICE_WATCHER_STOPPED"));
    }

    private string? TryGetDefaultDeviceId()
    {
        try { return MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default); }
        catch { return null; }
    }

    private static AudioDeviceDescriptor CreateDescriptor(DeviceInformation information, string? defaultId)
        => new(
            information.Id,
            string.IsNullOrWhiteSpace(information.Name) ? information.Id : information.Name,
            AudioDataFlow.Capture,
            information.IsEnabled ? "Active" : "Inactive",
            string.Equals(information.Id, defaultId, StringComparison.OrdinalIgnoreCase),
            AudioSelectionMode.Default,
            false,
            DateTimeOffset.UtcNow,
            information.IsEnabled ? "DISCOVERED" : "INACTIVE");
}
