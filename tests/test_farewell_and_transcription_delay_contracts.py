from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PARSER = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Core/VoiceIntentParser.cs").read_text(encoding="utf-8")
RUNTIME = (ROOT / "apps/voice-host/WhisperX.Atom.Voice.Host/VoiceHostRuntime.cs").read_text(encoding="utf-8")
MEDIA = (ROOT / "workers/media_worker/persistence.py").read_text(encoding="utf-8")
RELAY = (ROOT / "workers/outbox_relay/worker.py").read_text(encoding="utf-8")
LEASE = (ROOT / "workers/gpu_lease.py").read_text(encoding="utf-8")
MIGRATION = (ROOT / "apps/server/WhisperX.Atom.Api/Migrations/045_transcription_start_delay.sql").read_text(encoding="utf-8")


def test_farewell_is_local_and_exactly_gated():
    assert "Farewell" in PARSER
    for phrase in ("пока", "до свидания", "до встречи", "хорошего дня", "спокойной ночи"):
        assert f'"{phrase}"' in PARSER
    assert "IsExact(value" in PARSER
    assert "VoiceIntent.Farewell => new VoiceResponse(FarewellText" in RUNTIME
    assert "return await RespondAsync(response.Text" in RUNTIME
    # The Farewell branch must be distinct from the Assistant branch.
    command_path = RUNTIME.split("var response = command.Intent switch", 1)[1].split(
        "if (command.Intent == VoiceIntent.StartRecording)", 1
    )[0]
    assert "VoiceIntent.Farewell" in command_path
    assert "VoiceIntent.AssistantQuery => await AskAssistantAsync" in command_path


def test_transcription_delay_is_durable_and_bypassed_after_due_time():
    assert "TRANSCRIPTION_START_DELAY_SECONDS" in MEDIA
    assert "not_before=%s" in MEDIA
    assert "ALTER TABLE jobs ADD COLUMN IF NOT EXISTS not_before" in MIGRATION
    assert "delayed_job.not_before > now()" in RELAY
    assert "j.not_before IS NULL OR j.not_before <= now()" in RELAY
    assert "not_before IS NULL OR not_before <= now()" in LEASE
