using WhisperX.Atom.Voice;
using Xunit;

public sealed class VoiceRefinerProtocolTests
{
    [Fact]
    public void ShadowProtocolIsBoundedAndCurrentUserScoped()
    {
        Assert.Equal(2, VoiceRefinerProtocol.QueueCapacity);
        Assert.Equal(960_000, VoiceRefinerProtocol.MaxPcmBytes);
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
}
