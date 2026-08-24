using WhisperX.Atom.Voice;
using System.Text.Json;
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

    [Fact]
    public void RefinerModesAreAppendOnlyAndComparisonDoesNotApplyConfidenceGate()
    {
        Assert.Equal(0, (int)VoiceRefinerMode.Off);
        Assert.Equal(1, (int)VoiceRefinerMode.Shadow);
        Assert.Equal(2, (int)VoiceRefinerMode.AssistantOnly);
        Assert.Equal(3, (int)VoiceRefinerMode.WakeAudit);

        var parser = new VoiceIntentParser();
        Assert.Equal(VoiceIntent.StopRecording, parser.ParseIntentForComparison("Мифодий останови запись"));
        Assert.Equal(VoiceIntent.AssistantQuery, parser.ParseIntentForComparison("Мифодий почему остановилась запись"));
    }

    [Fact]
    public void RefinerPrivacyFieldsAreNotSerialized()
    {
        var envelope = new VoiceUtteranceEnvelope(
            "utterance", DateTimeOffset.UtcNow, null, null, null,
            new byte[1600], "секретный вопрос", VoiceIntent.AssistantQuery, 0.9, "ASSISTANT_QUERY");
        var result = new VoiceRefinementResult(VoiceRefinementState.Ready, "секретный shadow текст", Confidence: null);
        var envelopeJson = JsonSerializer.Serialize(envelope);
        var resultJson = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("секретный", envelopeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("секретный", resultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Pcm16kMono", envelopeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Text", resultJson, StringComparison.Ordinal);
        Assert.Null(result.Confidence);
    }

    [Fact]
    public void ResponseCarriesCorrelationAndSampleRateMetadata()
    {
        var response = new VoiceRefinerResponse(true, "request", "READY", "READY", "ignored", null,
            12, 1, null, "1.0.1+abc", "ggml-small.bin", "whisper.cpp-native", true, 2, 1,
            "utterance", 4, 16_000);

        Assert.Equal("request", response.RequestId);
        Assert.Equal("1.0.1+abc", response.BuildIdentity);
        Assert.Equal(1, response.NativeAbiVersion);
        Assert.Equal(16_000, response.SampleRate);
        Assert.Equal(4, response.Sequence);
    }
}
