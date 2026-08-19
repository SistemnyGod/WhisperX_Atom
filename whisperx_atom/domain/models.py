"""Identifier-only projections for the durable Meeting graph.

These objects deliberately contain no transcript text, audio bytes, tokens or
database handles.  They are safe to pass across API/worker boundaries and
make the relationship explicit:

    Meeting -> Recording -> ProcessingJob -> Transcript
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from ..pipeline_contract import PipelineStage, normalize_stage
from .ids import JobId, MeetingId, RecordingId, TranscriptId


@dataclass(frozen=True)
class MeetingRef:
    meeting_id: MeetingId
    owner_user_id: str | None = None
    title: str | None = None

    @classmethod
    def from_payload(cls, payload: dict[str, Any]) -> "MeetingRef":
        return cls(
            meeting_id=MeetingId.parse(payload.get("meeting_id", payload.get("meetingId"))),
            owner_user_id=str(payload["owner_user_id"]) if payload.get("owner_user_id") else None,
            title=str(payload["title"]) if payload.get("title") else None,
        )


@dataclass(frozen=True)
class RecordingRef:
    recording_id: RecordingId
    meeting_id: MeetingId
    media_asset_id: str | None = None
    source_type: str = "recorder_session"


@dataclass(frozen=True)
class TranscriptRef:
    transcript_id: TranscriptId
    meeting_id: MeetingId
    version: int
    status: str


@dataclass(frozen=True)
class ProcessingJobRef:
    job_id: JobId
    meeting_id: MeetingId
    job_type: str
    stage: PipelineStage
    attempt: int = 0

    @classmethod
    def from_payload(cls, payload: dict[str, Any]) -> "ProcessingJobRef":
        try:
            attempt = max(0, int(payload.get("attempt", 0)))
        except (TypeError, ValueError):
            attempt = 0
        return cls(
            job_id=JobId.parse(payload.get("job_id", payload.get("jobId"))),
            meeting_id=MeetingId.parse(payload.get("meeting_id", payload.get("meetingId"))),
            job_type=str(payload.get("job_type") or payload.get("type") or "TRANSCRIBE"),
            stage=normalize_stage(str(payload.get("stage") or "CREATED")),
            attempt=attempt,
        )


def require_meeting_id(payload: dict[str, Any]) -> MeetingId:
    """Require the central meeting identity at every worker boundary."""

    return MeetingId.parse(payload.get("meeting_id", payload.get("meetingId")))
