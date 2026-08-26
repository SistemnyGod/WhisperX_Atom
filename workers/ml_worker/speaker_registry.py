"""Pure, conservative speaker-profile matching helpers.

WhisperX/pyannote diarization labels are not identities.  This module only
maps a label when a caller supplies a valid embedding and the winning profile
is both above the confidence threshold and sufficiently separated from the
runner-up.  It never reads or stores audio.
"""
from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Any, Iterable, Mapping, Sequence


@dataclass(frozen=True)
class SpeakerMatch:
    profile_id: str | None
    confidence: float | None
    status: str
    reason: str
    suggestion_profile_id: str | None = None


def normalize_embedding(value: Any, max_dimensions: int = 2048) -> tuple[float, ...] | None:
    if isinstance(value, Mapping):
        value = value.get("values") or value.get("vector") or value.get("embedding")
    if isinstance(value, (str, bytes, bytearray)) or not isinstance(value, Iterable):
        return None
    try:
        vector = tuple(float(item) for item in value)
    except (TypeError, ValueError, OverflowError):
        return None
    if not vector or len(vector) > max_dimensions or any(not math.isfinite(item) for item in vector):
        return None
    norm = math.sqrt(sum(item * item for item in vector))
    return vector if norm > 1e-12 else None


def cosine_similarity(left: Sequence[float], right: Sequence[float]) -> float | None:
    if len(left) != len(right) or not left:
        return None
    left_norm = math.sqrt(sum(float(item) * float(item) for item in left))
    right_norm = math.sqrt(sum(float(item) * float(item) for item in right))
    if left_norm <= 1e-12 or right_norm <= 1e-12:
        return None
    return max(-1.0, min(1.0, sum(float(a) * float(b) for a, b in zip(left, right)) / (left_norm * right_norm)))


def update_centroid(old: Sequence[float] | None, new: Sequence[float], samples: int) -> tuple[float, ...] | None:
    incoming = normalize_embedding(new)
    if incoming is None:
        return None
    previous = normalize_embedding(old) if old is not None else None
    if previous is None or len(previous) != len(incoming) or samples <= 0:
        return incoming
    merged = tuple((float(a) * samples + float(b)) / (samples + 1) for a, b in zip(previous, incoming))
    norm = math.sqrt(sum(item * item for item in merged))
    return tuple(item / norm for item in merged) if norm > 1e-12 else None


def match_profile(
    embedding: Any,
    profiles: Iterable[Mapping[str, Any]],
    threshold: float = 0.78,
    margin: float = 0.08,
    suggestion_threshold: float = 0.60,
) -> SpeakerMatch:
    vector = normalize_embedding(embedding)
    if vector is None:
        return SpeakerMatch(None, None, "UNMATCHED", "DIARIZATION_EMBEDDING_MISSING")
    scored: list[tuple[float, str]] = []
    for profile in profiles:
        profile_id = str(profile.get("id") or "").strip()
        centroid = normalize_embedding(profile.get("embedding_centroid") or profile.get("centroid"))
        if not profile_id or centroid is None or len(centroid) != len(vector):
            continue
        score = cosine_similarity(vector, centroid)
        if score is not None:
            scored.append((score, profile_id))
    if not scored:
        return SpeakerMatch(None, None, "UNMATCHED", "NO_COMPATIBLE_PROFILE")
    scored.sort(reverse=True)
    best_score, best_id = scored[0]
    second_score = scored[1][0] if len(scored) > 1 else -1.0
    if best_score >= threshold and best_score - second_score >= margin:
        return SpeakerMatch(best_id, best_score, "MATCHED", "THRESHOLD_AND_MARGIN")
    if best_score >= suggestion_threshold:
        return SpeakerMatch(None, best_score, "SUGGESTION", "LOW_CONFIDENCE_OR_AMBIGUOUS", best_id)
    return SpeakerMatch(None, best_score, "UNMATCHED", "BELOW_SUGGESTION_THRESHOLD")


def default_display_label(stable_key: str) -> str:
    normalized = str(stable_key or "").strip().upper()
    if normalized.startswith("SPEAKER_"):
        suffix = normalized.removeprefix("SPEAKER_")
        try:
            return f"Спикер {int(suffix)}"
        except ValueError:
            pass
    return normalized or "Спикер"
