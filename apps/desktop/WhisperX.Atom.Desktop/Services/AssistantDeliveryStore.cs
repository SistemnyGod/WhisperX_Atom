using System.Text.Json;
using System.Threading.Channels;

namespace WhisperX_Atom_Desktop.Services;

public enum AssistantDeliveryState
{
    Pending = 0,
    Dispatching = 1,
    Delivered = 2,
    Expired = 3,
    Accepted = 4,
    Cancelled = 5,
    Ambiguous = 6,
    Reconciling = 7,
    NotAccepted = 8
}

public sealed record AssistantDeliveryEntry(
    Guid? QueryId,
    string? UserId,
    string? ConversationId,
    string? CommandId,
    string? TraceId,
    string? Mode,
    AssistantDeliveryState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatedAt);

public sealed record AssistantDeliveryMetrics(
    int Pending,
    int Dispatching,
    int Delivered,
    int Expired,
    int Accepted,
    int Cancelled,
    int Ambiguous,
    long DuplicateSuppressed,
    long AmbiguousDispatch,
    long Failed,
    int Reconciling = 0,
    int NotAccepted = 0);

/// <summary>
/// Durable, user-scoped delivery ledger for voice Assistant results.
/// It deliberately stores opaque identifiers only: no question, answer or
/// transcript text is written to the local file.
/// </summary>
public sealed class AssistantDeliveryStore
{
    private const int MaximumActiveEntries = 256;
    private static readonly TimeSpan PendingTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan TombstoneTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan DispatchAmbiguityTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PlaybackReconcileAfter = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly Channel<LedgerWrite> _writes = Channel.CreateBounded<LedgerWrite>(new BoundedChannelOptions(16)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _writerShutdown = new();
    private readonly Task _writerTask;
    private bool _disposed;
    private List<AssistantDeliveryEntry> _entries = [];
    private long _duplicateSuppressed;
    private long _ambiguousDispatch;
    private long _failed;

    public AssistantDeliveryStore(string? root = null, Func<DateTimeOffset>? clock = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperXAtom", "Assistant");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "voice-delivery.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _writerTask = Task.Run(WriteLoopAsync);
        Load();
    }
    private readonly Func<DateTimeOffset> _clock;

    private sealed record LedgerWrite(string Payload, TaskCompletionSource<bool> Completion);

    public bool TryAdd(Guid queryId, string? userId, string? commandId, string? traceId, string? mode, string? conversationId = null)
    {
        lock (_gate)
        {
            PruneLocked();
            if (_entries.Any(item => item.QueryId == queryId))
            {
                Interlocked.Increment(ref _duplicateSuppressed);
                return true;
            }
            if (_entries.Count(item => item.State is AssistantDeliveryState.Pending or AssistantDeliveryState.Dispatching or AssistantDeliveryState.Accepted or AssistantDeliveryState.Reconciling) >= MaximumActiveEntries)
            {
                Interlocked.Increment(ref _failed);
                return false;
            }
            var now = _clock();
            _entries.Add(new AssistantDeliveryEntry(queryId, userId, conversationId, commandId, traceId, mode,
                AssistantDeliveryState.Pending, now, now.Add(PendingTtl), now));
            if (!PersistLocked())
            {
                _entries.RemoveAt(_entries.Count - 1);
                Interlocked.Increment(ref _failed);
                return false;
            }
            return true;
        }
    }

    public bool TryAddCommand(string commandId, string? userId, string? traceId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return false;
        lock (_gate)
        {
            PruneLocked();
            var existingIndex = _entries.FindIndex(item => string.Equals(item.CommandId, commandId, StringComparison.Ordinal)
                && string.Equals(item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                if (_entries[existingIndex].State == AssistantDeliveryState.NotAccepted)
                {
                    var retryNow = _clock();
                    _entries[existingIndex] = _entries[existingIndex] with { State = AssistantDeliveryState.Reconciling, UpdatedAt = retryNow, ExpiresAt = retryNow.Add(TimeSpan.FromMinutes(2)) };
                    PersistLocked();
                }
                return true;
            }
            var now = _clock();
            _entries.Add(new AssistantDeliveryEntry(null, userId, null, commandId, traceId, "AUTO",
                AssistantDeliveryState.Reconciling, now, now.Add(TimeSpan.FromMinutes(2)), now));
            if (!PersistLocked())
            {
                _entries.RemoveAt(_entries.Count - 1);
                Interlocked.Increment(ref _failed);
                return false;
            }
            return true;
        }
    }

    public void BindQuery(string commandId, Guid queryId, string? mode, string? conversationId)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(item => string.Equals(item.CommandId, commandId, StringComparison.Ordinal)
                && item.State == AssistantDeliveryState.Reconciling);
            if (index < 0) return;
            var current = _entries[index];
            var now = _clock();
            _entries[index] = current with { QueryId = queryId, Mode = mode ?? current.Mode, ConversationId = conversationId ?? current.ConversationId, State = AssistantDeliveryState.Pending, UpdatedAt = now, ExpiresAt = now.Add(PendingTtl) };
            PersistLocked();
        }
    }

    public IReadOnlyList<AssistantDeliveryEntry> GetForReconciliation(string? userId)
    {
        lock (_gate)
        {
            PruneLocked();
            return _entries.Where(item => item.State == AssistantDeliveryState.Reconciling
                && string.Equals(item.UserId, userId, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

    public void MarkNotAccepted(string commandId)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(item => string.Equals(item.CommandId, commandId, StringComparison.Ordinal)
                && item.State == AssistantDeliveryState.Reconciling);
            if (index < 0) return;
            var current = _entries[index];
            _entries[index] = current with { State = AssistantDeliveryState.NotAccepted, ExpiresAt = _clock().Add(TombstoneTtl), UpdatedAt = _clock() };
            PersistLocked();
        }
    }

    public IReadOnlyList<AssistantDeliveryEntry> GetForUser(string? userId)
    {
        lock (_gate)
        {
            PruneLocked();
            var now = _clock();
            return _entries.Where(item => string.Equals(item.UserId, userId, StringComparison.OrdinalIgnoreCase)
                    && item.QueryId is not null
                    && (item.State == AssistantDeliveryState.Pending
                        || item.State == AssistantDeliveryState.Accepted && now - item.UpdatedAt >= PlaybackReconcileAfter))
                .ToArray();
        }
    }

    public AssistantDeliveryEntry? Get(Guid queryId)
    {
        lock (_gate)
        {
            PruneLocked();
            return _entries.FirstOrDefault(item => item.QueryId == queryId);
        }
    }

    public bool TryClaim(Guid queryId)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(item => item.QueryId == queryId && item.State == AssistantDeliveryState.Pending);
            if (index < 0)
            {
                Interlocked.Increment(ref _duplicateSuppressed);
                return false;
            }
            var current = _entries[index];
            _entries[index] = current with { State = AssistantDeliveryState.Dispatching, UpdatedAt = _clock() };
            if (!PersistLocked())
            {
                _entries[index] = current;
                Interlocked.Increment(ref _failed);
                return false;
            }
            return true;
        }
    }

    public void ResetToPending(Guid queryId)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(item => item.QueryId == queryId && item.State == AssistantDeliveryState.Dispatching);
            if (index < 0) return;
            var current = _entries[index];
            _entries[index] = current with { State = AssistantDeliveryState.Pending, UpdatedAt = _clock() };
            PersistLocked();
        }
    }

    public void MarkDelivered(Guid queryId)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(item => item.QueryId == queryId && item.State is AssistantDeliveryState.Accepted or AssistantDeliveryState.Dispatching);
            if (index < 0) return;
            var current = _entries[index];
            _entries[index] = current with { State = AssistantDeliveryState.Delivered, ExpiresAt = _clock().Add(TombstoneTtl), UpdatedAt = _clock() };
            PersistLocked();
        }
    }

    public void MarkAccepted(Guid queryId)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(item => item.QueryId == queryId && item.State == AssistantDeliveryState.Dispatching);
            if (index < 0) return;
            var current = _entries[index];
            _entries[index] = current with { State = AssistantDeliveryState.Accepted, UpdatedAt = _clock() };
            PersistLocked();
        }
    }

    public void MarkCancelled(Guid queryId)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(item => item.QueryId == queryId && item.State is AssistantDeliveryState.Accepted or AssistantDeliveryState.Dispatching);
            if (index < 0) return;
            var current = _entries[index];
            _entries[index] = current with { State = AssistantDeliveryState.Cancelled, ExpiresAt = _clock().Add(TombstoneTtl), UpdatedAt = _clock() };
            PersistLocked();
        }
    }

    public void MarkCompletedWithoutSpeech(Guid queryId)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(item => item.QueryId == queryId);
            if (index < 0) return;
            var current = _entries[index];
                _entries[index] = current with { State = AssistantDeliveryState.Delivered, ExpiresAt = _clock().Add(TombstoneTtl), UpdatedAt = _clock() };
            PersistLocked();
        }
    }

    public void MarkAmbiguous(Guid queryId)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(item => item.QueryId == queryId && item.State is AssistantDeliveryState.Dispatching or AssistantDeliveryState.Accepted);
            if (index >= 0)
            {
                var current = _entries[index];
                _entries[index] = current with { State = AssistantDeliveryState.Ambiguous, ExpiresAt = _clock().Add(TombstoneTtl), UpdatedAt = _clock() };
                PersistLocked();
                Interlocked.Increment(ref _ambiguousDispatch);
            }
        }
    }

    public AssistantDeliveryMetrics GetMetrics()
    {
        lock (_gate)
        {
            PruneLocked();
            return new(
                _entries.Count(item => item.State == AssistantDeliveryState.Pending),
                _entries.Count(item => item.State == AssistantDeliveryState.Dispatching),
                _entries.Count(item => item.State == AssistantDeliveryState.Delivered),
                _entries.Count(item => item.State == AssistantDeliveryState.Expired),
                _entries.Count(item => item.State == AssistantDeliveryState.Accepted),
                _entries.Count(item => item.State == AssistantDeliveryState.Cancelled),
                _entries.Count(item => item.State == AssistantDeliveryState.Ambiguous),
                Interlocked.Read(ref _duplicateSuppressed),
                Interlocked.Read(ref _ambiguousDispatch),
                Interlocked.Read(ref _failed),
                _entries.Count(item => item.State == AssistantDeliveryState.Reconciling),
                _entries.Count(item => item.State == AssistantDeliveryState.NotAccepted));
        }
    }

    public void ClearRuntimeEntries()
    {
        lock (_gate)
        {
            var now = _clock();
            // Keep command-only Reconciling entries across a Desktop restart:
            // they are the durable lookup key when the HTTP response was lost.
            _entries = _entries.Select(item => item.State is AssistantDeliveryState.Pending or AssistantDeliveryState.Dispatching or AssistantDeliveryState.Accepted
                    ? item with { State = AssistantDeliveryState.Expired, ExpiresAt = now.Add(TombstoneTtl), UpdatedAt = now }
                    : item).ToList();
            PersistLocked();
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                    _entries = JsonSerializer.Deserialize<List<AssistantDeliveryEntry>>(File.ReadAllText(_path), _json) ?? [];
                // DISPATCHING persisted across process lifetime means Desktop
                // terminated after claiming the result. Replaying it could
                // speak the same answer twice, so recover it as ambiguous.
                var now = _clock();
                for (var index = 0; index < _entries.Count; index++)
                {
                    if (_entries[index].State != AssistantDeliveryState.Dispatching) continue;
                    var entry = _entries[index];
                    _entries[index] = entry with
                    {
                        State = AssistantDeliveryState.Ambiguous,
                        ExpiresAt = now.Add(TombstoneTtl),
                        UpdatedAt = now
                    };
                    Interlocked.Increment(ref _ambiguousDispatch);
                }
                PruneLocked();
                PersistLocked();
            }
            catch
            {
                _entries = [];
            }
        }
    }

    private void PruneLocked()
    {
        var now = _clock();
        var changed = false;
        foreach (var index in Enumerable.Range(0, _entries.Count).Reverse())
        {
            var entry = _entries[index];
            if (entry.State == AssistantDeliveryState.Dispatching && now - entry.UpdatedAt > DispatchAmbiguityTimeout)
            {
                _entries[index] = entry with { State = AssistantDeliveryState.Ambiguous, ExpiresAt = now.Add(TombstoneTtl), UpdatedAt = now };
                Interlocked.Increment(ref _ambiguousDispatch);
                changed = true;
                continue;
            }
            if (entry.State == AssistantDeliveryState.Accepted && now - entry.UpdatedAt > TimeSpan.FromMinutes(2))
            {
                _entries[index] = entry with { State = AssistantDeliveryState.Ambiguous, ExpiresAt = now.Add(TombstoneTtl), UpdatedAt = now };
                changed = true;
                continue;
            }
            if (entry.State == AssistantDeliveryState.Reconciling && now >= entry.ExpiresAt)
            {
                _entries[index] = entry with { State = AssistantDeliveryState.NotAccepted, ExpiresAt = now.Add(TombstoneTtl), UpdatedAt = now };
                changed = true;
                continue;
            }
            if (entry.ExpiresAt > now) continue;
            if (entry.State is AssistantDeliveryState.Pending or AssistantDeliveryState.Dispatching)
            {
                _entries[index] = entry with { State = AssistantDeliveryState.Expired, ExpiresAt = now.Add(TombstoneTtl), UpdatedAt = now };
                changed = true;
            }
            else
            {
                _entries.RemoveAt(index);
                changed = true;
            }
        }
        if (changed) PersistLocked();
    }

    private bool PersistLocked()
    {
        if (_disposed) return false;
        try
        {
            var payload = JsonSerializer.Serialize(_entries, _json);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The queue is bounded, and callers wait for their own snapshot;
            // this keeps claim/add acknowledgements durable while ensuring all
            // filesystem work is serialized by one writer rather than running
            // concurrently on UI/request threads.
            _writes.Writer.WriteAsync(new LedgerWrite(payload, completion)).AsTask().GetAwaiter().GetResult();
            return completion.Task.GetAwaiter().GetResult();
        }
        catch
        {
            // A new/claimed entry is never acknowledged unless its ledger
            // write completed. Best-effort calls (pruning and tombstones)
            // may remain in memory until the next successful write.
            return false;
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var write in _writes.Reader.ReadAllAsync(_writerShutdown.Token))
            {
                var success = false;
                try
                {
                    var temporary = _path + ".part";
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    // Flush the file before the atomic replacement so a
                    // power loss cannot leave a claimed query without a
                    // recoverable ledger snapshot.
                    await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                    {
                        await writer.WriteAsync(write.Payload);
                        await writer.FlushAsync();
                        await stream.FlushAsync(_writerShutdown.Token);
                    }
                    File.Move(temporary, _path, true);
                    success = true;
                }
                catch
                {
                    // The caller receives false and can roll back an
                    // un-durable transition.  The next mutation can retry.
                }
                write.Completion.TrySetResult(success);
            }
        }
        catch (OperationCanceledException) when (_writerShutdown.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writes.Writer.TryComplete();
        }
        try { _writerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _writerShutdown.Cancel();
        _writerShutdown.Dispose();
    }
}
