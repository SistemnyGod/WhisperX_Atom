namespace WhisperX.Atom.Voice.Host;

internal sealed class PcmFrameAssembler(int frameBytes = 640)
{
    private readonly byte[] _frame = new byte[frameBytes];
    private int _offset;

    public async Task PushAsync(ReadOnlyMemory<byte> pcm, Func<byte[], Task> consumer)
    {
        var remaining = pcm;
        while (!remaining.IsEmpty)
        {
            var copy = Math.Min(_frame.Length - _offset, remaining.Length);
            remaining.Span[..copy].CopyTo(_frame.AsSpan(_offset));
            _offset += copy;
            remaining = remaining[copy..];
            if (_offset != _frame.Length) continue;
            await consumer(_frame);
            _offset = 0;
        }
    }

    public void Reset() => _offset = 0;
}