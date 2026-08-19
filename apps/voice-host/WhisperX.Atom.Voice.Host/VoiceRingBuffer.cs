namespace WhisperX.Atom.Voice.Host;

public sealed class VoiceRingBuffer(int capacityBytes)
{
    private readonly object _gate = new();
    private readonly byte[] _buffer = new byte[Math.Max(1, capacityBytes)];
    private int _write;
    private int _count;

    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            foreach (var value in data)
            {
                _buffer[_write] = value;
                _write = (_write + 1) % _buffer.Length;
                _count = Math.Min(_count + 1, _buffer.Length);
            }
        }
    }

    public byte[] Snapshot()
    {
        lock (_gate)
        {
            var result = new byte[_count];
            var start = (_write - _count + _buffer.Length) % _buffer.Length;
            for (var index = 0; index < result.Length; index++) result[index] = _buffer[(start + index) % _buffer.Length];
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _write = 0;
            _count = 0;
        }
    }
}
