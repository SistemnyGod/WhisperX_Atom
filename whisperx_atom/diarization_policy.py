"""Small, dependency-free policy helpers for the pyannote stage.

The diarizer is a request-scoped stage, but its model lifetime is owned by the
resident WhisperX pipeline.  Keeping the policy here makes the memory
decisions deterministic and unit-testable without importing torch/pyannote.
"""

from __future__ import annotations

import os


def env_bool(name: str, default: bool) -> bool:
    value = str(os.environ.get(name, str(default))).strip().lower()
    return value in {"1", "true", "yes", "y", "on"}


def env_int(name: str, default: int, *, minimum: int = 0, maximum: int = 1_000_000) -> int:
    try:
        value = int(str(os.environ.get(name, default)).strip())
    except (TypeError, ValueError):
        value = default
    return max(minimum, min(maximum, value))


def resolve_speaker_bounds(
    requested_min: int | None,
    requested_max: int | None,
    *,
    configured_min: int,
    configured_max: int,
) -> tuple[int, int]:
    """Resolve per-request limits while preserving legacy 1/12 defaults.

    Older NATS payloads omit limits and the compatibility dataclass represents
    that omission as ``1/12``.  Treat that exact pair as an omitted value so a
    deployment can safely set a bounded default.  Any other pair remains an
    explicit caller choice.
    """

    req_min = int(requested_min) if requested_min is not None else 1
    req_max = int(requested_max) if requested_max is not None else 12
    if req_min == 1 and req_max == 12:
        req_min, req_max = int(configured_min), int(configured_max)
    req_min = max(1, req_min)
    req_max = max(req_min, req_max)
    return req_min, req_max


def is_cuda_oom(error: BaseException) -> bool:
    text = f"{type(error).__name__}: {error}".lower()
    return "cuda" in text and ("out of memory" in text or "oom" in text)


def should_release_asr(*, free_vram_mb: float | None, threshold_mb: int, enabled: bool) -> bool:
    """Return whether cached ASR/alignment models should yield VRAM."""

    return bool(enabled and free_vram_mb is not None and free_vram_mb < max(0, threshold_mb))
