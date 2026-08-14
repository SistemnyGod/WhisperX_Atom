from pathlib import Path


ROOT = Path(__file__).parents[1]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_audio_preview_is_range_enabled_and_scoped_to_meeting_rbac():
    program = source("apps/server/WhisperX.Atom.Api/Program.cs")
    preview = program.split('app.MapGet("/api/media/{id:guid}/preview"', 1)[1].split('app.MapGet("/api/jobs/{id:guid}"', 1)[0]
    assert "CanAccessMeetingAsync(context, media.MeetingId)" in preview
    assert "enableRangeProcessing: true" in preview


def test_manual_edit_creates_a_new_version_without_updating_the_source_segment():
    program = source("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "CreateEditedTranscriptVersionAsync" in program
    assert "'USER_EDITED'" in program
    assert "source_transcript_id" in program
    assert "CASE WHEN ordinal=@ordinal THEN @text ELSE text END" in program


def test_speaker_merge_is_limited_to_current_transcript_and_audited():
    program = source("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "ORDER BY version DESC LIMIT 1" in program
    assert "SPEAKER_MERGED_CURRENT_TRANSCRIPT" in program
    assert "DELETE FROM meeting_speakers WHERE id=@source" not in program


def test_reprocess_and_worker_persist_new_reprocessed_version():
    program = source("apps/server/WhisperX.Atom.Api/Program.cs")
    worker = source("workers/ml_worker/persistence.py")
    assert "TRANSCRIBE_REPROCESS" in program
    assert "'ml.transcribe'" in program
    assert 'version_kind = "REPROCESSED"' in worker
    assert 'EndsWith("/transcript/reprocess"' in program


def test_transcript_registry_uses_lazy_loading_and_timestamp_seek_exists():
    vm = source("apps/desktop/WhisperX.Atom.Desktop/ViewModels/TranscriptsViewModel.cs")
    page = source("apps/desktop/WhisperX.Atom.Desktop/Pages/MeetingsPage.xaml.cs")
    assert "LoadSelectedAsync" in vm
    assert "new TranscriptRegistryItem(item)" in vm
    assert "PlaybackSession.Position = TimeSpan.FromMilliseconds(segment.StartMs)" in page


def test_transcript_words_are_opt_in_to_keep_default_payload_small():
    program = source("apps/server/WhisperX.Atom.Api/Program.cs")
    assert "bool? includeWords" in program
    assert "includeWords == true" in program
    assert "CASE WHEN @include_words THEN s.words ELSE NULL END" in program
    assert 'command.Parameters.AddWithValue("include_words", includeWords);' in program
