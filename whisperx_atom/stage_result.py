"""Typed, payload-free results for individual processing stages.

External clients already consume the compact ``stage_outcomes`` mapping.  The
types in this module make stage results explicit inside Core Pipeline while
preserving that mapping at the serialization boundary.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Iterable, Mapping


class StageResultStatus(str, Enum):
    PENDING = "PENDING"
    RUNNING = "RUNNING"
    SUCCEEDED = "SUCCEEDED"
    PARTIAL = "PARTIAL"
    FAILED = "FAILED"
    SKIPPED = "SKIPPED"
    REUSED = "REUSED"


_LEGACY_STATUS_ALIASES: dict[str, StageResultStatus] = {
    "REUSED_V1": StageResultStatus.REUSED,
}


def normalize_stage_result_status(value: str | StageResultStatus) -> StageResultStatus:
    if isinstance(value, StageResultStatus):
        return value
    normalized = str(value or "").strip().upper()
    if not normalized:
        raise ValueError("STAGE_RESULT_STATUS_EMPTY")
    try:
        return StageResultStatus(normalized)
    except ValueError:
        try:
            return _LEGACY_STATUS_ALIASES[normalized]
        except KeyError as exc:
            raise ValueError(f"STAGE_RESULT_STATUS_UNKNOWN:{normalized}") from exc


@dataclass(frozen=True)
class StageResult:
    """Result metadata for one pipeline stage, without transcript/audio data."""

    stage: str
    status: StageResultStatus
    legacy_status: str
    warnings: tuple[str, ...] = ()
    metadata: Mapping[str, Any] = field(default_factory=dict)

    @classmethod
    def from_values(
        cls,
        stage: str,
        status: str | StageResultStatus,
        *,
        warnings: Iterable[str] = (),
        metadata: Mapping[str, Any] | None = None,
    ) -> "StageResult":
        normalized_stage = str(stage or "").strip().upper()
        if not normalized_stage:
            raise ValueError("STAGE_RESULT_STAGE_EMPTY")
        normalized_status = normalize_stage_result_status(status)
        legacy_status = status.value if isinstance(status, StageResultStatus) else str(status).strip().upper()
        allowed_metadata = {
            "artifact_hash",
            "artifact_key",
            "attempt",
            "count",
            "duration_ms",
            "model",
            "profile",
            "reason",
        }
        safe_metadata = {
            str(key): value
            for key, value in dict(metadata or {}).items()
            if str(key).strip().lower() in allowed_metadata
        }
        return cls(
            stage=normalized_stage,
            status=normalized_status,
            legacy_status=legacy_status,
            warnings=tuple(dict.fromkeys(str(item).strip() for item in warnings if str(item).strip())),
            metadata=safe_metadata,
        )


class StageResults(dict[str, str]):
    """Validated stage-result collection compatible with the legacy mapping."""

    def __init__(self, values: Mapping[str, str] | None = None) -> None:
        super().__init__()
        self._details: dict[str, StageResult] = {}
        for stage, status in dict(values or {}).items():
            self.set(stage, status)

    def set(
        self,
        stage: str,
        status: str | StageResultStatus,
        *,
        warnings: Iterable[str] = (),
        metadata: Mapping[str, Any] | None = None,
    ) -> StageResult:
        result = StageResult.from_values(stage, status, warnings=warnings, metadata=metadata)
        self._details[result.stage] = result
        super().__setitem__(result.stage, result.legacy_status)
        return result

    def __setitem__(self, stage: str, status: str) -> None:
        self.set(stage, status)

    def __delitem__(self, stage: str) -> None:
        normalized = str(stage or "").strip().upper()
        self._details.pop(normalized, None)
        super().__delitem__(normalized)

    def clear(self) -> None:
        self._details.clear()
        super().clear()

    def result_for(self, stage: str) -> StageResult | None:
        return self._details.get(str(stage or "").strip().upper())

    def as_legacy_dict(self) -> dict[str, str]:
        return dict(self)


def ensure_stage_results(values: Mapping[str, str] | StageResults | None) -> StageResults:
    return values if isinstance(values, StageResults) else StageResults(values)
