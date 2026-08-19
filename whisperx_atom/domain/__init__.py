"""Small, dependency-free domain projections.

The runtime still stores identifiers as strings for IPC and PostgreSQL
compatibility.  This package gives application code a typed boundary without
moving persistence or model code into the domain layer.
"""

from .ids import DomainId, JobId, MeetingId, RecordingId, TranscriptId
from .models import MeetingRef, ProcessingJobRef, RecordingRef, TranscriptRef, require_meeting_id

__all__ = [
    "DomainId",
    "MeetingId",
    "RecordingId",
    "TranscriptId",
    "JobId",
    "MeetingRef",
    "RecordingRef",
    "TranscriptRef",
    "ProcessingJobRef",
    "require_meeting_id",
]
