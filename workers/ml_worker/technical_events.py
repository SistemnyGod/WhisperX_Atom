from __future__ import annotations

import os
import json
from collections.abc import Iterable
from typing import Any


def build_technical_intervals(
    rows: Iterable[tuple[object, ...]],
    max_open_ms: int | None = None,
) -> list[tuple[int, int, str]]:
    """Build bounded technical intervals from recorder timeline markers."""
    if max_open_ms is None:
        max_open_ms = max(1000, min(60000, int(os.getenv("VOICE_TECHNICAL_EVENT_MAX_MS", "15000"))))
    max_open_ms = max(1000, min(60000, int(max_open_ms)))
    intervals: list[tuple[int, int, str]] = []
    # Responses may be queued while TTS is playing. Pair by the durable
    # responseId instead of assuming a single global START/FINISH pair.
    open_by_id: dict[str, int] = {}
    anonymous_starts: list[int] = []

    def response_id(payload: Any) -> str | None:
        if payload is None:
            return None
        if isinstance(payload, str):
            try:
                payload = json.loads(payload)
            except (TypeError, ValueError):
                return None
        if isinstance(payload, dict):
            value = payload.get("responseId") or payload.get("response_id")
            if value:
                return str(value)
            nested = payload.get("payload")
            if nested is not None:
                return response_id(nested)
        return None

    for row in rows:
        event_type, media_time_ms = row[0], row[1]
        payload = row[2] if len(row) > 2 else None
        event = str(event_type).upper()
        at = max(0, int(media_time_ms))
        if event == "SYSTEM_RESPONSE_STARTED":
            event_id = response_id(payload)
            if event_id:
                previous = open_by_id.pop(event_id, None)
                if previous is not None:
                    intervals.append((previous, previous + max_open_ms, "TECHNICAL"))
                open_by_id[event_id] = at
            else:
                anonymous_starts.append(at)
        elif event == "SYSTEM_RESPONSE_FINISHED":
            event_id = response_id(payload)
            start = open_by_id.pop(event_id, None) if event_id else (anonymous_starts.pop(0) if anonymous_starts else None)
            if start is not None:
                intervals.append((start, max(start, at), "TECHNICAL"))
        elif event == "VOICE_COMMAND":
            intervals.append((at, at, "TECHNICAL"))
    intervals.extend((start, start + max_open_ms, "TECHNICAL") for start in open_by_id.values())
    intervals.extend((start, start + max_open_ms, "TECHNICAL") for start in anonymous_starts)
    return intervals


def segment_technical_flags(
    start_ms: int,
    end_ms: int,
    intervals: Iterable[tuple[int, int, str]],
) -> tuple[str, bool]:
    start = max(0, int(start_ms))
    end = max(start, int(end_ms))
    for interval_start, interval_end, kind in intervals:
        if start <= interval_end and end >= interval_start:
            return kind, True
    return "SPEECH", False
