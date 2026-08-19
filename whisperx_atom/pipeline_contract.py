"""Shared domain contract for the durable meeting-processing pipeline.

The API, media worker and GPU worker currently persist a few historical stage
names (for example ``READY_FOR_ASR`` and ``TRANSCRIPT_ENRICH``).  This module is
the compatibility boundary between those names and the target state machine
described in the architecture documentation.  It deliberately contains no
database or NATS code, so it can be used by workers and contract tests alike.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import Callable


class PipelineStage(str, Enum):
    """Durable processing stages shared by media, ASR and summary jobs."""

    CREATED = "CREATED"
    UPLOADING = "UPLOADING"
    UPLOADED = "UPLOADED"
    QUEUED = "QUEUED"
    VALIDATING = "VALIDATING"
    NORMALIZING = "NORMALIZING"
    READY_FOR_ASR = "READY_FOR_ASR"
    TRANSCRIBING = "TRANSCRIBING"
    ALIGNING = "ALIGNING"
    DIARIZING = "DIARIZING"
    QUALITY_CHECK = "QUALITY_CHECK"
    POSTPROCESSING = "POSTPROCESSING"
    PERSISTING = "PERSISTING"
    ASR_READY = "ASR_READY"
    ENRICHING = "ENRICHING"
    ENRICHED_READY = "ENRICHED_READY"
    TRANSCRIPT_READY = "TRANSCRIPT_READY"
    SUMMARIZING = "SUMMARIZING"
    COMPLETED = "COMPLETED"
    RETRY_WAIT = "RETRY_WAIT"
    FAILED = "FAILED"
    CANCELLED = "CANCELLED"


# Historical names remain valid at the persistence boundary.  The value on
# the right is the stable domain representation; storage keeps its existing
# spelling until a separate additive migration is approved.
_STAGE_ALIASES: dict[str, PipelineStage] = {
    "RETRY_PENDING": PipelineStage.RETRY_WAIT,
    "MEDIA_RETRY_WAIT": PipelineStage.RETRY_WAIT,
    "WAITING_FOR_GPU": PipelineStage.RETRY_WAIT,
    "TRANSCRIPT_ENRICH": PipelineStage.ENRICHING,
    "ALIGNMENT_PARTIAL": PipelineStage.ALIGNING,
    "DIARIZATION_PARTIAL": PipelineStage.DIARIZING,
    "PREPARING_CONTEXT": PipelineStage.SUMMARIZING,
    "EXTRACTING_FACTS": PipelineStage.SUMMARIZING,
    "GROUPING_TOPICS": PipelineStage.SUMMARIZING,
    "RESOLVING_DECISIONS": PipelineStage.SUMMARIZING,
    "EXTRACTING_TASKS": PipelineStage.SUMMARIZING,
    "RESOLVING_DEADLINES": PipelineStage.SUMMARIZING,
    "VALIDATING_EVIDENCE": PipelineStage.SUMMARIZING,
    "DEDUPLICATING": PipelineStage.SUMMARIZING,
    "MERGING_FACTS": PipelineStage.SUMMARIZING,
    "RESOLVING_ENTITIES": PipelineStage.SUMMARIZING,
    "GENERATING_SUMMARY": PipelineStage.SUMMARIZING,
    "READY": PipelineStage.COMPLETED,
    "ENRICHED_READY": PipelineStage.ENRICHED_READY,
}


def normalize_stage(value: str | PipelineStage) -> PipelineStage:
    """Return the stable domain stage for a persisted or incoming value."""

    if isinstance(value, PipelineStage):
        return value
    normalized = str(value or "").strip().upper()
    if not normalized:
        raise ValueError("PIPELINE_STAGE_EMPTY")
    try:
        return PipelineStage(normalized)
    except ValueError:
        try:
            return _STAGE_ALIASES[normalized]
        except KeyError as exc:
            raise ValueError(f"PIPELINE_STAGE_UNKNOWN:{normalized}") from exc


def validate_stage_name(value: str | PipelineStage) -> str:
    """Validate a storage stage while preserving its backwards-compatible spelling.

    Workers still persist historical names such as ``RETRY_PENDING`` because
    existing Desktop/API clients consume them.  This function validates those
    names against the domain contract but returns the original upper-case
    spelling, allowing the migration to be additive instead of a flag day.
    """

    if isinstance(value, PipelineStage):
        return value.value
    normalized = str(value or "").strip().upper()
    normalize_stage(normalized)
    return normalized


def is_terminal_stage(value: str | PipelineStage) -> bool:
    return normalize_stage(value) in {
        PipelineStage.ASR_READY,
        PipelineStage.ENRICHED_READY,
        PipelineStage.TRANSCRIPT_READY,
        PipelineStage.COMPLETED,
        PipelineStage.FAILED,
        PipelineStage.CANCELLED,
    }


def is_retryable_stage(value: str | PipelineStage) -> bool:
    return normalize_stage(value) == PipelineStage.RETRY_WAIT


# This graph is intentionally additive.  ASR V1 and enrichment are separate
# durable jobs, so ASR_READY is a terminal stage for the ASR job and a valid
# input for a later ENRICHING job.  Repeated writes of the same stage are
# idempotent and therefore always accepted.
_ALLOWED_TRANSITIONS: dict[PipelineStage, frozenset[PipelineStage]] = {
    PipelineStage.CREATED: frozenset({PipelineStage.UPLOADING, PipelineStage.QUEUED, PipelineStage.CANCELLED}),
    PipelineStage.UPLOADING: frozenset({PipelineStage.UPLOADED, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.UPLOADED: frozenset({PipelineStage.QUEUED, PipelineStage.VALIDATING, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.QUEUED: frozenset({PipelineStage.VALIDATING, PipelineStage.NORMALIZING, PipelineStage.TRANSCRIBING, PipelineStage.ENRICHING, PipelineStage.SUMMARIZING, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.VALIDATING: frozenset({PipelineStage.NORMALIZING, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.NORMALIZING: frozenset({PipelineStage.READY_FOR_ASR, PipelineStage.TRANSCRIBING, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.READY_FOR_ASR: frozenset({PipelineStage.TRANSCRIBING, PipelineStage.ENRICHING, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.TRANSCRIBING: frozenset({PipelineStage.ALIGNING, PipelineStage.QUALITY_CHECK, PipelineStage.PERSISTING, PipelineStage.ASR_READY, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.ALIGNING: frozenset({PipelineStage.DIARIZING, PipelineStage.QUALITY_CHECK, PipelineStage.POSTPROCESSING, PipelineStage.PERSISTING, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.DIARIZING: frozenset({PipelineStage.QUALITY_CHECK, PipelineStage.POSTPROCESSING, PipelineStage.PERSISTING, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.QUALITY_CHECK: frozenset({PipelineStage.POSTPROCESSING, PipelineStage.PERSISTING, PipelineStage.ASR_READY, PipelineStage.ENRICHED_READY, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.POSTPROCESSING: frozenset({PipelineStage.PERSISTING, PipelineStage.ENRICHED_READY, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.PERSISTING: frozenset({PipelineStage.ASR_READY, PipelineStage.ENRICHED_READY, PipelineStage.TRANSCRIPT_READY, PipelineStage.COMPLETED, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.ASR_READY: frozenset({PipelineStage.ENRICHING, PipelineStage.COMPLETED, PipelineStage.CANCELLED}),
    PipelineStage.ENRICHING: frozenset({PipelineStage.ALIGNING, PipelineStage.DIARIZING, PipelineStage.QUALITY_CHECK, PipelineStage.POSTPROCESSING, PipelineStage.PERSISTING, PipelineStage.ENRICHED_READY, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.ENRICHED_READY: frozenset({PipelineStage.SUMMARIZING, PipelineStage.COMPLETED, PipelineStage.CANCELLED}),
    PipelineStage.TRANSCRIPT_READY: frozenset({PipelineStage.SUMMARIZING, PipelineStage.COMPLETED, PipelineStage.CANCELLED}),
    PipelineStage.SUMMARIZING: frozenset({PipelineStage.COMPLETED, PipelineStage.RETRY_WAIT, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.RETRY_WAIT: frozenset({PipelineStage.QUEUED, PipelineStage.VALIDATING, PipelineStage.NORMALIZING, PipelineStage.TRANSCRIBING, PipelineStage.ENRICHING, PipelineStage.SUMMARIZING, PipelineStage.FAILED, PipelineStage.CANCELLED}),
    PipelineStage.COMPLETED: frozenset(),
    PipelineStage.FAILED: frozenset({PipelineStage.RETRY_WAIT, PipelineStage.CANCELLED}),
    PipelineStage.CANCELLED: frozenset(),
}


def can_transition(current: str | PipelineStage, target: str | PipelineStage) -> bool:
    """Return whether a durable stage transition is safe and repeatable."""

    current_stage = normalize_stage(current)
    target_stage = normalize_stage(target)
    return current_stage == target_stage or target_stage in _ALLOWED_TRANSITIONS[current_stage]


def require_transition(current: str | PipelineStage, target: str | PipelineStage) -> PipelineStage:
    """Validate a transition and return the normalized target stage."""

    target_stage = normalize_stage(target)
    if not can_transition(current, target_stage):
        raise ValueError(
            f"PIPELINE_INVALID_TRANSITION:{normalize_stage(current).value}->{target_stage.value}"
        )
    return target_stage


@dataclass(frozen=True)
class PipelineJob:
    """Small domain projection used at worker boundaries.

    It intentionally contains identifiers and lifecycle data only.  Audio,
    transcript text, credentials and model payloads must stay in their own
    stores and never be copied into job diagnostics.
    """

    job_id: str
    meeting_id: str
    job_type: str
    stage: PipelineStage
    status: str
    progress: int = 0
    attempt: int = 0
    correlation_id: str | None = None

    @classmethod
    def from_values(cls, *, job_id: str, meeting_id: str, job_type: str, stage: str | PipelineStage, status: str, progress: int = 0, attempt: int = 0, correlation_id: str | None = None) -> "PipelineJob":
        return cls(
            job_id=str(job_id),
            meeting_id=str(meeting_id),
            job_type=str(job_type),
            stage=normalize_stage(stage),
            status=str(status),
            progress=max(0, min(100, int(progress))),
            attempt=max(0, int(attempt)),
            correlation_id=str(correlation_id) if correlation_id else None,
        )


class PipelineStageTracker:
    """Request-scoped guard for progress callbacks emitted by Core Pipeline."""

    def __init__(self, callback: Callable[[str, int], None] | None) -> None:
        self._callback = callback
        self.current: PipelineStage | None = None

    def emit(self, stage: str | PipelineStage, progress: int) -> None:
        target = normalize_stage(stage)
        if self.current is not None and not can_transition(self.current, target):
            raise ValueError(f"PIPELINE_INVALID_TRANSITION:{self.current.value}->{target.value}")
        self.current = target
        if self._callback is not None:
            # Keep the original storage spelling for existing Desktop stages.
            self._callback(validate_stage_name(stage), max(0, min(100, int(progress))))
