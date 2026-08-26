namespace WhisperX_Atom_Desktop.Services;

public enum TransientNotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

public sealed record TransientNotification(
    string Key,
    string Title,
    string Message,
    TransientNotificationSeverity Severity,
    TimeSpan Duration);

/// <summary>
/// Process-local, privacy-safe notification channel. Notifications are never
/// persisted and repeated polling observations are deduplicated by key.
/// </summary>
public sealed class TransientNotificationService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _published = new(StringComparer.Ordinal);

    public event Action<TransientNotification>? Requested;

    public void Publish(
        string key,
        string title,
        string message,
        TransientNotificationSeverity severity = TransientNotificationSeverity.Informational,
        TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(message)) return;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_published.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromMinutes(10))
                return;
            _published[key] = now;
            if (_published.Count > 512)
            {
                foreach (var stale in _published.Where(item => now - item.Value > TimeSpan.FromHours(1)).Select(item => item.Key).ToArray())
                    _published.Remove(stale);
            }
        }

        var requestedDuration = duration ?? (severity == TransientNotificationSeverity.Error
            ? TimeSpan.FromSeconds(10)
            : TimeSpan.FromSeconds(7));
        var boundedDuration = TimeSpan.FromSeconds(Math.Clamp(requestedDuration.TotalSeconds, 5, 10));
        Requested?.Invoke(new TransientNotification(key, title.Trim(), message.Trim(), severity, boundedDuration));
    }
}
