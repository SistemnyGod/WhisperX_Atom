namespace WhisperX.Atom.Voice;

/// <summary>Small dependency-free smoke test used by CI and the installer doctor.</summary>
public static class VoiceCoreSelfTest
{
    public static void Run()
    {
        var parser = new VoiceIntentParser();
        var productionParser = new VoiceIntentParser(allowLegacyAtom: false);
        Assert(!productionParser.HasWakeWord("Атом, начни запись"), "atom alias is opt-in for production");
        Assert(productionParser.Parse("Атом, начни запись").Intent != VoiceIntent.StartRecording, "disabled atom alias is fail-closed");
        Assert(VoiceIntent.HistoryQuestion == VoiceIntent.AssistantQuery, "legacy question alias");
        Assert(VoiceIntent.AssistantQuery.ToString() == "AssistantQuery", "canonical assistant intent name");
        Assert(parser.HasWakeWord("Атом, начни запись"), "wake word");
        Assert(parser.HasWakeWord("Мифодий, начни запись"), "primary wake word");
        Assert(parser.HasWakeWord("Мефодий, начни запись"), "wake alias");
        Assert(parser.Parse("Мифодий, начни запись").Intent == VoiceIntent.StartRecording, "primary start intent");
        Assert(parser.Parse("Мифодий, запусти запись").Intent == VoiceIntent.StartRecording, "start alias intent");
        Assert(parser.Parse("Мифодий, запусти запись после звонка").Intent == VoiceIntent.AssistantQuery, "start alias with trailing text is conversational");
        Assert(parser.Parse("Мифодий, запустить запись").Intent == VoiceIntent.AssistantQuery, "infinitive start is conversational");
        Assert(parser.Parse("Атом, начни запись").Intent == VoiceIntent.StartRecording, "start intent");
        Assert(parser.Parse("Атом, зафиксируй решение по поставкам").Intent == VoiceIntent.MarkDecision, "decision intent");
        Assert(parser.Parse("Атом, зафиксируй решение по поставкам").Parameter == "по поставкам", "decision parameter");
        Assert(parser.Parse("Атом, кто отвечает за ремонт?").Intent == VoiceIntent.AssistantQuery, "question intent");
        Assert(parser.Parse("Мифодий, покажи решения по ремонту").Intent == VoiceIntent.AssistantQuery, "spoken meeting question without punctuation");
        Assert(parser.Parse("Мефодий, расскажи что решили").Intent == VoiceIntent.AssistantQuery, "spoken meeting question alias");
        Assert(parser.Parse("Мифодий, скажи привет").Intent == VoiceIntent.AssistantQuery, "general greeting");
        Assert(parser.Parse("Мифодий, пока").Intent == VoiceIntent.Farewell, "farewell intent");
        Assert(parser.Parse("МЕФОДИЙ, до свидания!").Intent == VoiceIntent.Farewell, "farewell normalization");
        Assert(parser.Parse("Мифодий, до встречи").Intent == VoiceIntent.Farewell, "farewell alias");
        Assert(parser.Parse("Атом, до свидания").Intent == VoiceIntent.Farewell, "farewell wake alias");
        Assert(parser.Parse("Мифодий, пока идёт запись?").Intent == VoiceIntent.AssistantQuery, "farewell word in question remains conversational");
        Assert(parser.Parse("Мифодий, пока не начинай запись").Intent == VoiceIntent.AssistantQuery, "farewell word in imperative sentence remains conversational");
        Assert(parser.Parse("Мифодий, повтори").Intent == VoiceIntent.RepeatAnswer, "repeat follow-up intent");
        Assert(parser.Parse("Мифодий, повтори последний ответ").Intent == VoiceIntent.RepeatAnswer, "repeat answer alias");
        Assert(parser.Parse("Мифодий, короче").Intent == VoiceIntent.ShortenAnswer, "shorten follow-up intent");
        Assert(parser.Parse("Мифодий, подробнее").Intent == VoiceIntent.ElaborateAnswer, "elaborate follow-up intent");
        Assert(parser.Parse("Мифодий, вернись к предыдущему вопросу").Intent == VoiceIntent.PreviousQuestion, "previous question intent");
        Assert(parser.Parse("Мифодий, почему сделать короче ответ?").Intent == VoiceIntent.AssistantQuery, "contextual shorten remains conversational");
        Assert(parser.Parse("Мифодий, подробнее по ремонту").Intent == VoiceIntent.AssistantQuery, "contextual elaborate remains conversational");
        Assert(parser.Parse("Мифодий, повтори ответ по ремонту").Intent == VoiceIntent.AssistantQuery, "contextual repeat remains conversational");
        Assert(parser.Parse("Мифодий, как дела").Intent == VoiceIntent.AssistantQuery, "general conversation");
        Assert(parser.Parse("Мифодий, пошути").Intent == VoiceIntent.AssistantQuery, "general joke request");
        Assert(parser.Parse("Мифодий, а подробнее?").Intent == VoiceIntent.AssistantQuery, "follow-up question");
        Assert(parser.Parse("Мифодий, почему?").Intent == VoiceIntent.AssistantQuery, "short follow-up question");
        Assert(parser.Parse("Мифодий, сколько киловатт в мегаватте?").Intent == VoiceIntent.AssistantQuery, "general knowledge question");
        Assert(parser.Parse("Мифодий, что решили по ремонту?").Intent == VoiceIntent.AssistantQuery, "meeting question");
        Assert(parser.Parse("Мифодий, почему?", confidence: 0.20).Intent == VoiceIntent.Unknown, "low confidence is unknown");
        Assert(parser.Parse("Мифодий, ???").Intent == VoiceIntent.Unknown, "unrecognizable utterance is unknown");
        Assert(parser.Parse("Мифодий, почему остановилась запись?").Intent == VoiceIntent.AssistantQuery, "why recording stopped is a question");
        Assert(parser.Parse("Мифодий, может нам остановить запись?").Intent == VoiceIntent.AssistantQuery, "infinitive suggestion is a question");
        Assert(parser.Parse("Мифодий, может нам запустить запись?").Intent == VoiceIntent.AssistantQuery, "start infinitive suggestion is a question");
        Assert(parser.Parse("Мифодий, остановить запись").Intent == VoiceIntent.AssistantQuery, "infinitive is not a command");
        Assert(parser.Parse("Мифодий, начать запись").Intent == VoiceIntent.AssistantQuery, "start infinitive is not a command");
        Assert(parser.Parse("Мифодий, остановись").Intent == VoiceIntent.StopSpeaking, "stop speech intent");
        Assert(parser.Parse("Мифодий, запись идёт?").Intent == VoiceIntent.GetStatus, "recording status fast path");
        Assert(parser.Parse("Мифодий, сколько идет запись").Intent == VoiceIntent.GetStatus, "recording duration fast path");
        Assert(parser.Parse("Мифодий, состояние сервера").Intent == VoiceIntent.GetServerStatus, "server status fast path");
        Assert(parser.Parse("Мифодий, состояние обработки").Intent == VoiceIntent.GetPipelineStatus, "pipeline status fast path");
        Assert(parser.Parse("Мифодий, стенограмма готова").Intent == VoiceIntent.GetPipelineStatus, "transcript status fast path");
        Assert(parser.Parse("Мифодий, свободное место").Intent == VoiceIntent.GetStorageStatus, "storage status fast path");
        Assert(parser.Parse("Мифодий, сколько осталось места").Intent == VoiceIntent.GetStorageStatus, "storage remaining fast path");

        var commands = new (string Text, VoiceIntent Intent)[]
        {
            ("\u0410\u0442\u043e\u043c, \u043d\u0430\u0447\u043d\u0438 \u0437\u0430\u043f\u0438\u0441\u044c", VoiceIntent.StartRecording),
            ("\u0410\u0442\u043e\u043c, \u043f\u043e\u0441\u0442\u0430\u0432\u044c \u043d\u0430 \u043f\u0430\u0443\u0437\u0443", VoiceIntent.PauseRecording),
            ("\u0410\u0442\u043e\u043c, \u043f\u0440\u043e\u0434\u043e\u043b\u0436\u0438 \u0437\u0430\u043f\u0438\u0441\u044c", VoiceIntent.ResumeRecording),
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
        Assert(parser.Parse("\u0410\u0442\u043e\u043c, \u0432\u043a\u043b\u044e\u0447\u0438 \u0441\u0432\u0435\u0442").Intent == VoiceIntent.AssistantQuery, "free utterance routes to assistant");
        Assert(parser.Parse("\u0410\u0442\u043e\u043c, \u0432\u043a\u043b\u044e\u0447\u0438 \u0441\u0432\u0435\u0442").Intent is not VoiceIntent.StartRecording
            and not VoiceIntent.StopRecording
            and not VoiceIntent.PauseRecording
            and not VoiceIntent.ResumeRecording, "free utterance cannot mutate recorder");

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

        var barge = new VoiceStateMachine();
        barge.Enable(true);
        Assert(barge.TryWake(), "barge wake setup");
        Assert(barge.BeginCapture(), "barge capture setup");
        Assert(barge.TryRespond("Ответ", VoiceHostState.Listening), "barge response setup");
        Assert(barge.TryBeginBargeIn(), "barge-in transition");
        Assert(barge.Snapshot.State == VoiceHostState.Capturing, "barge returns to capture");

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
