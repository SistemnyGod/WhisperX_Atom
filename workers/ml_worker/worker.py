from __future__ import annotations

import asyncio
import json
import logging
import os
import time
from dataclasses import replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from workers.nats_utils import ensure_stream, fetch_available, maintain_message
from whisperx_atom.contracts import ProcessingRequest
from whisperx_atom.core_pipeline import CorePipeline, WhisperXCorePipeline
from whisperx_atom.domain import require_meeting_id
from whisperx_atom.storage import LocalMediaStorage
from workers.gpu_lease import PostgresGpuLease
from whisperx_atom.gpu_scheduler import GpuScheduler
from whisperx_atom.checkpoint_store import checkpoint_store_from_env
from workers.runtime_heartbeat import AsyncHeartbeat
from workers.gpu_runtime_coordination import GpuRuntimeCoordinator
from .persistence import JobRepository


MESSAGE_ALREADY_CLAIMED_DELAY_SECONDS = max(5.0, float(os.getenv("GPU_DUPLICATE_NAK_DELAY_SECONDS", "15")))

class ResidentLlmConflict(RuntimeError):
    pass


class RetryScheduled(RuntimeError):
    def __init__(self, delay_seconds: float):
        super().__init__("GPU_JOB_RETRY_SCHEDULED")
        self.delay_seconds = delay_seconds


class MessageAlreadyClaimed(RuntimeError):
    """A different live worker owns the inbox lease.

    This is expected during redelivery races, not a worker failure.  The
    consumer uses the delay to let the lease expire instead of immediately
    NAKing the same message in a tight loop.
    """

    def __init__(self, delay_seconds: float = MESSAGE_ALREADY_CLAIMED_DELAY_SECONDS):
        super().__init__("message_claimed_by_active_worker")
        self.delay_seconds = max(5.0, float(delay_seconds))


class WorkerProcessRestartRequested(RuntimeError):
    """The durable job was requeued and this process must be recycled."""


def _duration_seconds(message: dict[str, Any], input_transcript: dict[str, Any] | None = None) -> float | None:
    """Resolve canonical duration with a rolling-upgrade metadata fallback.

    New media/enrichment messages carry ``duration_ms`` directly. Older
    outbox payloads may not, so use the trusted V1 quality metadata before
    falling back to the conservative watchdog limits.
    """
    candidates: list[tuple[Any, bool]] = []
    for key in ("audio_duration_ms", "duration_ms"):
        candidates.append((message.get(key), False))
    quality = (input_transcript or {}).get("quality_metadata") or {}
    for key in ("audio_duration_ms", "duration_ms"):
        candidates.append((quality.get(key), False))
    candidates.append((quality.get("asr_duration_seconds"), True))
    for value, is_seconds in candidates:
        try:
            value = float(value)
            if value > 0:
                return value if is_seconds else value / 1000.0
        except (TypeError, ValueError):
            pass
    return None


def _stage_timeout_seconds(stage: str, duration_seconds: float | None) -> float:
    duration = duration_seconds or 0.0
    normalized = (stage or "").upper()
    if normalized in {
        "PREPARING", "PREPROCESSING", "VALIDATING", "NORMALIZING", "READY_FOR_ASR",
        "QUALITY_CHECK", "POSTPROCESSING", "PERSISTING", "VALIDATING_EVIDENCE",
    }:
        return 20 * 60
    if normalized in {"ALIGNING"}:
        return max(30 * 60, duration * 0.75) if duration else 30 * 60
    if normalized in {"DIARIZING"}:
        return max(45 * 60, duration) if duration else 45 * 60
    if normalized in {"TRANSCRIBING", "ASR", "ASR_RUNNING"}:
        return max(45 * 60, duration * 1.5) if duration else 2 * 60 * 60
    return 2 * 60 * 60


def _job_timeout_seconds(duration_seconds: float | None) -> float:
    duration = duration_seconds or 0.0
    configured = min(8 * 60 * 60, max(3 * 60 * 60, float(os.getenv("GPU_JOB_MAX_RUNTIME_SECONDS", "28800"))))
    derived = max(3 * 60 * 60, duration * 3) if duration else 3 * 60 * 60
    return min(configured, derived)


def _utc_age(value: Any) -> float:
    if value is None:
        return float("inf")
    if isinstance(value, datetime):
        timestamp = value.replace(tzinfo=timezone.utc) if value.tzinfo is None else value
        return max(0.0, (datetime.now(timezone.utc) - timestamp).total_seconds())
    return float("inf")


async def resident_llm_detected() -> bool:
    if os.getenv("GPU_ENFORCE_NO_RESIDENT_LLM", "true").lower() not in {"1", "true", "yes"}:
        return False
    try:
        _, writer = await asyncio.wait_for(asyncio.open_connection(os.getenv("LLM_HEALTH_HOST", "llama-server"), int(os.getenv("LLM_HEALTH_PORT", "8080"))), timeout=1.0)
        writer.close()
        await writer.wait_closed()
        return True
    except (OSError, asyncio.TimeoutError):
        return False
LOGGER = logging.getLogger("whisperx.gpu-worker")
RESIDENT_LLM_RETRY_DELAY_SECONDS = max(5, int(os.getenv("GPU_RESIDENT_LLM_RETRY_DELAY_SECONDS", "15")))


def _optional_non_negative_float(value: Any) -> float | None:
    """Parse diagnostic durations without allowing malformed payloads through."""

    try:
        parsed = float(value)
    except (TypeError, ValueError):
        return None
    return parsed if parsed >= 0 else None


def resolve_storage_path(storage_key: str) -> Path:
    """Translate a storage key below `/data` to the worker's local mount."""
    host_root = os.getenv("WHISPERX_DATA_HOST", "").strip() if os.name == "nt" else "/data"
    return LocalMediaStorage(host_root).resolve(storage_key)


def error_code_for(exc: Exception) -> str:
    text = f"{type(exc).__name__}: {exc}".lower()
    # Waiting behind a higher-priority workload is normal scheduling, not a
    # terminal GPU failure.  Keep this distinct so V2 can be durably requeued
    # without discarding an already available V1 transcript.
    if "gpu_lease_priority_timeout" in text:
        return "GPU_PRIORITY_TIMEOUT"
    if "asr_input_mismatch" in text:
        return "ASR_INPUT_MISMATCH"
    if "no speech" in text or "no_speech_detected" in text:
        return "NO_SPEECH_DETECTED"
    # A decodable recording that produces no segments is a valid no-speech
    # outcome. Keep the public error stable even when the quality gate raises
    # its internal TRANSCRIPT_EMPTY reason.
    if "transcript_empty" in text:
        return "NO_SPEECH_DETECTED"
    if "invalid_timecode" in text:
        return "TRANSCRIPT_INVALID_TIMECODE"
    if "transcript_outside_media" in text:
        return "TRANSCRIPT_OUTSIDE_MEDIA"
    if "cuda" in text and ("out of memory" in text or "oom" in text):
        return "CUDA_OOM"
    if "cuda" in text and ("unavailable" in text or "required" in text or "not available" in text):
        return "CUDA_UNAVAILABLE"
    if isinstance(exc, FileNotFoundError):
        return "MEDIA_NOT_FOUND"
    if any(token in text for token in (
        "hf_token", "huggingface", "hf_hub_offline", "gated", "pyannote",
        "no module named 'pyannote", "cannot import name 'pyannote",
    )):
        return "MODEL_ACCESS_ERROR"
    if "no_audio" in text or "no audio" in text:
        return "MEDIA_NO_AUDIO"
    if "invalid data" in text or "moov atom not found" in text or "could not find codec parameters" in text:
        return "MEDIA_INVALID"
    if "ffmpeg" in text or "audio" in text:
        return "AUDIO_PROCESSING_ERROR"
    return "GPU_PROCESSING_FAILED"


def is_retryable_error_code(code: str) -> bool:
    # CUDA_OOM is retryable at the job level after the worker has already
    # cleared its resident pipeline and attempted one local retry.  Keeping
    # the durable source asset and requeueing avoids forcing a new recording
    # when memory pressure is transient (or another GPU workload releases
    # memory after this process yields).
    return code in {"MEDIA_NOT_FOUND", "CUDA_UNAVAILABLE", "CUDA_OOM", "AUDIO_PROCESSING_ERROR"}


def retry_delay_seconds(attempt: int) -> float:
    base = {1: 5, 2: 15, 3: 30, 4: 60, 5: 120}.get(attempt, 300)
    return base + (attempt * 0.37)


def _speaker_limit(message: dict[str, Any], key: str, env_name: str, fallback: int) -> int:
    raw = message.get(key)
    if raw is None or str(raw).strip() == "":
        raw = os.getenv(env_name, str(fallback))
    try:
        return max(1, int(raw))
    except (TypeError, ValueError):
        return fallback



class GpuWorker:
    def __init__(self, heartbeat: AsyncHeartbeat | None = None) -> None:
        self._scheduler = GpuScheduler()
        # All server-side ASR/enrichment requests enter through this facade.
        # The underlying ProcessingService remains the compatibility
        # implementation while stages are extracted incrementally.
        self._pipeline: CorePipeline = WhisperXCorePipeline(
            checkpoint_store=checkpoint_store_from_env(),
        )
        self._repository = JobRepository()
        # ASR has precedence over optional enrichment.  Both leases use the
        # same advisory-lock key, so this changes only queue ordering and
        # never permits concurrent CUDA inference.
        self._asr_gpu_lease = PostgresGpuLease(self._repository.conninfo, priority=10)
        self._enrichment_gpu_lease = PostgresGpuLease(self._repository.conninfo, priority=50)
        self._gpu_coordination = GpuRuntimeCoordinator(self._repository.conninfo)
        self._heartbeat = heartbeat

    def close(self) -> None:
        self._pipeline.close()
        self._repository.close()

    async def _watch_progress(self, job_id: str, message_id: str, started_at: float, duration_seconds: float | None) -> None:
        """Detect a live process whose CUDA/thread call stopped advancing."""
        while True:
            await asyncio.sleep(10)
            if time.monotonic() - started_at >= _job_timeout_seconds(duration_seconds):
                outcome = await asyncio.to_thread(self._repository.requeue_or_fail_stage_timeout, job_id, message_id)
                if outcome:
                    LOGGER.error("job=%s exceeded overall GPU runtime outcome=%s", job_id, outcome)
                    raise WorkerProcessRestartRequested(outcome)
                return
            liveness = await asyncio.to_thread(self._repository.job_progress_liveness, job_id)
            if liveness is None:
                return
            stage, _progress, stage_changed_at, progress_changed_at = liveness
            # Either a stage transition or a numeric progress update proves
            # that the pipeline is alive.  Use the newest timestamp rather
            # than the oldest one; max() would timeout a job whose progress
            # keeps advancing inside a long-running stage.
            age = min(_utc_age(stage_changed_at), _utc_age(progress_changed_at))
            if age >= _stage_timeout_seconds(stage, duration_seconds):
                outcome = await asyncio.to_thread(self._repository.requeue_or_fail_stage_timeout, job_id, message_id)
                if outcome:
                    LOGGER.error("job=%s stage=%s made no progress age=%.1fs outcome=%s", job_id, stage, age, outcome)
                    raise WorkerProcessRestartRequested(outcome)
                return

    async def handle(self, message: dict[str, Any]) -> dict[str, Any] | None:
        queued_at = time.perf_counter()
        async with self._scheduler.slot():
            job_id = str(message["job_id"])
            meeting_id = str(require_meeting_id(message))
            if self._heartbeat:
                self._heartbeat.set_job(job_id)
                self._heartbeat.set_state("BUSY")
            LOGGER.info("received job=%s message=%s stage=%s", job_id, message.get("message_id"), message.get("stage"))
            claim = self._repository.claim_message_details(str(message.get("message_id", "")), job_id)
            if claim.status == "OWNED_BY_OTHER_WORKER":
                state = self._repository.job_state(job_id)
                if self._heartbeat:
                    self._heartbeat.set_job(None)
                    self._heartbeat.set_state("DEGRADED", "GPU_JOB_OWNERSHIP_CONFLICT")
                if state is not None and state[0] not in ("READY", "FAILED", "CANCELLED"):
                    delay = MESSAGE_ALREADY_CLAIMED_DELAY_SECONDS
                    if claim.lease_expires_at is not None:
                        remaining = (claim.lease_expires_at - datetime.now(claim.lease_expires_at.tzinfo)).total_seconds()
                        delay = max(MESSAGE_ALREADY_CLAIMED_DELAY_SECONDS, min(60.0, remaining + 0.5))
                    raise MessageAlreadyClaimed(delay)
                if self._heartbeat:
                    self._heartbeat.set_state("READY")
                return None
            state = self._repository.job_state(job_id)
            if state is not None and state[0] in ("READY", "FAILED", "CANCELLED"):
                LOGGER.info("skip terminal job=%s status=%s", job_id, state[0])
                self._repository.release_message(str(message.get("message_id", "")))
                if self._heartbeat:
                    self._heartbeat.set_job(None)
                    self._heartbeat.set_state("READY")
                return None
            job_type = self._repository.job_type(job_id) or str(message.get("job_type") or "TRANSCRIBE")
            asr_only_job = job_type == "TRANSCRIBE_ASR"
            enrichment_job = job_type == "TRANSCRIPT_ENRICH"
            self._repository.update_job(job_id, "RUNNING", "TRANSCRIBING", 20)
            request_profile = "asr" if asr_only_job else ("enrich" if enrichment_job else message.get("profile", "meeting"))
            input_transcript = None
            if enrichment_job:
                transcript_id = message.get("transcript_id") or await asyncio.to_thread(self._repository.input_transcript_id, job_id)
                input_transcript = await asyncio.to_thread(self._repository.load_transcript_source, transcript_id)
                if not input_transcript or not input_transcript.get("segments"):
                    raise RuntimeError("TRANSCRIPT_INPUT_NOT_FOUND")
            source_quality = (input_transcript or {}).get("quality_metadata") or {}
            normalized_language = str(message.get("language") or source_quality.get("language") or "ru").strip() or "ru"
            technical_intervals = tuple(await asyncio.to_thread(self._repository.technical_intervals, meeting_id)) if asr_only_job else ()
            request = ProcessingRequest(
                job_id=job_id,
                media_path=resolve_storage_path(str(message["storage_key"])),
                language=normalized_language,
                profile=request_profile,
                # Older outbox payloads omit speaker limits.  Keep their
                # compatibility shape, but let the deployment cap typical
                # meetings through DIARIZATION_* without editing every job.
                min_speakers=_speaker_limit(message, "min_speakers", "DIARIZATION_MIN_SPEAKERS", 1),
                max_speakers=_speaker_limit(message, "max_speakers", "DIARIZATION_MAX_SPEAKERS", 8),
                input_transcript=input_transcript,
                source_storage_key=str(message.get("storage_key") or "") or None,
                source_audio_hash=str(source_quality.get("asr_audio_hash") or "") or None,
                acoustic_profile=str(message.get("acousticProfile") or message.get("acoustic_profile") or "AUTO").upper(),
                technical_intervals=technical_intervals,
                media_prepare_ms=_optional_non_negative_float(message.get("media_prepare_ms")),
            )

            def progress(stage: str, value: int) -> None:
                LOGGER.info("job=%s stage=%s progress=%s", job_id, stage, value)
                self._repository.update_job(job_id, "RUNNING", stage, value)

            failure_code: str | None = None
            coordination_request_id = f"{job_id}:{message.get('message_id', '') or 'delivery'}"
            coordination_requested = False
            workload_type = "V2_ENRICH" if enrichment_job else "V1_ASR"
            workload_priority = 50 if enrichment_job else 10
            draft_holder: dict[str, str | None] = {"id": None}

            def persist_asr_draft(draft: dict[str, Any]) -> None:
                draft_holder["id"] = self._repository.persist_asr_draft(job_id, str(message["meeting_id"]), draft)

            try:
                # V1 asks resident Qwen to release memory before acquiring the
                # lease. V2 first passes the priority gate and requests only
                # after it owns the lease, so it cannot jump ahead of Assistant.
                if not enrichment_job:
                    coordination_requested = await asyncio.to_thread(
                        self._gpu_coordination.request_workload,
                        coordination_request_id,
                        workload_type,
                        workload_priority,
                        "gpu-worker",
                    )
                    if coordination_requested and not await asyncio.to_thread(
                        self._gpu_coordination.wait_for_llm_release,
                        coordination_request_id,
                        float(os.getenv("GPU_LLM_PREEMPT_TIMEOUT_SECONDS", "120")),
                    ):
                        raise ResidentLlmConflict("resident_llama_release_timeout")
                if not enrichment_job and await resident_llm_detected():
                    raise ResidentLlmConflict("resident_llama_server_must_be_stopped_before_transcription")
                LOGGER.info("job=%s waiting for GPU lease path=%s", job_id, request.media_path)
                gpu_lease = self._enrichment_gpu_lease if enrichment_job else self._asr_gpu_lease
                async with gpu_lease:
                    LOGGER.info("job=%s acquired GPU lease", job_id)
                    await asyncio.to_thread(self._repository.record_pipeline_event_for_job, job_id, "GPU_CLAIMED")
                    if enrichment_job:
                        coordination_requested = await asyncio.to_thread(
                            self._gpu_coordination.request_workload,
                            coordination_request_id,
                            workload_type,
                            workload_priority,
                            "gpu-worker",
                        )
                        if not coordination_requested and await asyncio.to_thread(self._gpu_coordination.asr_request_active):
                            raise ResidentLlmConflict("higher_priority_gpu_workload")
                        if coordination_requested and not await asyncio.to_thread(
                            self._gpu_coordination.wait_for_llm_release,
                            coordination_request_id,
                            float(os.getenv("GPU_LLM_PREEMPT_TIMEOUT_SECONDS", "120")),
                        ):
                            raise ResidentLlmConflict("resident_llama_release_timeout")
                    # Include both bounded local scheduler wait and the
                    # cross-process PostgreSQL GPU lease wait in diagnostics.
                    request = replace(request, queue_wait_ms=(time.perf_counter() - queued_at) * 1000.0)
                    oom_attempt = 0
                    while True:
                        try:
                            draft_callback = None if enrichment_job else persist_asr_draft
                            processing_task = asyncio.create_task(
                                asyncio.to_thread(self._pipeline.process, request, progress, draft_callback)
                            )
                            watchdog_task = asyncio.create_task(
                                self._watch_progress(
                                    job_id,
                                    str(message.get("message_id", "")),
                                    time.monotonic(),
                                    _duration_seconds(message, input_transcript),
                                )
                            )
                            done, _pending = await asyncio.wait(
                                {processing_task, watchdog_task},
                                return_when=asyncio.FIRST_COMPLETED,
                            )
                            if watchdog_task in done:
                                # A timed-out to_thread cannot be cancelled safely;
                                # the caller will recycle this process immediately
                                # after the durable requeue transaction commits.
                                await watchdog_task
                                raise WorkerProcessRestartRequested("GPU_STAGE_TIMEOUT")
                            watchdog_task.cancel()
                            await asyncio.gather(watchdog_task, return_exceptions=True)
                            result = await processing_task
                            break
                        except Exception as exc:
                            if error_code_for(exc) != "CUDA_OOM" or oom_attempt >= 1:
                                raise
                            oom_attempt += 1
                            LOGGER.warning("job=%s CUDA_OOM; cleared resident pipeline and retrying once", job_id)
                LOGGER.info("job=%s released GPU lease", job_id)
                payload = result.to_dict()
                payload["correlation_id"] = message.get("correlation_id") or payload.get("correlation_id")
                payload["meeting_id"] = meeting_id
                payload["processing_job_id"] = job_id
                pipeline_metrics = payload.get("metadata", {}).get("pipeline_metrics") if isinstance(payload.get("metadata"), dict) else None
                try:
                    await asyncio.to_thread(
                        self._repository.record_pipeline_metrics,
                        job_id,
                        meeting_id,
                        pipeline_metrics,
                        "enrichment_" if enrichment_job else "",
                    )
                except Exception:
                    # Diagnostics must never turn a valid V1/V2 result into a
                    # failed job when an older database has no timing column
                    # or the telemetry write is temporarily unavailable.
                    LOGGER.warning("pipeline_metrics_persist_failed job=%s", job_id, exc_info=True)
                if asr_only_job:
                    # The callback persisted PARTIAL_READY V1 immediately
                    # after ASR. Closing this job must not create a second
                    # transcript version; enrichment is a separate job.
                    if not draft_holder["id"]:
                        draft_holder["id"] = self._repository.persist_asr_draft(job_id, meeting_id, payload)
                    await asyncio.to_thread(self._repository.complete_asr_job, job_id, meeting_id)
                    payload["transcript_id"] = draft_holder["id"]
                    payload["version_kind"] = "ASR_DRAFT"
                    LOGGER.info("ASR draft ready transcript=%s meeting_id=%s job_id=%s", draft_holder["id"], payload["meeting_id"], job_id)
                    return payload
                if draft_holder["id"]:
                    payload["source_transcript_id"] = draft_holder["id"]
                    payload["version_kind"] = "ENRICHED"
                elif enrichment_job:
                    payload["source_transcript_id"] = message.get("transcript_id") or self._repository.input_transcript_id(job_id)
                    payload["version_kind"] = "ENRICHED"
                LOGGER.info("transcript result correlation_id=%s meeting_id=%s job_id=%s", payload.get("correlation_id"), payload["meeting_id"], job_id)
                persisted = await asyncio.to_thread(self._repository.persist_result, job_id, meeting_id, payload)
                if not persisted:
                    LOGGER.info("job=%s result discarded because the meeting was cancelled or deleted", job_id)
                    return None
                LOGGER.info("job=%s persisted segments=%s words=%s", job_id, len(payload.get("segments", [])), len(payload.get("word_segments", [])))
                return payload
            except ResidentLlmConflict as exc:
                LOGGER.warning("job=%s waiting: %s", job_id, exc)
                failure_code = "GPU_RESIDENT_LLM_CONFLICT"
                self._repository.release_message(str(message.get("message_id", "")))
                self._repository.update_job(job_id, "QUEUED", "WAITING_FOR_GPU", 0, str(exc), "GPU_RESIDENT_LLM_CONFLICT")
                if self._heartbeat:
                    self._heartbeat.set_state("READY", "GPU_RESIDENT_LLM_CONFLICT")
                raise
            except WorkerProcessRestartRequested:
                raise
            except Exception as exc:
                LOGGER.exception("job=%s failed", job_id)
                failure_code = error_code_for(exc)
                if failure_code == "GPU_PRIORITY_TIMEOUT" and enrichment_job:
                    scheduled_attempt = self._repository.schedule_retry(
                        job_id,
                        "V2 waited for a higher-priority GPU workload",
                        "GPU_PRIORITY_WAIT_RETRY_PENDING",
                        message_id=str(message.get("message_id", "")),
                        max_attempts=int(os.getenv("GPU_PRIORITY_RETRY_MAX_ATTEMPTS", "5")),
                    )
                    if scheduled_attempt is not None:
                        failure_code = "GPU_PRIORITY_WAIT_RETRY_PENDING"
                        if self._heartbeat:
                            self._heartbeat.set_state("READY", failure_code)
                        raise RetryScheduled(retry_delay_seconds(scheduled_attempt)) from exc
                if is_retryable_error_code(failure_code):
                    scheduled_attempt = self._repository.schedule_retry(
                        job_id,
                        type(exc).__name__ + ": " + str(exc),
                        failure_code + "_RETRY_PENDING",
                        message_id=str(message.get("message_id", "")),
                    )
                    if scheduled_attempt is not None:
                        failure_code = failure_code + "_RETRY_PENDING"
                        if self._heartbeat:
                            self._heartbeat.set_state("READY", failure_code)
                        raise RetryScheduled(retry_delay_seconds(scheduled_attempt)) from exc
                self._repository.release_message(str(message.get("message_id", "")))
                self._repository.update_job(job_id, "FAILED", "FAILED", 0, type(exc).__name__ + ": " + str(exc), failure_code)
                if self._heartbeat:
                    self._heartbeat.set_state("READY", failure_code)
                raise
            finally:
                if coordination_requested:
                    await asyncio.to_thread(self._gpu_coordination.clear_asr, coordination_request_id)
                if self._heartbeat:
                    self._heartbeat.set_job(None)
                    self._heartbeat.set_state("READY", failure_code)


async def run() -> None:
    logging.basicConfig(
        level=getattr(logging, os.getenv("LOG_LEVEL", "INFO").upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/ml_worker/requirements.txt") from exc

    async def gpu_capabilities() -> dict[str, Any]:
        capabilities: dict[str, Any] = {
            "cudaAvailable": False,
            "hfConfigured": bool(os.getenv("HF_TOKEN")),
            "runtime": os.getenv("GPU_WORKER_RUNTIME", "container"),
            # Publish the exact model contract consumed by the pipeline.  The
            # supervisor/Doctor can now compare runtime configuration with the
            # release manifest instead of trusting only image identity.
            "asrModelRepository": os.getenv("WHISPERX_MODEL_REPOSITORY", "Systran/faster-whisper-large-v3"),
            "asrModelRevision": os.getenv("WHISPERX_MODEL_REVISION", ""),
            "asrModelPath": os.getenv("WHISPERX_MODEL_PATH", ""),
            "asrModelSha256": os.getenv("WHISPERX_MODEL_SHA256", ""),
            "diarizationModel": os.getenv("DIARIZATION_MODEL", "pyannote/speaker-diarization-3.1"),
            "diarizationModelRevision": os.getenv("DIARIZATION_MODEL_REVISION", ""),
            "diarizationModelPath": os.getenv("DIARIZATION_MODEL_PATH", ""),
            "diarizationModelSha256": os.getenv("DIARIZATION_MODEL_SHA256", ""),
        }
        try:
            import torch

            capabilities["cudaAvailable"] = bool(torch.cuda.is_available())
            if capabilities["cudaAvailable"]:
                capabilities["cudaDevice"] = torch.cuda.get_device_name(0)
        except Exception as exc:
            capabilities["cudaError"] = type(exc).__name__
        diarization_enabled = os.getenv("ENABLE_DIARIZATION", "true").lower() in {"1", "true", "yes"}
        diarization_mode = os.getenv("DIARIZATION_MODE", "preferred").lower()
        if not diarization_enabled or diarization_mode == "disabled":
            capabilities["diarization"] = "DISABLED"
            capabilities["diarizationReason"] = "diarization_disabled"
        elif not capabilities["hfConfigured"]:
            capabilities["diarization"] = "DEGRADED"
            capabilities["diarizationReason"] = "hf_token_missing"
        else:
            # A token only proves that authentication is configured.  Probe the
            # same WhisperX/pyannote constructor used by the enrichment stage so
            # readiness cannot advertise diarization when the gated model or
            # its cache is unavailable.
            try:
                from whisperx.diarize import DiarizationPipeline

                device = os.getenv("DIARIZATION_DEVICE", "auto").strip().lower() or "auto"
                if device not in {"auto", "cpu", "cuda"}:
                    device = "auto"
                if device == "cuda" and not capabilities.get("cudaAvailable"):
                    capabilities["diarization"] = "DEGRADED"
                    capabilities["diarizationReason"] = "diarization_cuda_unavailable"
                    return capabilities
                if device == "auto":
                    device = os.getenv("DEVICE", "cuda").strip().lower() or "cuda"
                    if device == "cuda" and not capabilities.get("cudaAvailable"):
                        device = "cpu"
                diarization_model = (
                    os.getenv("DIARIZATION_MODEL_PATH", "").strip()
                    or os.getenv("DIARIZATION_MODEL", "pyannote/speaker-diarization-3.1").strip()
                )
                probe = await asyncio.wait_for(
                    asyncio.to_thread(
                        DiarizationPipeline,
                        model_name=diarization_model,
                        use_auth_token=os.environ["HF_TOKEN"],
                        device=device,
                    ),
                    timeout=float(os.getenv("DIARIZATION_READINESS_TIMEOUT_SECONDS", "120")),
                )
                del probe
                capabilities["diarization"] = "READY"
                capabilities["diarizationReason"] = "pyannote_model_loaded"
            except asyncio.TimeoutError:
                capabilities["diarization"] = "DEGRADED"
                capabilities["diarizationReason"] = "pyannote_model_load_timeout"
            except Exception as exc:
                # Do not include exception text: model URLs and auth details
                # must never leak into heartbeat payloads or logs.
                LOGGER.warning("diarization_probe_failed error_type=%s", type(exc).__name__)
                capabilities["diarization"] = "DEGRADED"
                capabilities["diarizationReason"] = "pyannote_model_load_failed"
        return capabilities

    # Publish liveness before any network/model initialization.  A cold
    # pyannote load can legitimately take longer than the host watchdog
    # timeout; STARTING is a live process state, not a failed worker.
    startup_capabilities: dict[str, Any] = {
        "cudaAvailable": False,
        "hfConfigured": bool(os.getenv("HF_TOKEN")),
        "runtime": os.getenv("GPU_WORKER_RUNTIME", "container"),
        "natsConnected": False,
        "startupStage": "STARTING",
    }
    heartbeat = AsyncHeartbeat("gpu-worker", capabilities=lambda: dict(startup_capabilities))
    await heartbeat.start()

    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    # CUDA/model capability discovery is intentionally done once at startup.
    # It must not be repeated on every heartbeat tick while ASR is running.
    startup_capabilities.update({**await gpu_capabilities(), "natsConnected": True, "startupStage": "READY"})
    worker = GpuWorker(heartbeat)
    recovered = await asyncio.to_thread(worker._repository.reset_stale_leases)
    if recovered:
        LOGGER.warning("recovered stale GPU jobs count=%s", recovered)
    jetstream = client.jetstream()
    await ensure_stream(jetstream, name="WHISPERX", subjects=["media.ingest", "ml.transcribe", "llm.summarize", "llm.assistant", "memory.index"])
    subscription = await jetstream.pull_subscribe("ml.transcribe", durable="whisperx-gpu")
    heartbeat.set_state("READY")
    while True:
        # Keep large-v3 resident between jobs, but release it after the
        # configured idle window so another GPU workload can make progress.
        worker._pipeline.release_idle()
        for message in await fetch_available(subscription, nats.errors.TimeoutError):
            job_id: str | None = None
            try:
                payload = json.loads(message.data)
            except (json.JSONDecodeError, TypeError, UnicodeDecodeError):
                LOGGER.exception("gpu_poison_message_discarded")
                await message.ack()
                continue
            if not isinstance(payload, dict) or not str(payload.get("job_id", "")).strip():
                LOGGER.error("gpu_poison_message_discarded reason=job_id_missing")
                await message.ack()
                continue
            try:
                require_meeting_id(payload)
            except ValueError:
                LOGGER.error("gpu_poison_message_discarded reason=meeting_id_missing")
                await message.ack()
                continue
            try:
                job_id = str(payload.get("job_id", ""))
                message_id = str(payload.get("message_id", ""))
                async with maintain_message(message, on_tick=lambda: asyncio.to_thread(worker._repository.renew_lease, job_id, message_id)):
                    await worker.handle(payload)
                await message.ack()
            except WorkerProcessRestartRequested as exc:
                # The job was durably requeued/failed before this point. Do not
                # ACK or NAK the delivery; process exit lets JetStream redeliver
                # it after the container/host watchdog restarts this worker.
                LOGGER.critical("recycling GPU worker after watchdog outcome=%s", exc)
                os._exit(70)
            except ResidentLlmConflict:
                # The job is deliberately returned to QUEUED by handle(). A
                # delayed NAK prevents a resident llama-server from turning
                # the pull consumer into a tight redelivery loop.
                await message.nak(delay=RESIDENT_LLM_RETRY_DELAY_SECONDS)
            except RetryScheduled as exc:
                await message.nak(delay=exc.delay_seconds)
            except MessageAlreadyClaimed as exc:
                LOGGER.warning("gpu_message_claimed_by_other_worker job_id=%s retry_in=%.1fs", job_id, exc.delay_seconds)
                await message.nak(delay=exc.delay_seconds)
            except Exception:
                LOGGER.exception("gpu_message_failed job_id=%s", job_id)
                await message.nak()


if __name__ == "__main__":
    asyncio.run(run())
