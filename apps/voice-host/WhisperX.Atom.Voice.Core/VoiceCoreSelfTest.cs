namespace WhisperX.Atom.Voice;

/// <summary>Small dependency-free smoke test used by CI and the installer doctor.</summary>
public static class VoiceCoreSelfTest
{
    public static void Run()
    {
        var parser = new VoiceIntentParser();
        Assert(parser.HasWakeWord("Атом, начни запись"), "wake word");
        Assert(parser.HasWakeWord("Мифодий, начни запись"), "primary wake word");
        Assert(parser.HasWakeWord("Мефодий, начни запись"), "wake alias");
        Assert(parser.Parse("Мифодий, начни запись").Intent == VoiceIntent.StartRecording, "primary start intent");
        Assert(parser.Parse("Атом, начни запись").Intent == VoiceIntent.StartRecording, "start intent");
        Assert(parser.Parse("Атом, зафиксируй решение по поставкам").Intent == VoiceIntent.MarkDecision, "decision intent");
        Assert(parser.Parse("Атом, зафиксируй решение по поставкам").Parameter == "по поставкам", "decision parameter");
        Assert(parser.Parse("Атом, кто отвечает за ремонт?").Intent == VoiceIntent.HistoryQuestion, "question intent");
        Assert(parser.Parse("Мифодий, покажи решения по ремонту").Intent == VoiceIntent.HistoryQuestion, "spoken meeting question without punctuation");
        Assert(parser.Parse("Мефодий, расскажи что решили").Intent == VoiceIntent.HistoryQuestion, "spoken meeting question alias");

        var commands = new (string Text, VoiceIntent Intent)[]
        {
            ("\u0410\u0442\u043e\u043c, \u0437\u0430\u043f\u0438\u0441\u044c", VoiceIntent.StartRecording),
            ("\u0410\u0442\u043e\u043c, \u043f\u0430\u0443\u0437\u0430", VoiceIntent.PauseRecording),
            ("\u0410\u0442\u043e\u043c, \u043f\u0440\u043e\u0434\u043e\u043b\u0436\u0438", VoiceIntent.ResumeRecording),
            ("\u0410\u0442\u043e\u043c, \u043f\u043e\u0441\u0442\u0430\u0432\u044c \u043c\u0435\u0442\u043a\u0443", VoiceIntent.AddMarker),
            ("\u0410\u0442\u043e\u043c, \u043e\u0442\u043c\u0435\u0442\u044c \u0440\u0435\u0448\u0435\u043d\u0438\u0435", VoiceIntent.MarkDecision),
            ("\u0410\u0442\u043e\u043c, \u043e\u0442\u043c\u0435\u0442\u044c \u043f\u043e\u0440\u0443\u0447\u0435\u043d\u0438\u0435", VoiceIntent.MarkActionItem),
            ("\u0410\u0442\u043e\u043c, \u0441\u0442\u0430\u0442\u0443\u0441", VoiceIntent.GetStatus),
            ("\u0410\u0442\u043e\u043c, \u0437\u0430\u0432\u0435\u0440\u0448\u0438 \u0437\u0430\u043f\u0438\u0441\u044c", VoiceIntent.StopRecording),
            ("\u0410\u0442\u043e\u043c, \u043f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0430\u044e", VoiceIntent.Confirm),
            ("\u0410\u0442\u043e\u043c, \u043e\u0442\u043c\u0435\u043d\u0430", VoiceIntent.Cancel)
        };
        foreach (var item in commands)
            Assert(parser.Parse(item.Text).Intent == item.Intent, "command: " + item.Text);
        Assert(parser.Parse("\u0410\u0442\u043e\u043c, \u0432\u043a\u043b\u044e\u0447\u0438 \u0441\u0432\u0435\u0442").Intent == VoiceIntent.Unknown, "unknown command");

        var machine = new VoiceStateMachine();
        machine.BeginStartup();
        Assert(machine.Snapshot.State == VoiceHostState.Starting, "startup transition");
        machine.MarkReady();
        machine.Enable(true);
        Assert(machine.TryWake(), "wake transition");
        Assert(machine.BeginCapture(), "capture transition");
        Assert(machine.BeginRecognition("Атом, останови запись"), "recognition transition");
        Assert(machine.RequestConfirmation(new VoiceCommand(VoiceIntent.StopRecording, "Атом, останови запись")), "confirmation transition");
        Assert(machine.TryRespond("Подтвердите остановку", VoiceHostState.Confirming), "confirmation response transition");
        machine.FinishResponse();
        Assert(machine.Snapshot.State == VoiceHostState.Confirming, "confirmation survives response");
        Assert(machine.CancelConfirmation(), "cancel transition");
        Assert(machine.Snapshot.State == VoiceHostState.Listening, "cancel returns to listening");

        var twoPhase = new VoiceStateMachine();
        twoPhase.Enable(true);
        Assert(twoPhase.TryWake(), "two-phase wake transition");
        Assert(twoPhase.BeginCapture(), "two-phase capture transition");
        Assert(twoPhase.TryRespond("\u0421\u043b\u0443\u0448\u0430\u044e", VoiceHostState.Capturing), "two-phase response transition");
        twoPhase.FinishResponse();
        Assert(twoPhase.Snapshot.State == VoiceHostState.Capturing, "two-phase response returns to capture");

        var degraded = new VoiceStateMachine();
        degraded.Enable(true);
        degraded.SetDegraded("VOICE_MODEL_MISSING");
        Assert(degraded.Snapshot.State == VoiceHostState.Degraded, "degraded state");
        Assert(!degraded.TryWake(), "degraded blocks wake");
        degraded.Enable(true);
        Assert(degraded.Snapshot.State == VoiceHostState.Listening, "recovery from degraded");

        Console.WriteLine("Voice core self-test passed.");
    }

    private static void Assert(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("Voice core self-test failed: " + name);
    }
}
