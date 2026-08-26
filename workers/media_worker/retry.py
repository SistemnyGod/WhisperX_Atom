from __future__ import annotations

import errno
import subprocess
from dataclasses import dataclass


RETRY_DELAY_SECONDS = 10
MAX_MEDIA_ATTEMPTS = 1  # one retry after the initial processing attempt


@dataclass(frozen=True)
class MediaFailure:
    code: str
    transient: bool


def classify_media_failure(exc: BaseException) -> MediaFailure:
    """Map media failures to stable codes; never retry unknown failures."""
    message = str(exc).lower()
    if "checksum_mismatch" in message:
        return MediaFailure("RECORDING_CHUNK_CHECKSUM_MISMATCH", False)
    if "sequence_gap" in message or "sample_gap" in message or "timeline" in message:
        return MediaFailure("RECORDING_TIMELINE_INVALID", False)
    if "recording_track_duration_mismatch" in message or "audio_track_drift_high" in message:
        return MediaFailure("AUDIO_TRACK_DRIFT_HIGH", False)
    if isinstance(exc, FileNotFoundError) or "chunk_missing" in message or "recording_tracks_required" in message:
        return MediaFailure("RECORDING_CHUNK_MISSING", False)
    if "no audio" in message or "has_no_audio" in message:
        return MediaFailure("MEDIA_NO_AUDIO", False)
    if "unsupported" in message or "invalid media" in message:
        return MediaFailure("UNSUPPORTED_MEDIA_FORMAT", False)
    if "duration" in message or "max_media" in message or "too large" in message:
        return MediaFailure("MEDIA_POLICY_VIOLATION", False)
    if isinstance(exc, OSError):
        if exc.errno in {errno.EACCES, errno.EAGAIN, errno.EBUSY, errno.ESTALE, errno.ETIMEDOUT}:
            return MediaFailure("MEDIA_IO_TRANSIENT", True)
        return MediaFailure("MEDIA_STORAGE_UNAVAILABLE", True)
    if isinstance(exc, subprocess.SubprocessError):
        # A non-zero ffmpeg exit normally means invalid input. Only launch
        # failures caused by an OS-level transient are retried.
        cause = exc.__cause__
        if isinstance(cause, OSError) and cause.errno in {errno.EAGAIN, errno.EBUSY, errno.ETIMEDOUT}:
            return MediaFailure("MEDIA_FFMPEG_TRANSIENT", True)
        return MediaFailure("MEDIA_PROCESSING_FAILED", False)
    module = type(exc).__module__
    if module.startswith(("psycopg", "nats")):
        return MediaFailure("MEDIA_DEPENDENCY_TRANSIENT", True)
    return MediaFailure("MEDIA_PROCESSING_FAILED", False)


def should_retry(attempt: int, failure: MediaFailure) -> bool:
    return failure.transient and attempt < MAX_MEDIA_ATTEMPTS
