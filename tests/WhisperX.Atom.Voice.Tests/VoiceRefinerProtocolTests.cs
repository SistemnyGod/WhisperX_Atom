using WhisperX.Atom.Voice;
using Xunit;

public sealed class VoiceRefinerProtocolTests
{
    [Fact]
    public void ShadowProtocolIsBoundedAndCurrentUserScoped()
    {
        Assert.Equal(2, VoiceRefinerProtocol.QueueCapacity);
        Assert.Equal(960_000, VoiceRefinerProtocol.MaxPcmBytes);
        Assert.Equal(1, VoiceRefinerProtocol.NativeAbiVersion);
        Assert.Equal(15_000, VoiceRefinerProtocol.HostInferenceTimeoutMs);
        Assert.Equal(18_000, VoiceRefinerProtocol.ClientTimeoutMs);
        Assert.Equal(73, VoiceRefinerProtocol.InferenceTimeoutExitCode);
        Assert.StartsWith(VoiceRefinerProtocol.PipePrefix, VoiceRefinerProtocol.PipeName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", VoiceRefinerProtocol.PipeName);
    }

    [Fact]
    public void EnvelopeCarriesSequenceWithoutChangingLegacyArguments()
    {
        var envelope = new VoiceUtteranceEnvelope(
            "utterance", DateTimeOffset.UtcNow, null, null, null,
            new byte[1600], "Мифодий начни запись", VoiceIntent.StartRecording, 0.8,
            "LOCAL_COMMAND", Sequence: 7, DurationMs: 50, QueueWaitMs: 2);

        Assert.Equal(7, envelope.Sequence);
        Assert.Equal(50, envelope.DurationMs);
        Assert.Equal(2, envelope.QueueWaitMs);
    }

    [Fact]
    public void CaptureMetadataIsOptionalAndKeepsLegacyEnvelopeShape()
    {
        var envelope = new VoiceUtteranceEnvelope(
            "utterance", DateTimeOffset.UtcNow, null, null, null,
            new byte[1600], "Мифодий останови запись", VoiceIntent.StopRecording, 0.8,
            "LOCAL_COMMAND", Sequence: 8, DurationMs: 600,
            PreRollMs: 2000, SpeechMs: 600, PostRollMs: 400, Truncated: false);

        Assert.Equal(2000, envelope.PreRollMs);
        Assert.Equal(600, envelope.SpeechMs);
        Assert.Equal(400, envelope.PostRollMs);
        Assert.False(envelope.Truncated);
    }
}
