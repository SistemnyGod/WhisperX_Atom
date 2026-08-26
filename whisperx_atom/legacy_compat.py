"""Dependency-free helpers for the deprecated local WhisperX compatibility path.

Keeping these functions separate from the torch/WhisperX import boundary makes
the safety behaviour executable in lightweight contract tests as well as in the
legacy runtime.
"""

from __future__ import annotations

import copy
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import Any


def assign_speakers_by_overlap(
    segments: Sequence[dict[str, Any]],
    diar_segments: Sequence[dict[str, Any]],
) -> list[dict[str, Any]] | Sequence[dict[str, Any]]:
    """Assign labels with a forward interval sweep while preserving ASR order."""
    if not segments or not diar_segments:
        return segments

    ordered_diar = sorted(
        diar_segments,
        key=lambda item: (float(item["start"]), float(item["end"])),
    )
    # Pyannote emits non-overlapping intervals.  The cursor therefore advances
    # once and avoids restarting a full N×M scan for every ASR segment.
    out_by_index: dict[int, dict[str, Any]] = {}
    ordered_segments = sorted(
        enumerate(segments),
        key=lambda pair: (
            float(pair[1].get("start", 0)),
            float(pair[1].get("end", pair[1].get("start", 0))),
        ),
    )
    cursor = 0
    for original_index, segment in ordered_segments:
        start = float(segment.get("start", 0))
        end = float(segment.get("end", start))
        while cursor < len(ordered_diar) and float(ordered_diar[cursor]["end"]) <= start:
            cursor += 1

        best_overlap = 0.0
        best_speaker = "UNKNOWN"
        candidate_index = cursor
        while candidate_index < len(ordered_diar) and float(ordered_diar[candidate_index]["start"]) < end:
            item = ordered_diar[candidate_index]
            overlap = min(end, float(item["end"])) - max(start, float(item["start"]))
            if overlap > best_overlap:
                best_overlap = overlap
                best_speaker = item.get("speaker", "UNKNOWN")
            candidate_index += 1
        if best_overlap <= 0 and cursor < len(ordered_diar):
            midpoint = (start + end) / 2
            item = ordered_diar[cursor]
            if float(item["start"]) <= midpoint <= float(item["end"]):
                best_speaker = item.get("speaker", "UNKNOWN")

        labeled = dict(segment)
        labeled["speaker"] = best_speaker
        out_by_index[original_index] = labeled
    return [out_by_index[index] for index in range(len(segments))]


def assign_speaker_result(
    result: dict[str, Any],
    assigner: Callable[[dict[str, Any]], dict[str, Any]],
    diar_segments: Sequence[dict[str, Any]],
) -> dict[str, Any]:
    """Run an assigner without allowing in-place mutation to erase ASR data."""
    original_segments = copy.deepcopy(result.get("segments") or [])
    if "word_segments" in result:
        try:
            assigned = assigner(result)
        except Exception:
            assigned = None
        if isinstance(assigned, dict) and assigned.get("segments"):
            return assigned

    result["segments"] = assign_speakers_by_overlap(original_segments, diar_segments)
    return result


def recover_interrupted_jobs(
    job_dir: Path,
    read_job: Callable[[str], dict[str, Any]],
    write_job: Callable[[str, dict[str, Any]], None],
) -> int:
    """Mark in-memory legacy jobs left active by a process restart."""
    recovered = 0
    if not job_dir.exists():
        return recovered
    for path in job_dir.glob("*.json"):
        try:
            data = read_job(path.stem)
        except (OSError, ValueError, TypeError):
            continue
        if str(data.get("status", "")).lower() not in {"running", "processing"}:
            continue
        data["status"] = "error"
        data["error"] = "LEGACY_PIPELINE_INTERRUPTED_AFTER_RESTART"
        data["stage"] = data.get("stage") or "recovery"
        write_job(path.stem, data)
        recovered += 1
    return recovered
