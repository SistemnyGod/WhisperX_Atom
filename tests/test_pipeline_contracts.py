from __future__ import annotations

from pathlib import Path

import pytest

from whisperx_atom.alignment_engine import WhisperXAlignmentEngine
from whisperx_atom.asr_engine import WhisperXAsrEngine
from whisperx_atom.core_pipeline import WhisperXCorePipeline
from whisperx_atom.diarization_engine import WhisperXDiarizationEngine
from whisperx_atom.preprocessing_engine import WhisperXPreprocessingEngine
from whisperx_atom.processing import ProcessingService
from whisperx_atom.pipeline_contract import (
    PipelineJob,
    PipelineStage,
    can_transition,
    is_retryable_stage,
    is_terminal_stage,
    normalize_stage,
    PipelineStageTracker,
    require_transition,
    validate_stage_name,
)
from whisperx_atom.storage import LocalMediaStorage
from whisperx_atom.domain import JobId, MeetingId, ProcessingJobRef, require_meeting_id


def test_historical_stage_aliases_map_to_domain_values_without_rewriting_storage():
    assert normalize_stage("TRANSCRIPT_ENRICH") is PipelineStage.ENRICHING
    assert normalize_stage("MEDIA_RETRY_WAIT") is PipelineStage.RETRY_WAIT
    assert normalize_stage("WAITING_FOR_GPU") is PipelineStage.RETRY_WAIT
    assert normalize_stage("ALIGNMENT_PARTIAL") is PipelineStage.ALIGNING
    assert normalize_stage("READY") is PipelineStage.COMPLETED
    assert validate_stage_name("TRANSCRIPT_ENRICH") == "TRANSCRIPT_ENRICH"


def test_pipeline_transition_is_idempotent_and_rejects_terminal_regression():
    assert can_transition("VALIDATING", "NORMALIZING")
    assert can_transition("NORMALIZING", "NORMALIZING")
    assert can_transition("ASR_READY", "ENRICHING")
    assert not can_transition("COMPLETED", "TRANSCRIBING")
    assert require_transition("RETRY_PENDING", "QUEUED") is PipelineStage.QUEUED
    with pytest.raises(ValueError, match="PIPELINE_INVALID_TRANSITION"):
        require_transition("COMPLETED", "QUEUED")


def test_terminal_and_retry_states_are_explicit():
    assert is_terminal_stage("ASR_READY")
    assert is_terminal_stage("ENRICHED_READY")
    assert is_terminal_stage("FAILED")
    assert not is_terminal_stage("TRANSCRIBING")
    assert is_retryable_stage("RETRY_PENDING")


def test_pipeline_job_is_identifier_only_and_bounds_progress():
    job = PipelineJob.from_values(
        job_id="job-1",
        meeting_id="meeting-1",
        job_type="TRANSCRIBE_ASR",
        stage="READY_FOR_ASR",
        status="QUEUED",
        progress=180,
        attempt=-2,
        correlation_id="corr-1",
    )
    assert job.stage is PipelineStage.READY_FOR_ASR
    assert job.progress == 100
    assert job.attempt == 0
    assert job.correlation_id == "corr-1"
    assert not hasattr(job, "text")


def test_gpu_worker_uses_the_core_pipeline_facade():
    source = open("workers/ml_worker/worker.py", encoding="utf-8").read()
    assert "WhisperXCorePipeline" in source
    assert "self._pipeline.process" in source
    assert "from whisperx_atom.processing import ProcessingService" not in source


def test_core_pipeline_keeps_legacy_service_in_one_adapter():
    source = open("whisperx_atom/core_pipeline.py", encoding="utf-8").read()
    assert "ProcessingService" in source
    assert "class WhisperXCorePipeline" in source


def test_processing_service_accepts_an_injected_asr_engine():
    class FakeAsrEngine:
        def transcribe(self, pipeline, context, *, vad_onset, chunk_size, beam_size):
            return {
                "language": "ru",
                "segments": [],
                "word_segments": [],
                "engine_options": {
                    "vad_onset": vad_onset,
                    "chunk_size": chunk_size,
                    "beam_size": beam_size,
                },
            }

    fake = FakeAsrEngine()
    service = ProcessingService(asr_engine=fake)
    try:
        assert service._asr_engine is fake
    finally:
        service.close()


def test_processing_service_accepts_an_injected_preprocessing_engine():
    class FakePreprocessingEngine:
        def prepare_asr_input(self, pipeline, input_path, acoustic_profile="AUTO"):
            return input_path, {"acoustic_profile": acoustic_profile}

        def prepare_diarization_input(self, pipeline, input_path):
            return input_path

        def prepare_profile(self, pipeline, input_path, profile):
            return input_path

    fake = FakePreprocessingEngine()
    service = ProcessingService(preprocessing_engine=fake)
    try:
        assert service._preprocessing_engine is fake
    finally:
        service.close()


def test_processing_service_accepts_alignment_and_diarization_engines():
    class FakeAlignmentEngine:
        def align(self, pipeline, context, result):
            return {**result, "aligned": True}

    class FakeDiarizationEngine:
        def diarize(self, pipeline, context, result, profile="diar"):
            return {**result, "diarization_profile": profile}

    alignment = FakeAlignmentEngine()
    diarization = FakeDiarizationEngine()
    service = ProcessingService(alignment_engine=alignment, diarization_engine=diarization)
    try:
        assert service._alignment_engine is alignment
        assert service._diarization_engine is diarization
    finally:
        service.close()


def test_core_pipeline_can_inject_asr_engine_without_touching_worker_contract():
    class FakeAsrEngine:
        def transcribe(self, pipeline, context, *, vad_onset, chunk_size, beam_size):
            return {}

    fake = FakeAsrEngine()
    pipeline = WhisperXCorePipeline(asr_engine=fake)
    try:
        assert pipeline._service._asr_engine is fake
    finally:
        pipeline.close()

    with pytest.raises(ValueError, match="CORE_PIPELINE_SERVICE_AND_DEPENDENCIES_ARE_MUTUALLY_EXCLUSIVE"):
        WhisperXCorePipeline(ProcessingService(), asr_engine=fake)


def test_whisperx_preprocessing_engine_preserves_legacy_method_shapes():
    class LegacyPipeline:
        def prepare_asr_input(self, input_path, acoustic_profile):
            return input_path, {"acoustic_profile": acoustic_profile}

        def _preprocess_audio(self, input_path, asr):
            assert asr is False
            return input_path.with_suffix(".diar.wav")

        def _preprocess_audio_profile(self, input_path, profile):
            return input_path.with_suffix(f".{profile}.wav")

    source = Path("meeting.wav")
    engine = WhisperXPreprocessingEngine()
    path, metadata = engine.prepare_asr_input(LegacyPipeline(), source, "LARGE_ROOM")
    assert path == source
    assert metadata == {"acoustic_profile": "LARGE_ROOM"}
    assert engine.prepare_diarization_input(LegacyPipeline(), source) == Path("meeting.diar.wav")
    assert engine.prepare_profile(LegacyPipeline(), source, "asr_far_field") == Path("meeting.asr_far_field.wav")


def test_whisperx_alignment_and_diarization_adapters_preserve_legacy_method_shapes():
    class LegacyPipeline:
        def _align_result(self, context, result):
            return {**result, "aligned": True}

        def _apply_diarization(self, context, result, profile):
            return {**result, "profile": profile}

    context = object()
    source = {"segments": [{"text": "готово"}]}
    assert WhisperXAlignmentEngine().align(LegacyPipeline(), context, source)["aligned"] is True
    assert WhisperXDiarizationEngine().diarize(LegacyPipeline(), context, source, "diar_retry")["profile"] == "diar_retry"


def test_whisperx_asr_engine_preserves_legacy_pipeline_call_shape():
    class LegacyPipeline:
        def run_asr_pass(self, context, vad_onset, chunk_size, beam_size):
            return {
                "language": "ru",
                "segments": [{"text": "готово"}],
                "options": (context, vad_onset, chunk_size, beam_size),
            }

    context = object()
    result = WhisperXAsrEngine().transcribe(
        LegacyPipeline(),
        context,
        vad_onset=0.4,
        chunk_size=20,
        beam_size=7,
    )
    assert result["language"] == "ru"
    assert result["segments"][0]["text"] == "готово"
    assert result["options"] == (context, 0.4, 20, 7)


def test_whisperx_asr_engine_normalizes_none_result_to_empty_mapping():
    class EmptyPipeline:
        def run_asr_pass(self, context, vad_onset, chunk_size, beam_size):
            return None

    assert WhisperXAsrEngine().transcribe(
        EmptyPipeline(),
        object(),
        vad_onset=0.5,
        chunk_size=30,
        beam_size=5,
    ) == {}


def test_media_storage_resolves_keys_consistently_and_rejects_traversal():
    # The adapter is pure path resolution; using the repository path keeps
    # this contract test independent of Windows temp-directory ACLs.
    root = Path.cwd()
    storage = LocalMediaStorage(root)
    assert storage.resolve("/data/meetings/m-1/original.wav") == (root / "meetings" / "m-1" / "original.wav").resolve()
    for invalid in ("relative.wav", "/data/../secret.wav", "/data/a/../../secret.wav", "/data/C:/secret.wav"):
        with pytest.raises(ValueError, match="invalid_storage_key"):
            storage.resolve(invalid)


def test_stage_tracker_preserves_storage_names_and_rejects_regressions():
    emitted: list[tuple[str, int]] = []
    tracker = PipelineStageTracker(lambda stage, progress: emitted.append((stage, progress)))
    tracker.emit("VALIDATING", 5)
    tracker.emit("NORMALIZING", 15)
    tracker.emit("READY_FOR_ASR", 25)
    assert emitted == [("VALIDATING", 5), ("NORMALIZING", 15), ("READY_FOR_ASR", 25)]
    with pytest.raises(ValueError, match="PIPELINE_INVALID_TRANSITION"):
        tracker.emit("VALIDATING", 1)


def test_summary_progress_substages_share_summarizing_domain_stage():
    for stage in (
        "PREPARING_CONTEXT",
        "EXTRACTING_FACTS",
        "GROUPING_TOPICS",
        "RESOLVING_DECISIONS",
        "EXTRACTING_TASKS",
        "RESOLVING_DEADLINES",
        "VALIDATING_EVIDENCE",
        "DEDUPLICATING",
        "MERGING_FACTS",
        "RESOLVING_ENTITIES",
        "GENERATING_SUMMARY",
    ):
        assert normalize_stage(stage) is PipelineStage.SUMMARIZING


def test_meeting_graph_requires_central_identity_without_storing_payload_data():
    meeting = require_meeting_id({"meeting_id": "meeting-1"})
    assert isinstance(meeting, MeetingId)
    job = ProcessingJobRef.from_payload(
        {"job_id": "job-1", "meeting_id": str(meeting), "type": "TRANSCRIBE_ASR", "stage": "READY_FOR_ASR"}
    )
    assert isinstance(job.job_id, JobId)
    assert job.meeting_id == meeting
    assert not hasattr(job, "text")
    with pytest.raises(ValueError, match="MEETING_ID_REQUIRED"):
        require_meeting_id({"job_id": "job-1"})
