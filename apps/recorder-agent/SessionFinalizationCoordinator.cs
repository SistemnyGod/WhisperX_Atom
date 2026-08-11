using System.Collections.Concurrent;

namespace WhisperX.Atom.Recorder;

/// <summary>
/// Serializes archive/delivery work per local recording session. The IPC host
/// and background recovery worker can both observe the same pending session;
/// only one of them may touch its temporary archive files or server receipt.
/// </summary>
public sealed class SessionFinalizationCoordinator
{
    private sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new SemaphoreSlim(1, 1);
        public int References;
    }

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, GateEntry> _gates = new(StringComparer.Ordinal);

    public async Task<T> RunAsync<T>(string sessionId, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("session_required", nameof(sessionId));
        GateEntry entry;
        lock (_gate)
        {
            entry = _gates.GetOrAdd(sessionId, static _ => new GateEntry());
            entry.References++;
        }

        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            acquired = true;
            return await operation(cancellationToken);
        }
        finally
        {
            if (acquired) entry.Semaphore.Release();
            lock (_gate)
            {
                entry.References--;
                if (entry.References == 0 && _gates.TryRemove(new KeyValuePair<string, GateEntry>(sessionId, entry)))
                    entry.Semaphore.Dispose();
            }
        }
    }
}
