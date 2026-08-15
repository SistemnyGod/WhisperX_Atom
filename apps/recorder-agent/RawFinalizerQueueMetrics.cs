namespace WhisperX.Atom.Recorder;

/// <summary>
/// Process-local gauges for the bounded raw-finalizer queue.  The queue itself
/// remains owned by each active session writer; these atomics let HEALTH expose
/// the worst observed depth without touching the realtime capture path.
/// </summary>
public sealed class RawFinalizerQueueMetrics
{
    private int _depth;
    private int _maximumDepth;
    private int _capacity;

    public RawFinalizerQueueMetrics(int capacity = 4) => _capacity = Math.Clamp(capacity, 2, 32);

    public int Depth => Volatile.Read(ref _depth);
    public int MaximumDepth => Volatile.Read(ref _maximumDepth);
    public int Capacity => Volatile.Read(ref _capacity);

    public void Configure(int capacity) => Volatile.Write(ref _capacity, Math.Clamp(capacity, 2, 32));

    public bool TryEnqueue()
    {
        var depth = Interlocked.Increment(ref _depth);
        while (true)
        {
            var currentMaximum = Volatile.Read(ref _maximumDepth);
            if (depth <= currentMaximum || Interlocked.CompareExchange(ref _maximumDepth, depth, currentMaximum) == currentMaximum)
                return true;
        }
    }

    public void Dequeue()
    {
        while (true)
        {
            var current = Volatile.Read(ref _depth);
            if (current <= 0 || Interlocked.CompareExchange(ref _depth, current - 1, current) == current)
                return;
        }
    }
}
