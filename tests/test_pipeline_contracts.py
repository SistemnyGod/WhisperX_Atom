from __future__ import annotations

from pathlib import Path
import hashlib
import shutil
import uuid

import pytest

from whisperx_atom.alignment_engine import WhisperXAlignmentEngine
from whisperx_atom.asr_engine import WhisperXAsrEngine
from whisperx_atom.core_pipeline import WhisperXCorePipeline
from whisperx_atom.diarization_engine import WhisperXDiarizationEngine
from whisperx_atom.preprocessing_engine import WhisperXPreprocessingEngine
from whisperx_atom.postprocessing_engine import WhisperXPostprocessingEngine
from whisperx_atom.gpu_scheduler import GpuScheduler
from whisperx_atom.stage_result import StageResult, StageResultStatus, StageResults
from whisperx_atom.contracts import ProcessingRequest, ProcessingResult
from whisperx_atom.checkpoint_store import LocalPipelineCheckpointStore
from whisperx_atom.processing import ProcessingService
from whisperx_atom.runtime import LegacyPipelineStageAdapter, WhisperXRuntime
from whisperx_atom.storage import LocalMediaStorage
from whisperx_atom.domain import JobId, MeetingId, ProcessingJobRef, require_meeting_id
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


@pytest.fixture
def workspace_tmp_path():
    """Writable temporary root independent of the Windows profile TEMP ACL."""

    directory = Path.cwd() / "tests" / "_pipeline_test_artifacts" / uuid.uuid4().hex
    directory.mkdir(parents=True, exist_ok=False)
    try:
        yield directory
    finally:
        shutil.rmtree(directory, ignore_errors=True)


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


def test_runtime_stage_adapter_exposes_only_explicit_stage_operations():
    class LegacyPipeline:
        class Cache:
            pass

        def __init__(self):
            self.cache = self.Cache()

        def prepare_asr_input(self, path, profile):
            return path, {"profile": profile}

        def _preprocess_audio(self, path, asr):
            assert asr is False
            return path

        def _preprocess_audio_profile(self, path, profile):
            return path

        def run_asr_pass(self, context, vad_onset, chunk_size, beam_size):
            return {"segments": [], "options": (context, vad_onset, chunk_size, beam_size)}

        def _align_result(self, context, result):
            return {**result, "aligned": True}

        def _apply_diarization(self, context, result, profile):
            return {**result, "profile": profile}

        def _apply_glossary(self, result):
            return {**result, "postprocessed": True}

    legacy = LegacyPipeline()
    adapter = WhisperXRuntime.get_stage_adapter(legacy)
    assert isinstance(adapter, LegacyPipelineStageAdapter)
    assert WhisperXRuntime.get_stage_adapter(adapter) is adapter
    assert adapter.prepare_asr_input(Path("a.wav"), "AUTO")[1] == {"profile": "AUTO"}
    assert adapter.prepare_diarization_input(Path("a.wav")) == Path("a.wav")
    assert adapter.prepare_profile(Path("a.wav"), "asr_far_field") == Path("a.wav")
    assert adapter.transcribe("ctx", vad_onset=0.4, chunk_size=20, beam_size=5)["options"] == ("ctx", 0.4, 20, 5)
    assert adapter.align("ctx", {})["aligned"] is True
    assert adapter.diarize("ctx", {}, "diar_retry")["profile"] == "diar_retry"
    assert adapter.postprocess({})["postprocessed"] is True


def test_processing_service_keeps_legacy_imports_inside_runtime_boundary():
    processing = Path("whisperx_atom/processing.py").read_text(encoding="utf-8")
    runtime = Path("whisperx_atom/runtime.py").read_text(encoding="utf-8")
    assert "from app.transcription_pipeline import" not in processing
    assert "get_stage_adapter" in processing
    assert "from app.transcription_pipeline import" in runtime


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


def test_processing_service_accepts_an_injected_postprocessing_engine():
    class FakePostprocessingEngine:
        def postprocess(self, pipeline, context, result):
            return {**result, "postprocessed": True}

    fake = FakePostprocessingEngine()
    service = ProcessingService(postprocessing_engine=fake)
    try:
        assert service._postprocessing_engine is fake
    finally:
        service.close()


def test_whisperx_postprocessing_adapter_preserves_legacy_glossary_shape():
    class LegacyPipeline:
        def _apply_glossary(self, result):
            return {**result, "glossary": True}

    source = {"segments": [{"text": "готово"}]}
    assert WhisperXPostprocessingEngine().postprocess(LegacyPipeline(), object(), source)["glossary"] is True


def test_gpu_scheduler_clamps_concurrency_and_releases_slots():
    scheduler = GpuScheduler(concurrency=99)
    assert scheduler.concurrency == 4
    assert scheduler.available_slots == 4

    async def exercise():
        async with scheduler.slot():
            assert scheduler.available_slots == 3
        assert scheduler.available_slots == 4

    import asyncio

    asyncio.run(exercise())


def test_stage_results_are_typed_but_keep_the_legacy_payload_shape():
    outcomes = StageResults({"ASR": "REUSED_V1", "ALIGNMENT": "SUCCEEDED"})
    reused = outcomes.result_for("asr")
    assert reused is not None
    assert reused.status is StageResultStatus.REUSED
    assert outcomes.as_legacy_dict() == {"ASR": "REUSED_V1", "ALIGNMENT": "SUCCEEDED"}

    result = ProcessingResult(
        job_id="job-1",
        language="ru",
        text="готово",
        segments=[],
        word_segments=[],
        metadata={},
        stage_outcomes=outcomes,
    )
    assert result.to_dict()["stage_outcomes"] == {"ASR": "REUSED_V1", "ALIGNMENT": "SUCCEEDED"}


def test_stage_result_rejects_unknown_status_and_drops_payload_metadata():
    with pytest.raises(ValueError, match="STAGE_RESULT_STATUS_UNKNOWN"):
        StageResult.from_values("ASR", "MAYBE")
    result = StageResult.from_values(
        "ASR",
        "SUCCEEDED",
        metadata={"duration_ms": 1200, "text": "секрет", "token": "секрет"},
    )
    assert result.metadata == {"duration_ms": 1200}


def test_local_checkpoint_store_is_atomic_scoped_and_validates_provenance(workspace_tmp_path):
    tmp_path = workspace_tmp_path
    store = LocalPipelineCheckpointStore(tmp_path)
    result = {"language": "ru", "segments": [{"start": 0.0, "end": 1.0, "text": "готово"}]}
    saved = store.save("job-1", "ALIGNMENT", "fingerprint-1", result)
    assert saved.artifact_key == "/data/processing-checkpoints/job-1/ALIGNMENT.json"
    assert not (tmp_path / "processing-checkpoints" / "job-1" / "ALIGNMENT.json.part").exists()
    loaded = store.load("job-1", "ALIGNMENT", "fingerprint-1")
    assert loaded is not None
    assert loaded.result == result
    assert loaded.artifact_sha256 == saved.artifact_sha256
    assert store.load("job-1", "ALIGNMENT", "different-source") is None
    with pytest.raises(ValueError, match="CHECKPOINT_JOB_ID_INVALID"):
        store.save("../job", "ALIGNMENT", "fingerprint-1", result)


def test_enrichment_reuses_alignment_and_diarization_checkpoints(workspace_tmp_path):
    tmp_path = workspace_tmp_path
    media = tmp_path / "canonical.wav"
    media.write_bytes(b"canonical-audio")
    audio_hash = hashlib.sha256(media.read_bytes()).hexdigest()
    preprocessing = {
        "asr_input_path_kind": "canonical",
        "preprocessing_mode": "auto",
        "preprocessing_applied": False,
        "preprocessing_profile": None,
        "acoustic_profile": "AUTO",
    }
    source = {
        "transcript_id": "transcript-1",
        "language": "ru",
        "segments": [{"start": 0.0, "end": 0.8, "text": "проверка", "speaker": "UNKNOWN"}],
        "word_segments": [],
        "quality_metadata": {
            "asr_storage_key": "/data/canonical.wav",
            "asr_audio_hash": audio_hash,
            "asr_sample_rate": 16000,
            "asr_channels": 1,
            "asr_duration_seconds": 1.0,
            "asr_preprocessing": preprocessing,
            "pipeline_metrics": {"media_prepare_ms": 345},
        },
    }

    class Config:
        language = "ru"
        enable_alignment = True
        enable_diarization = True
        asr_model = "large-v3"
        asr_backend = "faster-whisper"
        device = "cpu"
        compute_type = "int8"

    class Context:
        asr_audio_path = None
        asr_preprocessing = {}
        asr_result = None
        diar_audio_path = None
        aligned_result = None

        def register_temp(self, path):
            return path

    class Preprocessor:
        def prepare_asr_input(self, pipeline, input_path, acoustic_profile="AUTO"):
            return input_path, dict(preprocessing)

        def prepare_diarization_input(self, pipeline, input_path):
            return input_path

        def prepare_profile(self, pipeline, input_path, profile):
            return input_path

    class Alignment:
        def __init__(self):
            self.calls = 0

        def align(self, pipeline, context, result):
            self.calls += 1
            return {**result, "aligned": True}

    class Diarization:
        def __init__(self):
            self.calls = 0

        def diarize(self, pipeline, context, result, profile="diar"):
            self.calls += 1
            updated = {**result, "diarized": True}
            updated["segments"] = [{**item, "speaker": "SPEAKER_00"} for item in result["segments"]]
            return updated

    class Postprocessor:
        def postprocess(self, pipeline, context, result):
            return result

    request = ProcessingRequest(
        job_id="job-1",
        media_path=media,
        profile="enrich",
        input_transcript=source,
        source_storage_key="/data/canonical.wav",
        source_audio_hash=audio_hash,
    )
    store = LocalPipelineCheckpointStore(tmp_path / "checkpoints")
    first_alignment, first_diarization = Alignment(), Diarization()
    first = ProcessingService(
        preprocessing_engine=Preprocessor(),
        alignment_engine=first_alignment,
        diarization_engine=first_diarization,
        postprocessing_engine=Postprocessor(),
        checkpoint_store=store,
    )
    first_result = first._process_enrichment(request, object(), Context(), Config(), 1.0, lambda *_: None)
    assert first_alignment.calls == 1
    assert first_diarization.calls == 1
    assert first_result.segments[0]["speaker"] == "SPEAKER_00"
    assert first_result.metadata["pipeline_metrics"]["media_prepare_ms"] == 345.0

    second_alignment, second_diarization = Alignment(), Diarization()
    second = ProcessingService(
        preprocessing_engine=Preprocessor(),
        alignment_engine=second_alignment,
        diarization_engine=second_diarization,
        postprocessing_engine=Postprocessor(),
        checkpoint_store=store,
    )
    second_result = second._process_enrichment(request, object(), Context(), Config(), 1.0, lambda *_: None)
    assert second_alignment.calls == 0
    assert second_diarization.calls == 0
    assert second_result.segments == first_result.segments
    assert second_result.stage_outcomes.result_for("ALIGNMENT").metadata["reason"] == "checkpoint_reused"


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
