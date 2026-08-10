using NAudio.Wave;

namespace WhisperX.Atom.Voice.Host;

internal static class VoiceHostSelfTest
{
    public static void Run()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var converter = new AudioPcmConverter();
        var detector = new VoiceActivityDetector();
        var silence = new byte[480 * format.BlockAlign];
        var silencePcm = converter.Convert(silence, silence.Length, format);
        Assert(silencePcm.Length > 0 && silencePcm.Length % 2 == 0, "silence conversion");

        var tone = new byte[480 * format.BlockAlign];
        for (var frame = 0; frame < 480; frame++)
        {
            var value = (float)(Math.Sin(frame * 2 * Math.PI * 440 / 48000) * 0.25);
            for (var channel = 0; channel < 2; channel++)
                BitConverter.GetBytes(value).CopyTo(tone, frame * format.BlockAlign + channel * 4);
        }
        var tonePcm = converter.Convert(tone, tone.Length, format);
        Assert(tonePcm.Length > 0 && tonePcm.Length % 2 == 0, "tone conversion");
        Assert(!detector.IsSpeech(silencePcm, "balanced"), "silence VAD");
        Assert(detector.IsSpeech(tonePcm, "balanced"), "tone VAD");
        Assert(VoiceHostRuntime.WakePhrases.All(value => value == "атом" || value.StartsWith("атом ", StringComparison.Ordinal) || value == "[unk]"), "wake grammar requires atom");
        Assert(VoiceHostRuntime.WakePhrases.Contains("[unk]"), "wake grammar unknown token");
        Assert(VoiceHostRuntime.CommandPhrases.Contains("[unk]"), "command grammar unknown token");
        var missingConfidence = VoskRecognizer.ParseResult("{\"text\":\"атом запись\"}", true);
        Assert(missingConfidence.Confidence == 0, "missing Vosk confidence is rejected");
        using var firstMutex = new Mutex(true, "Local\\WhisperXAtomVoiceHostSelfTest-" + Environment.ProcessId, out var ownsFirst);
        using var secondMutex = new Mutex(true, "Local\\WhisperXAtomVoiceHostSelfTest-" + Environment.ProcessId, out var ownsSecond);
        Assert(ownsFirst && !ownsSecond, "single-instance mutex");
        using var quietResponder = new SpeechResponder { QuietMode = true };
        Assert(!quietResponder.TryEnqueue("Запись начата") && !quietResponder.IsBusy, "quiet mode does not stick busy");
        Console.WriteLine("Voice host audio self-test passed.");
    }

    private static void Assert(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("Voice host self-test failed: " + name);
    }
}
