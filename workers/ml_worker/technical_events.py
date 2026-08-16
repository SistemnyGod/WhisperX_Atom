from __future__ import annotations

import os
from collections.abc import Iterable


def build_technical_intervals(
    rows: Iterable[tuple[object, object]],
    max_open_ms: int | None = None,
) -> list[tuple[int, int, str]]:
    """Build bounded technical intervals from recorder timeline markers."""
    if max_open_ms is None:
        max_open_ms = max(1000, min(60000, int(os.getenv("VOICE_TECHNICAL_EVENT_MAX_MS", "15000"))))
    max_open_ms = max(1000, min(60000, int(max_open_ms)))
    intervals: list[tuple[int, int, str]] = []
    open_start: int | None = None
    for event_type, media_time_ms in rows:
        event = str(event_type).upper()
        at = max(0, int(media_time_ms))
        if event == "SYSTEM_RESPONSE_STARTED":
            if open_start is not None:
                intervals.append((open_start, open_start + max_open_ms, "TECHNICAL"))
            open_start = at
        elif event == "SYSTEM_RESPONSE_FINISHED":
            if open_start is not None:
                intervals.append((open_start, max(open_start, at), "TECHNICAL"))
                open_start = None
        elif event == "VOICE_COMMAND":
            intervals.append((at, at, "TECHNICAL"))
    if open_start is not None:
        intervals.append((open_start, open_start + max_open_ms, "TECHNICAL"))
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
