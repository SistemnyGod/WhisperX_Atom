using NAudio.Wave;

namespace WhisperX.Atom.Recorder;

public enum RawAudioSampleFormat
{
    Pcm16,
    Pcm24,
    Pcm32,
    Float32
}

public sealed record AudioSampleFormatDescriptor(
    RawAudioSampleFormat Kind,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    int ValidBitsPerSample,
    string SourceEncoding,
    string? SourceSubFormat)
{
    public string CanonicalEncoding => Kind switch
    {
        RawAudioSampleFormat.Pcm16 => "PCM_S16LE",
        RawAudioSampleFormat.Pcm24 => "PCM_S24LE",
        RawAudioSampleFormat.Pcm32 => "PCM_S32LE",
        RawAudioSampleFormat.Float32 => "FLOAT32",
        _ => throw new ArgumentOutOfRangeException()
    };

    public string FfmpegInput => Kind switch
    {
        RawAudioSampleFormat.Pcm16 => "s16le",
        RawAudioSampleFormat.Pcm24 => "s24le",
        RawAudioSampleFormat.Pcm32 => "s32le",
        RawAudioSampleFormat.Float32 => "f32le",
        _ => throw new ArgumentOutOfRangeException()
    };
}

public static class AudioSampleFormatResolver
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public static AudioSampleFormatDescriptor Resolve(WaveFormat format)
    {
        var sourceEncoding = format.Encoding.ToString();
        string? sourceSubFormat = null;
        var encoding = format.Encoding;
        var validBits = format.BitsPerSample;
        if (format is WaveFormatExtensible extensible)
        {
            sourceSubFormat = extensible.SubFormat.ToString("D");
            // NAudio 2.2.1 does not expose the extensible valid-bits field;
            // retain the container width and leave the optional DB field
            // nullable when the driver does not expose it.
            validBits = format.BitsPerSample;
            if (extensible.SubFormat == IeeeFloatSubFormat) encoding = WaveFormatEncoding.IeeeFloat;
            else if (extensible.SubFormat == PcmSubFormat) encoding = WaveFormatEncoding.Pcm;
            else throw new NotSupportedException($"unsupported_audio_subformat:{sourceSubFormat}");
        }

        if (encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return new AudioSampleFormatDescriptor(RawAudioSampleFormat.Float32, format.SampleRate, format.Channels, 32, validBits, sourceEncoding, sourceSubFormat);
        if (encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32)
            return new AudioSampleFormatDescriptor(format.BitsPerSample switch
            {
                16 => RawAudioSampleFormat.Pcm16,
                24 => RawAudioSampleFormat.Pcm24,
                _ => RawAudioSampleFormat.Pcm32
            }, format.SampleRate, format.Channels, format.BitsPerSample, validBits, sourceEncoding, sourceSubFormat);

        throw new NotSupportedException($"unsupported_audio_format:{sourceEncoding}/{format.BitsPerSample}");
    }

    public static AudioSampleFormatDescriptor FromStored(string encoding, int bitsPerSample, int sampleRate, int channels, string? sourceSubFormat = null, int? validBitsPerSample = null)
    {
        var normalized = encoding.Trim().ToUpperInvariant();
        var extensibleFloat = bitsPerSample == 32
            && Guid.TryParse(sourceSubFormat, out var subFormat)
            && subFormat == IeeeFloatSubFormat;
        var kind = extensibleFloat ? RawAudioSampleFormat.Float32 : normalized switch
        {
            "FLOAT32" or "IEEEFLOAT" or "IEEE_FLOAT" => RawAudioSampleFormat.Float32,
            "PCM_S16LE" or "PCM" when bitsPerSample == 16 => RawAudioSampleFormat.Pcm16,
            "PCM_S24LE" or "PCM" when bitsPerSample == 24 => RawAudioSampleFormat.Pcm24,
            "PCM_S32LE" or "PCM" or "EXTENSIBLE" when bitsPerSample == 32 => RawAudioSampleFormat.Pcm32,
            _ => throw new NotSupportedException($"unsupported_audio_encoding:{encoding}/{bitsPerSample}")
        };
        return new AudioSampleFormatDescriptor(kind, sampleRate, channels, bitsPerSample, validBitsPerSample ?? bitsPerSample, encoding, sourceSubFormat);
    }
}
