using WhisperX.Atom.Voice;
using Xunit;

public sealed class VoiceBehaviorTests
{
    [Theory]
    [InlineData("Мифодий, начни запись", VoiceIntent.StartRecording)]
    [InlineData("Мифодий, запусти запись", VoiceIntent.StartRecording)]
    [InlineData("Мифодий, останови запись", VoiceIntent.StopRecording)]
    [InlineData("Мифодий, останови запись останови запись", VoiceIntent.StopRecording)]
    [InlineData("Мифодий, останови запись мефодий", VoiceIntent.StopRecording)]
    [InlineData("Мифодий, пока", VoiceIntent.Farewell)]
    [InlineData("Мифодий, кто отвечает за ремонт?", VoiceIntent.AssistantQuery)]
    public void NormalVoiceUtterancesHaveStableIntent(string text, VoiceIntent expected)
    {
        var command = new VoiceIntentParser(allowLegacyAtom: false).Parse(text, confidence: 0.95);

        Assert.Equal(expected, command.Intent);
        Assert.Equal(text, command.Text);
    }

    [Theory]
    [InlineData("Мифодий, запусти запись после звонка")]
    [InlineData("Мифодий, запустить запись")]
    [InlineData("Мифодий, останови запись?")]
    [InlineData("Мифодий, пока идёт запись?")]
    public void ConversationalPhrasesCannotMutateRecorder(string text)
    {
        var intent = new VoiceIntentParser(allowLegacyAtom: false).Parse(text, confidence: 0.95).Intent;

        Assert.Equal(VoiceIntent.AssistantQuery, intent);
        Assert.DoesNotContain(intent, new[]
        {
            VoiceIntent.StartRecording, VoiceIntent.StopRecording,
            VoiceIntent.PauseRecording, VoiceIntent.ResumeRecording
        });
    }

    [Theory]
    [InlineData("Мифодий, останови запись входе остановит запись")]
    [InlineData("Мифодий, останови запись единственными запись")]
    [InlineData("Мифодий, запусти запись после звонка")]
    public void MalformedRecorderImperativesAreMarkedForLocalRecovery(string text)
    {
        var parser = new VoiceIntentParser(allowLegacyAtom: false);

        Assert.True(parser.IsRecorderImperative(text));
        Assert.False(parser.IsSafeRecorderCommand(text));
        Assert.Equal(VoiceIntent.AssistantQuery, parser.Parse(text, confidence: 0.95).Intent);
    }

    [Theory]
    [InlineData("Мифодий, останови запись останови запись")]
    [InlineData("Мифодий, останови запись мефодий")]
    public void BoundedNormalizationAcceptsOnlySafeCommandRepairs(string text)
    {
        var parser = new VoiceIntentParser(allowLegacyAtom: false);

        Assert.True(parser.IsSafeRecorderCommand(text));
    }

    [Theory]
    [InlineData("останови запись", "останови запись", "останови запись")]
    [InlineData("останови запись", "запись останови", "останови запись останови")]
    [InlineData("останови", "запись", "останови запись")]
    public void FinalSegmentsMergeOnlyByTokenOverlap(string first, string second, string expected)
    {
        Assert.Equal(expected, VoiceCommandText.MergeFinalSegments(first, second));
    }

    [Fact]
    public void LowConfidenceRecognitionDoesNotProduceACommand()
    {
        var command = new VoiceIntentParser().Parse("Мифодий, начни запись", confidence: 0.20);

        Assert.Equal(VoiceIntent.Unknown, command.Intent);
    }

    [Fact]
    public void ResponseCompletesOnceAndReturnsToListening()
    {
        var machine = new VoiceStateMachine();
        machine.Enable(true);
        Assert.True(machine.TryWake());
        Assert.True(machine.BeginCapture());
        Assert.True(machine.BeginRecognition("Мифодий, пока"));
        Assert.True(machine.TryExecute(new VoiceCommand(VoiceIntent.Farewell, "Мифодий, пока")));
        Assert.True(machine.TryRespond("Пока!", VoiceHostState.Listening));

        machine.FinishResponse();
        Assert.Equal(VoiceHostState.Cooldown, machine.Snapshot.State);
        machine.FinishCooldown();

        Assert.Equal(VoiceHostState.Listening, machine.Snapshot.State);
        Assert.Single(machine.History, item => item.To == VoiceHostState.Responding);
    }

    [Fact]
    public void WakeBargeInMovesOnlyAnActiveResponseBackToCapture()
    {
        var machine = new VoiceStateMachine();
        machine.Enable(true);
        Assert.True(machine.TryRespond("Ответ", VoiceHostState.Listening));
        Assert.True(machine.TryBeginBargeIn());
        Assert.Equal(VoiceHostState.Capturing, machine.Snapshot.State);
        Assert.False(machine.TryBeginBargeIn());
    }
}
