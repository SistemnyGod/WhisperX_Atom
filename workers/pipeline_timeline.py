"""Durable, idempotent recording pipeline timeline helpers.

The timeline is stored in the existing recording_sessions.stage_timings JSONB
column. It is intentionally diagnostic-only: it never changes job state and
contains timestamps/durations, not audio, transcript text or prompts.
"""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Any


PIPELINE_TIMELINE_EVENTS = {
    "STOP",
    "LOCAL_READY",
    "FLAC_READY",
    "UPLOAD_START",
    "DELIVERY_CONFIRMED",
    "MEDIA_READY",
    "ASR_QUEUED",
    "GPU_CLAIMED",
    "V1_READY",
    "V2_READY",
    "SUMMARY_READY",
}

_DURATION_EDGES: dict[str, tuple[tuple[str, str], ...]] = {
    "LOCAL_READY": (("STOP", "stop_to_local_ready_ms"),),
    "FLAC_READY": (("LOCAL_READY", "local_ready_to_flac_ms"),),
    "UPLOAD_START": (("FLAC_READY", "flac_to_upload_started_ms"),),
    "DELIVERY_CONFIRMED": (("UPLOAD_START", "upload_to_delivery_confirmed_ms"),),
    "MEDIA_READY": (("DELIVERY_CONFIRMED", "delivery_to_media_ready_ms"),),
    "ASR_QUEUED": (("MEDIA_READY", "media_to_asr_queued_ms"),),
    "GPU_CLAIMED": (("ASR_QUEUED", "asr_queue_to_gpu_claimed_ms"),),
    "V1_READY": (
        ("GPU_CLAIMED", "gpu_claimed_to_v1_ms"),
        ("STOP", "stop_to_v1_ms"),
    ),
    "V2_READY": (
        ("V1_READY", "v1_to_v2_ms"),
        ("STOP", "stop_to_v2_ms"),
    ),
    "SUMMARY_READY": (
        ("V2_READY", "v2_to_summary_ms"),
        ("STOP", "stop_to_summary_ms"),
    ),
}


def _timestamp(value: datetime | None) -> str:
    current = value or datetime.now(timezone.utc)
    if current.tzinfo is None:
        current = current.replace(tzinfo=timezone.utc)
    return current.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def record_pipeline_event(connection: Any, session_id: str, event: str, at: datetime | None = None) -> None:
    """Record one timeline event and first-observation durations.

    The SQL only fills a missing event/duration. Retries and duplicate NATS
    deliveries therefore preserve the original timestamp and cannot create a
    second logical pipeline chain.
    """

    event = str(event or "").strip().upper()
    if event not in PIPELINE_TIMELINE_EVENTS:
        return
    created = _timestamp(at)
    connection.execute(
        """
        UPDATE recording_sessions
        SET stage_timings = CASE
            WHEN COALESCE(stage_timings, '{}'::jsonb) #> ARRAY['pipelineEvents', %s] IS NOT NULL
                THEN COALESCE(stage_timings, '{}'::jsonb)
            ELSE jsonb_set(
                COALESCE(stage_timings, '{}'::jsonb),
                ARRAY['pipelineEvents', %s],
                to_jsonb(%s::text),
                true)
            END
        WHERE id=%s
        """,
        (event, event, created, session_id),
    )
    for start_event, duration_key in _DURATION_EDGES.get(event, ()):
        connection.execute(
            """
            UPDATE recording_sessions
            SET stage_timings = CASE
                WHEN COALESCE(stage_timings, '{}'::jsonb) #> ARRAY['pipelineDurations', %s] IS NOT NULL
                    OR COALESCE(stage_timings, '{}'::jsonb) #>> ARRAY['pipelineEvents', %s] IS NULL
                    THEN COALESCE(stage_timings, '{}'::jsonb)
                ELSE jsonb_set(
                    COALESCE(stage_timings, '{}'::jsonb),
                    ARRAY['pipelineDurations', %s],
                    to_jsonb(GREATEST(0, ROUND(EXTRACT(EPOCH FROM
                        (%s::timestamptz -
                         (COALESCE(stage_timings, '{}'::jsonb) #>> ARRAY['pipelineEvents', %s])::timestamptz)) * 1000))::bigint),
                    true)
                END
            WHERE id=%s
            """,
            (duration_key, start_event, duration_key, created, start_event, session_id),
        )

