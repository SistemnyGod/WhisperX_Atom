from pathlib import Path

from workers.ml_worker.technical_events import build_technical_intervals, segment_technical_flags


ROOT = Path(__file__).resolve().parents[1]
VOICE_RUNTIME = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs").read_text(encoding="utf-8")
VOICE_RECOGNIZERS = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceRecognizers.cs").read_text(encoding="utf-8")
VOICE_PARSER = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceIntentParser.cs").read_text(encoding="utf-8")
SPEECH_RESPONDER = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/SpeechResponder.cs").read_text(encoding="utf-8")


def test_silero_runtime_resolves_installed_sibling_tts_host_layout():
    assert "ResolveTtsHostRoot(AppContext.BaseDirectory)" in SPEECH_RESPONDER
    resolver = SPEECH_RESPONDER.split("internal static string ResolveTtsHostRoot", 1)[1]
    assert 'Path.Combine(voiceHostBaseDirectory, "TtsHost")' in resolver
    assert 'Path.Combine(parent, "TtsHost")' in resolver
    assert "File.Exists(Path.Combine(sibling, executableName))" in resolver
VOICE_CAPTURE = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceAudioCapture.cs").read_text(encoding="utf-8")
VOICE_IPC = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostIpc.cs").read_text(encoding="utf-8")
VOICE_TELEMETRY = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceTelemetryPipeServer.cs").read_text(encoding="utf-8")
VOICE_CONTROLLER = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/VoiceHostController.cs").read_text(encoding="utf-8")
VOICE_LEASE = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntimeLease.cs").read_text(encoding="utf-8")
RECORDER_HOST_RUNTIME = (ROOT / "apps/recorder-host/RecorderHostRuntime.cs").read_text(encoding="utf-8")
LEGACY_AGENT_PIPE = (ROOT / "apps/recorder-agent/AgentPipeHost.cs").read_text(encoding="utf-8")
SPOOL = (ROOT / "apps/recorder-agent/SpoolStore.cs").read_text(encoding="utf-8")
STATE_MACHINE = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceStateMachine.cs").read_text(encoding="utf-8")
BROKER = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs").read_text(encoding="utf-8")
BROKER_CLIENT = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceIntentBrokerClient.cs").read_text(encoding="utf-8")
DELIVERY_STORE = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/AssistantDeliveryStore.cs").read_text(encoding="utf-8")
ASSISTANT_PAGE = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Pages/AssistantPage.xaml.cs").read_text(encoding="utf-8")
SETTINGS_PAGE = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Pages/SettingsPage.xaml.cs").read_text(encoding="utf-8")
SETTINGS_VM = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/ViewModels/SettingsViewModel.cs").read_text(encoding="utf-8")
VOICE_UI_STATES = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/ViewModels/VoiceUiStates.cs").read_text(encoding="utf-8")
VOICE_README = (ROOT / "apps/voice-host/README.md").read_text(encoding="utf-8")
ML_PERSISTENCE = (ROOT / "workers/ml_worker/persistence.py").read_text(encoding="utf-8")
TECHNICAL_EVENTS = (ROOT / "workers/ml_worker/technical_events.py").read_text(encoding="utf-8")


def test_voice_host_lifecycle_and_startup_guard_are_explicit():
    assert "VoiceHostState.Starting" in VOICE_RUNTIME
    assert '"VOICE_HOST_NOT_INITIALIZED"' in VOICE_RUNTIME
    assert "Environment.ProcessId" in VOICE_RUNTIME
    assert "BuildIdentity" in VOICE_RUNTIME


def test_voice_command_has_durable_pre_session_outbox_and_idempotent_replay():
    assert "recording_pending_events" in RECORDER_HOST_RUNTIME
    assert "AddPendingEventAsync" in RECORDER_HOST_RUNTIME
    assert "AttachPendingEventAsync" in RECORDER_HOST_RUNTIME
    assert '"VOICE_EVENT" => await _runtime.RecordEventAsync' in RECORDER_HOST_RUNTIME
    assert "eventId" in VOICE_RUNTIME
    assert "response.LocalSessionId" in VOICE_RUNTIME
    assert "INSERT OR IGNORE INTO recording_events" in SPOOL
    assert "AddPendingEventAsync" in LEGACY_AGENT_PIPE
    assert "targetSessionId" in LEGACY_AGENT_PIPE


def test_voice_host_uses_selected_endpoint_and_latest_audio_metrics():
    assert "NormalizeEndpointId" in VOICE_CAPTURE
    assert "ResolveRequestedDevice" in VOICE_CAPTURE
    assert "VOICE_MICROPHONE_UNAVAILABLE" in VOICE_RUNTIME
    assert "VoiceAudioTelemetry" in VOICE_CAPTURE
    assert "Rms" in VOICE_CAPTURE and "Clipping" in VOICE_CAPTURE
    assert "ArrayPool<byte>" in VOICE_CAPTURE
    assert "EffectiveMicrophoneDeviceId" in VOICE_CONTROLLER
    assert "GetDevice(candidate!)" in VOICE_CAPTURE
    assert "MicrophoneErrorDetail" in (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceContracts.cs").read_text(encoding="utf-8")


def test_completed_event_uses_durable_sample_timeline():
    assert "GetSessionMediaTimeMsAsync" in SPOOL
    assert "MAX(start_sample + sample_count)" in SPOOL
    assert "GetSessionMediaTimeMsAsync(sessionId" in RECORDER_HOST_RUNTIME
    assert "GetSessionMediaTimeMsAsync(sessionId" in LEGACY_AGENT_PIPE


def test_voice_host_configure_and_latest_only_telemetry_are_additive():
    assert 'case "CONFIGURE"' in VOICE_RUNTIME
    assert "microphoneDeviceId" in VOICE_RUNTIME
    assert "TelemetryPipeName" in VOICE_IPC
    assert "lastSequence" in VOICE_TELEMETRY
    assert "Task.Delay(100" in VOICE_TELEMETRY


def test_managed_host_is_program_files_only_and_build_checked():
    assert 'Environment.SpecialFolder.ProgramFiles' in VOICE_CONTROLLER
    assert "VOICE_HOST_BUILD_MISMATCH" in VOICE_CONTROLLER
    assert "CurrentBuildIdentity" in VOICE_CONTROLLER
    assert "_lifecycleGate" in VOICE_CONTROLLER
    assert "GetHealthAsync" in VOICE_CONTROLLER
    assert "LastErrorDetail" in VOICE_CONTROLLER
    assert "ExpectedBuildIdentity" in VOICE_CONTROLLER
    assert "SetBuildMismatch" in VOICE_CONTROLLER


def test_voice_host_lease_and_command_safety_are_explicit():
    assert "Global\\WhisperXAtomVoiceHost" in VOICE_LEASE
    assert "TryAcquire" in VOICE_LEASE
    assert 'normalizedCommand is not ("STATUS" or "DOCTOR" or "SHUTDOWN")' in VOICE_RUNTIME
    assert "command.Confidence < 0.70" in VOICE_RUNTIME
    assert "TimeSpan.FromSeconds(10)" in VOICE_RUNTIME
    assert "_speech.IsBusy" in VOICE_RUNTIME
    assert "TouchHeartbeat" in STATE_MACHINE
    assert "VOICE_HOST_RESTART_LIMIT" in VOICE_CONTROLLER
    assert "VOICE_HOST_OWNER_MISMATCH" in VOICE_CONTROLLER


def test_broker_test_mode_does_not_call_recorder_and_returns_trace():
    assert '"EXECUTE_INTENT"' in BROKER
    assert 'if (testMode)' in BROKER
    assert 'RecorderState: "TEST_ONLY"' in BROKER
    assert "TraceId" in BROKER
    assert "ConnectWithRetryAsync" in BROKER_CLIENT
    assert "commandId" in BROKER and "CacheCommand" in BROKER


def test_command_and_conversation_paths_are_disjoint():
    # All recorder mutations are explicit parser intents and terminate in the
    # Desktop command switch; conversational text is the only path that emits
    # ASSISTANT_QUESTION.
    for intent in (
        "StartRecording", "StopRecording", "PauseRecording", "ResumeRecording",
        "AddMarker", "MarkDecision", "MarkActionItem", "GetStatus",
    ):
        assert f'VoiceIntent.{intent}' in VOICE_RUNTIME
    assert 'command = "ASSISTANT_QUESTION"' in BROKER_CLIENT
    assert 'CreateAssistantRequestAsync(' in BROKER
    assert 'requestedMode' in BROKER and 'captureContext.RecordingSessionId' in BROKER and 'captureContext.CaptureState' in BROKER
    command_path = VOICE_RUNTIME.split('var response = command.Intent switch', 1)[1].split('if (command.Intent == VoiceIntent.StartRecording)', 1)[0]
    assert 'AskAssistantAsync' in command_path
    # The Assistant call is only the AssistantQuery branch of the switch; no
    # recorder intent branch can reach it.
    assert 'VoiceIntent.AssistantQuery =>' in command_path


def test_voice_questions_use_the_user_scoped_assistant_and_speak_safe_terminal_results():
    assert 'AskAssistantAsync(string question, string? requestedMode' in BROKER_CLIENT
    assert '"ASSISTANT_QUESTION"' in BROKER
    # AUTO is intentionally forwarded unchanged.  The API, not the Desktop
    # transport, decides GENERAL_CHAT/CURRENT_MEETING/LIVE_MEETING.
    assert 'requestedMode = requestedMode ?? "AUTO"' in BROKER_CLIENT
    assert "CancelAfter(TimeSpan.FromSeconds(15))" in BROKER_CLIENT
    assert 'Detail: "broker_timeout"' in BROKER_CLIENT
    assert 'CreateAssistantRequestAsync(' in BROKER
    assert 'const string requestedMode = "AUTO"' in BROKER
    assert 'ReadCaptureContextAsync' in BROKER
    resolver = (ROOT / "apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs").read_text(encoding="utf-8")
    assert 'AskAssistantAsync(normalizedQuestion, "AUTO"' in VOICE_RUNTIME
    assert '"LIVE_MEETING"' in resolver and "ResolveAsync" in resolver
    assert '"LIVE_MEETING_NOT_READY"' in (ROOT / "apps/server/WhisperX.Atom.Api/Program.cs").read_text(encoding="utf-8")
    assert 'AskAssistantAsync(normalizedQuestion, "AUTO"' in VOICE_RUNTIME
    assert "ResolveAssistantQuestion" not in VOICE_RUNTIME
    assert '"MEETING_HISTORY"' in resolver
    assert '"CURRENT_MEETING"' in resolver
    assert '"LIVE_MEETING_NOT_READY"' in VOICE_RUNTIME
    assert '"GENERAL_CHAT"' in resolver
    # Voice Host never performs the old bounded 180-second polling loop.
    # Desktop owns durable query polling and sends a terminal response back
    # through the backwards-compatible control command.
    assert "CompleteAssistantQuestionAsync" not in VOICE_RUNTIME
    assert '"SPEAK_ASSISTANT_RESULT"' in VOICE_RUNTIME
    assert "DeliverAssistantResultsAsync" in BROKER
    assert "VoiceAssistantConversationStore" in BROKER
    assert 'value.StartsWith("покажи ", StringComparison.Ordinal)' in VOICE_PARSER
    assert 'value.StartsWith("расскажи ", StringComparison.Ordinal)' in VOICE_PARSER


def test_free_question_recognizer_is_separate_from_the_strict_wake_word_path():
    assert "CreateUnrestrictedSession" in VOICE_RECOGNIZERS
    assert "_utteranceRecognizer" in VOICE_RUNTIME
    assert "_wakeRecognizer!.Accept(pcm)" in VOICE_RUNTIME
    assert "_utteranceRecognizer!.Accept(pcm)" in VOICE_RUNTIME
    # Actions remain parser-controlled, therefore arbitrary text cannot call
    # Recorder before it is classified as an explicit intent.
    assert "var command = _parser.Parse(text, confidence, MinimumConfidence());" in VOICE_RUNTIME
    assert "IsAssistantUtterance" in VOICE_PARSER
    assert "_ => IsAssistantUtterance(withoutWake) ? VoiceIntent.AssistantQuery" in VOICE_PARSER
    assert "DefaultMinimumConfidence = 0.55" in VOICE_PARSER
    assert "double.IsFinite(confidence)" in VOICE_PARSER
    assert "minimumConfidence" in VOICE_PARSER
    assert 'Matches(value, "начни запись")' in VOICE_PARSER
    assert 'Matches(value, "заверши запись", "останови запись")' in VOICE_PARSER
    command_table = VOICE_PARSER.split('var intent = withoutWake switch', 1)[1].split('// AssistantQuery', 1)[0]
    assert '"начать запись"' not in command_table
    assert '"остановить запись"' not in command_table
    assert '"запись", "старт"' not in command_table
    assert "AssistantQuery" in (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceContracts.cs").read_text(encoding="utf-8")
    assert "HistoryQuestion = AssistantQuery" in (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceContracts.cs").read_text(encoding="utf-8")


def test_conversational_followups_use_the_existing_assistant_conversation():
    contracts = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceContracts.cs").read_text(encoding="utf-8")
    resolver = (ROOT / "apps/server/WhisperX.Atom.Api/AssistantModeResolver.cs").read_text(encoding="utf-8")
    worker = (ROOT / "workers/summary_worker/assistant.py").read_text(encoding="utf-8")
    for intent in ("RepeatAnswer", "ShortenAnswer", "ElaborateAnswer", "PreviousQuestion"):
        assert intent in contracts
        assert f"VoiceIntent.{intent}" in VOICE_PARSER or f"VoiceIntent.{intent}" in VOICE_RUNTIME
    assert '"Повтори предыдущий ответ."' in VOICE_RUNTIME
    assert '"Сделай предыдущий ответ короче."' in VOICE_RUNTIME
    assert '"Расскажи подробнее по предыдущему ответу."' in VOICE_RUNTIME
    assert '"Повтори предыдущий вопрос."' in VOICE_RUNTIME
    assert '"сделай предыдущий ответ короче"' in resolver
    assert '"вернись к предыдущему вопросу и ответь на него снова"' in resolver
    assert "_retrieval_query_for_follow_up" in worker
    assert 'role"' in worker and '== "user"' in worker


def test_far_field_front_end_never_mutates_recorder_audio_and_commands_have_separate_confidence_policy():
    runtime = VOICE_RUNTIME
    front_end = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceAudioFrontEnd.cs").read_text(encoding="utf-8")
    assert "_voiceFrontEnd.Process(pcm)" in runtime
    assert "durable PCM" in runtime
    assert "HighPassCutoffHz" in front_end
    assert "MaxGain = 4.0d" in front_end
    assert "Math.Clamp(filtered * _gain, -0.92d, 0.92d)" in front_end
    assert '"high" => 0.45' in runtime
    assert 'VoiceIntent.StopRecording or VoiceIntent.StopSpeaking => 0.70' in runtime
    assert 'VoiceIntent.AssistantQuery => MinimumConfidence()' in runtime
    assert "IsConfidenceSufficient(command)" in runtime


def test_voice_snapshot_exposes_adaptive_vad_diagnostics_without_changing_ipc_shape():
    detector = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceActivityDetector.cs").read_text(encoding="utf-8")
    contracts = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceContracts.cs").read_text(encoding="utf-8")
    desktop = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/VoiceHostClient.cs").read_text(encoding="utf-8")
    assert "NoiseFloorDb" in detector and "ThresholdDb" in detector
    assert "VoiceNoiseFloorDb = _vad.NoiseFloorDb" in VOICE_RUNTIME
    assert "VoiceVadThresholdDb = _vad.ThresholdDb" in VOICE_RUNTIME
    assert "double? VoiceNoiseFloorDb = null" in contracts
    assert "double? VoiceVadThresholdDb = null" in desktop


def test_noise_calibration_is_ephemeral_and_wired_to_desktop_settings():
    runtime = VOICE_RUNTIME
    accumulator = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceCalibrationAccumulator.cs").read_text(encoding="utf-8")
    view_model = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/ViewModels/SettingsViewModel.cs").read_text(encoding="utf-8")
    page = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Pages/SettingsPage.xaml").read_text(encoding="utf-8")
    assert 'case "CALIBRATION_START"' in runtime and 'case "CALIBRATION_STOP"' in runtime
    assert "storesAudio = false" in runtime
    assert "RecommendedVadThresholdDb" in accumulator
    assert "ApplyNoiseFloor(result.AverageRms, _sensitivity)" in runtime
    assert 'SendAsync("CALIBRATION_START"' in view_model
    assert 'SendAsync("CALIBRATION_STOP")' in view_model
    assert "Калибровать шум комнаты" in page


def test_voice_general_chat_is_not_blocked_by_an_active_recording():
    broker = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs").read_text(encoding="utf-8")
    store = (ROOT / "apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs").read_text(encoding="utf-8")
    parser = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceIntentParser.cs").read_text(encoding="utf-8")
    assert "AUTO routing" in broker
    assert "The authenticated API owns AUTO routing" in broker
    assert "IsGeneralConversationQuestion" in store
    assert 'var requestMeetingId = captureContext.IsActive' in broker
    assert 'скажи привет' in parser
    assert 'как дела' in parser
    assert 'пошути' in parser
    assistant_block = broker.split('if (string.Equals(commandElement.GetString(), "ASSISTANT_QUESTION"', 1)[1].split('if (string.Equals(commandElement.GetString(), "ASSISTANT_RESULT"', 1)[0]
    assert 'StatusAsync(cancellationToken)' not in assistant_block


def test_assistant_result_delivery_is_observable_and_bounded():
    client = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/VoiceHostClient.cs").read_text(encoding="utf-8")
    controller = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/VoiceHostController.cs").read_text(encoding="utf-8")
    broker = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/DesktopVoiceBrokerServer.cs").read_text(encoding="utf-8")
    assert 'CancelAfter(TimeSpan.FromSeconds(15))' in client
    assert 'ASSISTANT_DELIVERY_EXCEPTION' in controller
    assert 'ASSISTANT_DELIVERY_TERMINAL' in broker
    assert 'ASSISTANT_DELIVERY_PLAYED' in broker
    assert 'ASSISTANT_DELIVERY_AMBIGUOUS_IPC' in broker
    assert 'ASSISTANT_PLAYBACK_STATUS' in broker
    assert 'ReconcileAssistantPlaybackAsync' in broker
    assert 'ASSISTANT_PLAYBACK_STATUS' in VOICE_RUNTIME


def test_missing_accepted_query_stays_in_reconciliation_instead_of_unavailable_speech():
    assert 'assistant_query_not_visible_yet' in BROKER
    assert 'RecorderState: "ASSISTANT_RECONCILING"' in BROKER
    assert 'query_not_found' not in BROKER


def test_voice_documentation_matches_the_unrestricted_question_runtime():
    assert "separate unrestricted Vosk" in VOICE_README
    assert "unrestricted Vosk capture for arbitrary spoken\nquestions is still a release blocker" not in VOICE_README


def test_live_meeting_has_a_separate_provisional_asr_producer():
    assert "_liveRecognizer" in VOICE_RUNTIME
    assert "ProcessLiveAsrAsync" in VOICE_RUNTIME
    assert 'command = "LIVE_ASR_SEGMENTS"' in BROKER_CLIENT
    assert "PublishLiveAsrSegmentAsync" in VOICE_RUNTIME
    assert "_liveRecordingActive" in VOICE_RUNTIME
    assert "_liveRecordingPaused" in VOICE_RUNTIME
    assert "UpdateLiveRecordingState" in VOICE_RUNTIME
    assert "Live provisional ASR publish failed" in VOICE_RUNTIME


def test_two_track_live_audio_isolated_from_commands_and_bounded():
    live_client = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/LiveAudioClient.cs").read_text(encoding="utf-8")
    live_broadcaster = (ROOT / "apps/recorder-host/LiveAudioBroadcaster.cs").read_text(encoding="utf-8")
    assert "WhisperXAtomLiveAudioV1" in live_client
    assert "QueueCapacityPerTrack" in live_broadcaster
    assert "TryPublish" in live_broadcaster
    assert "SESSION_PAUSED" in live_broadcaster and "SESSION_RESUMED" in live_broadcaster
    assert "systemAudioEnabled" in live_broadcaster and "systemAudioEnabled" in live_client
    assert "ConnectionChanged" in live_client and "MIC_FALLBACK" in VOICE_RUNTIME
    assert "_liveLocalRecognizer" in VOICE_RUNTIME and "_liveRemoteRecognizer" in VOICE_RUNTIME
    assert 'string.Equals(frame.TrackType, "system-audio", StringComparison.OrdinalIgnoreCase)' in VOICE_RUNTIME
    # The system track is consumed only by the provisional recognizer worker;
    # command/wake recognizers remain on the normal microphone callback.
    live_worker = VOICE_RUNTIME[VOICE_RUNTIME.index("private async Task ProcessLiveAudioFramesAsync"):VOICE_RUNTIME.index("private async Task ProcessLiveAsrAsync")]
    assert "_liveRemoteRecognizer" in live_worker
    assert "_utteranceRecognizer" not in live_worker
    assert "_wakeRecognizer" not in live_worker
    assert "_cancelRecognizer" not in live_worker
    assert "MIC_FALLBACK" in VOICE_RUNTIME
    assert "if (_speech.IsBusy || IsLiveTtsSuppressed())" in live_worker


def test_voice_responder_never_uses_legacy_wav_replies():
    assert "public bool UsesPreRecordedResponses => false" in SPEECH_RESPONDER
    assert "PlayWavAsync" not in SPEECH_RESPONDER
    assert "ResponseKey" not in SPEECH_RESPONDER
    assert "ATOM_VOICE_USE_PRERECORDED_RESPONSES" not in SPEECH_RESPONDER
    assert '"Microsoft Irina"' in SPEECH_RESPONDER


def test_tts_cancellation_has_an_isolated_recognizer_and_playback_result():
    assert "CancelGrammar" in VOICE_RUNTIME
    assert '"мифодий прекрати говорить"' in VOICE_RUNTIME
    assert "ProcessCancelPcmAsync" in VOICE_RUNTIME
    assert "_cancelRecognizer" in VOICE_RUNTIME
    assert "if (_speech.IsBusy)" in VOICE_RUNTIME
    assert "_speech.CancelAll()" in VOICE_RUNTIME
    assert "SpeechPlaybackState" in SPEECH_RESPONDER
    assert "TryEnqueueDetailed" in SPEECH_RESPONDER
    assert "_cancelGeneration" in SPEECH_RESPONDER
    assert "AnswerStatus" in (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceContracts.cs").read_text(encoding="utf-8")
    assert "LiveTtsTailTicks" in VOICE_RUNTIME
    assert "IsLiveTtsSuppressed" in VOICE_RUNTIME


def test_assistant_timing_metadata_is_exposed_without_storing_transcript_locally():
    api_store = (ROOT / "apps/server/WhisperX.Atom.Api/UnifiedProductStore.cs").read_text(encoding="utf-8")
    frontend = (ROOT / "apps/desktop/WhisperX.Atom.Desktop/Services/FrontendContracts.cs").read_text(encoding="utf-8")
    assert "q.answer_metadata" in api_store
    assert "TimingsText" in frontend
    assert "voice-delivery.json" in DELIVERY_STORE


def test_assistant_delivery_is_durable_idempotent_and_reports_playback_ack():
    assert "Pending = 0" in DELIVERY_STORE
    assert "Accepted = 4" in DELIVERY_STORE
    assert "Cancelled = 5" in DELIVERY_STORE
    assert "Ambiguous = 6" in DELIVERY_STORE
    assert "voice-delivery.json" in DELIVERY_STORE
    assert "File.Move(temporary, _path, true)" in DELIVERY_STORE
    assert "DuplicateSuppressed" in DELIVERY_STORE
    assert '"ASSISTANT_PLAYBACK_FINISHED"' in BROKER
    assert "MarkDelivered" in BROKER
    assert "MarkAmbiguous" in BROKER


def test_tts_cancel_suppresses_normal_recognizer_until_playback_worker_finishes():
    assert "playbackWasActive" in SPEECH_RESPONDER
    assert "playbackWasActive ? 1 : 0" in SPEECH_RESPONDER
    assert '"VOICE_QUIET_MODE"' in SPEECH_RESPONDER
    assert "Do not create a synthetic technical interval" in VOICE_RUNTIME


def test_interrupted_assistant_dispatch_becomes_ambiguous_without_replay():
    assert "DispatchAmbiguityTimeout" in DELIVERY_STORE
    assert "Desktop\n                // terminated after claiming" in DELIVERY_STORE
    assert "entry.State == AssistantDeliveryState.Dispatching" in DELIVERY_STORE
    assert "State = AssistantDeliveryState.Ambiguous" in DELIVERY_STORE
    assert "PlaybackReconcileAfter" in DELIVERY_STORE
    assert "reconcilingAcceptedPlayback" in BROKER
    assert '"RESERVED"' in VOICE_RUNTIME
    assert '"VOICE_PLAYBACK_LEDGER_UNAVAILABLE"' in VOICE_RUNTIME
    assert '"VOICE_PLAYBACK_LEDGER_UNAVAILABLE"' in BROKER


def test_assistant_query_acceptance_is_silent_and_result_is_spoken_once():
    assert 'Speak: false' in VOICE_RUNTIME
    assert 'assistant-query-queued' in VOICE_RUNTIME
    assert 'ASSISTANT_RESULT' in VOICE_RUNTIME
    # The generic acknowledgement must not be synthesized on every query.
    assert '"Вопрос принят, отвечу после обработки."' not in VOICE_RUNTIME


def test_exact_greetings_use_local_tts_without_assistant_roundtrip():
    assert "LocalGreetingText" in VOICE_RUNTIME
    assert '"привет" or "скажи привет" or "поздоровайся"' in VOICE_RUNTIME
    assert 'VoiceIntent.AssistantQuery when LocalGreetingText' in VOICE_RUNTIME
    # Contextual or longer utterances must remain ordinary AssistantQuery;
    # only the exact normalized greeting receives the local fast path.
    assert 'VoiceIntent.AssistantQuery => await AskAssistantAsync' in VOICE_RUNTIME


def test_duplicate_voice_command_is_visible_but_silent():
    assert 'Команда уже выполняется' in VOICE_RUNTIME
    assert 'speak: false' in VOICE_RUNTIME
    assert 'if (!speak || _speech.QuietMode)' in VOICE_RUNTIME


def test_voice_ledger_covers_wake_recognition_result_and_tts_without_raw_audio():
    assert '"VOICE_WAKE_DETECTED"' in VOICE_RUNTIME
    assert '"VOICE_RECOGNIZED"' in VOICE_RUNTIME
    assert '"ASSISTANT_RESULT_READY"' in VOICE_RUNTIME
    assert '"SYSTEM_RESPONSE_STARTED"' in VOICE_RUNTIME
    assert '"SYSTEM_RESPONSE_FINISHED"' in VOICE_RUNTIME
    assert "QueueVoiceLedgerEvent" in VOICE_RUNTIME
    assert "recognizedTextLength" in VOICE_RUNTIME
    # TEST_SPEECH may echo text for an explicit local diagnostic request;
    # the durable lifecycle event itself stores only its length.
    assert "recognizedText = command.Text" not in VOICE_RUNTIME
    ledger = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceLedgerStore.cs").read_text(encoding="utf-8")
    assert "voice-ledger.jsonl" in ledger
    assert "MaximumBytes" in ledger
    assert "audio" in ledger.lower()


def test_assistant_page_refreshes_on_voice_result_without_manual_refresh():
    assert "AssistantResultAvailable" in ASSISTANT_PAGE
    assert "RefreshSelectedConversationAsync" in ASSISTANT_PAGE
    assert "LoadConversationsAsync" in ASSISTANT_PAGE


def test_server_builds_bounded_system_response_intervals():
    assert "def _technical_intervals" in ML_PERSISTENCE
    assert '"SYSTEM_RESPONSE_STARTED"' in TECHNICAL_EVENTS
    assert '"SYSTEM_RESPONSE_FINISHED"' in TECHNICAL_EVENTS
    assert "VOICE_TECHNICAL_EVENT_MAX_MS" in TECHNICAL_EVENTS
    assert '"TECHNICAL"' in TECHNICAL_EVENTS
    assert "is_hidden" in ML_PERSISTENCE


def test_ui_exposes_live_telemetry_timeout_and_trace():
    assert "TimeSpan.FromMilliseconds(750)" in VOICE_UI_STATES
    assert "VoiceLastTraceId" in SETTINGS_VM
    assert "VoiceBarsPanel" in SETTINGS_PAGE
    assert "FromMilliseconds(33)" in SETTINGS_PAGE
    assert "SubscribeTelemetryAsync" in SETTINGS_PAGE
    assert "VoiceRequestedMicrophone" in SETTINGS_VM


def test_system_response_intervals_hide_only_their_media_window():
    intervals = build_technical_intervals(
        [("VOICE_COMMAND", 1000), ("SYSTEM_RESPONSE_STARTED", 1200), ("SYSTEM_RESPONSE_FINISHED", 2400)],
        max_open_ms=5000,
    )
    # VOICE_COMMAND is a point marker and must not hide an ASR segment that
    # merely contains the command. Only the actual TTS interval is technical.
    assert segment_technical_flags(1000, 1100, intervals) == ("SPEECH", False)
    assert segment_technical_flags(1300, 2000, intervals) == ("TECHNICAL", True)
    assert segment_technical_flags(3000, 4000, intervals) == ("SPEECH", False)


def test_voice_command_marker_does_not_hide_long_user_segment():
    intervals = build_technical_intervals([("VOICE_COMMAND", 1000)])
    assert segment_technical_flags(0, 5000, intervals) == ("SPEECH", False)


def test_orphaned_tts_start_is_bounded():
    intervals = build_technical_intervals([("SYSTEM_RESPONSE_STARTED", 1000)], max_open_ms=1500)
    assert intervals == [(1000, 2500, "TECHNICAL")]
