namespace WhisperX.Atom.Recorder;

/// <summary>
/// Pure, session-scoped validation at the capture → durable-writer boundary.
/// A discontinuity must stop the session explicitly; silently concatenating
/// frames would create a locally valid-looking PCM file with missing or
/// duplicated speech.
/// </summary>
public sealed class AudioFrameContinuityValidator
{
    private long _expectedStartSample;
    private AudioStreamFormat? _format;

    public AudioFrameContinuityValidator(long expectedStartSample = 0)
    {
        if (expectedStartSample < 0) throw new ArgumentOutOfRangeException(nameof(expectedStartSample));
        _expectedStartSample = expectedStartSample;
    }

    public long ExpectedStartSample => _expectedStartSample;

    public void ValidateAndAdvance(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.StartSample < 0 || frame.SampleCount <= 0)
            throw new InvalidDataException("AUDIO_FRAME_INVALID");
        if (frame.StartSample > _expectedStartSample)
            throw new InvalidDataException("AUDIO_FRAME_GAP");
        if (frame.StartSample < _expectedStartSample)
            throw new InvalidDataException("AUDIO_FRAME_OVERLAP");

        var format = frame.Format ?? AudioStreamFormats.Phase1Microphone;
        if (format.SampleRate <= 0 || format.Channels <= 0 || format.BitsPerSample <= 0
            || format.BitsPerSample % 8 != 0 || format.BytesPerFrame <= 0)
            throw new InvalidDataException("AUDIO_FRAME_FORMAT_INVALID");
        if (_format is not null && _format != format)
            throw new InvalidDataException("AUDIO_FRAME_FORMAT_MISMATCH");

        int expectedBytes;
        long nextStartSample;
        try
        {
            expectedBytes = checked(frame.SampleCount * format.BytesPerFrame);
            nextStartSample = checked(frame.StartSample + frame.SampleCount);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("AUDIO_FRAME_RANGE_OVERFLOW", ex);
        }

        if (frame.Pcm16Length != expectedBytes
            || frame.Pcm16Length > frame.Pcm16Bytes.Length
            || frame.Pcm16Memory.Length != expectedBytes)
            throw new InvalidDataException("AUDIO_FRAME_SIZE_MISMATCH");

        _format ??= format;
        _expectedStartSample = nextStartSample;
    }
}
