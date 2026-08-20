namespace WhisperX.Atom.Voice.Host.Tts;

public sealed record TtsOptions(
    string Voice = "aidar",
    int SampleRate = 48000,
    int Rate = 0,
    int Volume = 90,
    int CpuThreads = 4,
    bool CacheSafePhrase = true)
{
    public int NormalizedSampleRate => SampleRate is 24000 or 48000 ? SampleRate : 48000;
    public int NormalizedVolume => Math.Clamp(Volume, 0, 100);
    public int NormalizedRate => Math.Clamp(Rate, -10, 10);
    public int NormalizedCpuThreads => Math.Clamp(CpuThreads, 1, 32);
}
