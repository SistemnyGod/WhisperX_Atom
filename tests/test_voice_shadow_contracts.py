from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
HOST = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs").read_text(encoding="utf-8")
REFINER = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/WhisperCppVoiceRefiner.cs").read_text(encoding="utf-8")
CONTRACTS = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceRefinementContracts.cs").read_text(encoding="utf-8")


def test_shadow_is_optional_and_never_part_of_readiness_failure():
    assert 'VOICE_ASR_REFINER_MODE' in HOST
    assert 'ParseVoiceRefinerMode' in HOST
    assert 'VoiceRefinerMode.Shadow' in HOST
    assert 'VoiceRefinerMode.Off' in HOST
    assert 'VoiceRefinerMode.AssistantOnly' in HOST
    assert 'VoiceRefinerMode.WakeAudit' in HOST
    assert 'VOICE_REFINER_MODE_INVALID' in HOST
    assert "IVoiceAsrRefiner" in HOST
    assert "QueueShadowRefinement" in HOST
    assert "ApplyAssistantRefinementAsync" in HOST
    assert "AssistantRefinementBudgetMs = 5_000" in HOST
    assert 'VoiceRefinerState' in HOST


def test_shadow_requires_verified_model_and_is_bounded():
    client = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/ResidentVoiceRefinerClient.cs").read_text(encoding="utf-8")
    host = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Refiner.Host/Program.cs").read_text(encoding="utf-8")
    assert 'VOICE_REFINER_ASSETS_UNAVAILABLE' in client
    assert 'VOICE_REFINER_ASSET_INTEGRITY_FAILED' in host
    assert 'VoiceRefinerProtocol.QueueCapacity' in host
    assert 'PipeOptions.CurrentUserOnly' in host
    assert 'voice-refiner.manifest.json' in client
    assert 'ReadManifestHashes' in client
    assert 'voice-shadow-*.wav' not in REFINER
    assert 'whisper-cli' not in REFINER.lower()


def test_envelope_does_not_define_serialized_or_ledger_payload():
    assert 'byte[] Pcm16kMono' in CONTRACTS
    assert 'IVoiceAsrRefiner' in CONTRACTS
    assert 'Serialize' not in CONTRACTS
    assert 'VoiceRefinementState' in CONTRACTS


def test_shadow_protocol_is_private_and_bounded():
    protocol = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceRefinerProtocol.cs").read_text(encoding="utf-8")
    buffer = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/UtteranceCaptureBuffer.cs").read_text(encoding="utf-8")
    assert "CurrentUserOnly" in (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Refiner.Host/Program.cs").read_text(encoding="utf-8")
    assert "QueueCapacity = 2" in protocol
    assert "MaxPcmBytes" in protocol
    assert "preRollSeconds = 2" in buffer
    assert "maxSeconds = 20" in buffer
    assert "Pcm16kMono" in CONTRACTS
    assert "VoiceRefinerProtocol.MaxPcmBytes - (int)_pttBuffer.Length" in HOST


def test_recorder_thresholds_are_strict_and_question_guard_is_preserved():
    assert 'VoiceIntent.StartRecording or VoiceIntent.PauseRecording or VoiceIntent.ResumeRecording => 0.70' in HOST
    assert 'VoiceIntent.StopRecording => 0.75' in HOST
    parser = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceIntentParser.cs").read_text(encoding="utf-8")
    assert 'if (ContainsQuestionMarker(text) || IsQuestion(withoutWake)) return false;' in parser


def test_release_packaging_cannot_skip_asset_gate():
    installer = (ROOT / "scripts/build-installer.ps1").read_text(encoding="utf-8-sig")
    publish = (ROOT / "scripts/publish-desktop.ps1").read_text(encoding="utf-8-sig")
    assert 'RequireVoiceRefinerAssets' in installer
    assert 'verify-voice-refiner-assets.ps1' in installer
    assert 'VoiceRefinerHost' in publish


def test_shadow_rc_uses_resident_attestation_and_host_watchdog():
    client = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/ResidentVoiceRefinerClient.cs").read_text(encoding="utf-8")
    host = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Refiner.Host/Program.cs").read_text(encoding="utf-8")
    protocol = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceRefinerProtocol.cs").read_text(encoding="utf-8")
    assert "FileAttestationCache" in client
    assert "LastWriteTimeUtc" in client
    assert "VOICE_REFINER_NATIVE_ABI_MISMATCH" in host
    assert "InferenceTimeoutExitCode = 73" in protocol
    assert "Environment.Exit(VoiceRefinerProtocol.InferenceTimeoutExitCode)" in host
    assert "Task.Run" in host
    assert "VerifyManifest" in host
    assert "VOICE_REFINER_ASSET_CHANGED" in client
    assert "CancelAfter(_timeout)" in client
    assert "response.SampleRate" in client
    assert "TryApplyRefinerSequence" in HOST


def test_shadow_rc_manifest_v2_and_real_capture_windows():
    stage = (ROOT / "scripts/stage-voice-refiner-assets.ps1").read_text(encoding="utf-8-sig")
    verify = (ROOT / "scripts/verify-voice-refiner-assets.ps1").read_text(encoding="utf-8-sig")
    buffer = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/UtteranceCaptureBuffer.cs").read_text(encoding="utf-8")
    bridge = (ROOT / "apps/voice-host/native/whisper-refiner/whisperx_refiner_bridge.cpp").read_text(encoding="utf-8")
    assert "schemaVersion = 2" in stage
    assert "whisperCppRevision" in stage and "bridgeRevision" in stage
    assert "schemaVersion -ne 2" in verify
    assert "VOICE_REFINER_MANIFEST_UPGRADE_REQUIRED" in verify
    assert "preRollSeconds = 2" in buffer
    assert "maxSeconds = 20" in buffer
    assert "postRollSeconds = 0.4" in buffer
    assert "CompleteForShadow" in buffer
    assert "whisperx_refiner_abi_version" in bridge
    assert "params.use_gpu = false" in bridge
    assert "f049fff95a089aa9969deb009cdd4892b3e74916" in stage
    assert "c521a4b02f422512d734391fdf08bb08c0862f68" in stage
    assert "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b" in verify
    assert "WHISPER_CPP_SOURCE_DIR" in (ROOT / "apps/voice-host/native/whisper-refiner/CMakeLists.txt").read_text(encoding="utf-8")


def test_shadow_agreement_is_diagnostic_and_corpus_is_release_gated():
    parser = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceIntentParser.cs").read_text(encoding="utf-8")
    corpus = (ROOT / "scripts/voice-shadow-corpus.ps1").read_text(encoding="utf-8-sig")
    assert "ParseIntentForComparison" in parser
    assert "_parser.ParseIntentForComparison" in HOST
    assert "result.Confidence ?? 0" not in HOST
    assert "voice-shadow-corpus-v2" in corpus
    assert "$minimumCases = 700" in corpus
    assert "refinedAccuracyGain" in corpus
    assert "wakeByDistance" in corpus
    assert "actualRecorderMutation" in corpus
    assert "if ($status -ne 'READY_FOR_REVIEW') { exit 2 }" in corpus
