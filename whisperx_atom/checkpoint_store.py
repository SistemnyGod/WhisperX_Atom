"""Durable, atomic checkpoints for repeatable processing stages.

Checkpoints are derived artifacts, never canonical recordings.  They are
validated against an explicit source fingerprint and can be deleted/rebuilt
without affecting PCM/FLAC, Transcript V1 or the jobs table.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol


_SAFE_COMPONENT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")


def _component(value: str, error_code: str) -> str:
    normalized = str(value or "").strip()
    if not _SAFE_COMPONENT.fullmatch(normalized) or normalized in {".", ".."}:
        raise ValueError(error_code)
    return normalized


def _json_default(value: Any) -> Any:
    item = getattr(value, "item", None)
    if callable(item):
        return item()
    raise TypeError(f"CHECKPOINT_VALUE_NOT_SERIALIZABLE:{type(value).__name__}")


def _canonical_json(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        default=_json_default,
    ).encode("utf-8")


@dataclass(frozen=True)
class PipelineCheckpoint:
    job_id: str
    stage: str
    source_fingerprint: str
    artifact_key: str
    artifact_sha256: str
    result: dict[str, Any]


class PipelineCheckpointStore(Protocol):
    def load(self, job_id: str, stage: str, source_fingerprint: str) -> PipelineCheckpoint | None:
        ...

    def save(
        self,
        job_id: str,
        stage: str,
        source_fingerprint: str,
        result: dict[str, Any],
    ) -> PipelineCheckpoint:
        ...


class NullPipelineCheckpointStore:
    """Compatibility implementation used when durable artifacts are disabled."""

    def load(self, job_id: str, stage: str, source_fingerprint: str) -> PipelineCheckpoint | None:
        return None

    def save(
        self,
        job_id: str,
        stage: str,
        source_fingerprint: str,
        result: dict[str, Any],
    ) -> PipelineCheckpoint:
        payload = _canonical_json(result)
        return PipelineCheckpoint(
            job_id=str(job_id),
            stage=str(stage).upper(),
            source_fingerprint=str(source_fingerprint),
            artifact_key="",
            artifact_sha256=hashlib.sha256(payload).hexdigest(),
            result=result,
        )


class LocalPipelineCheckpointStore:
    """Filesystem checkpoint store below the mounted media root."""

    SCHEMA_VERSION = 1

    def __init__(self, root: Path | str, namespace: str = "/data") -> None:
        self.root = Path(root).expanduser().resolve(strict=False)
        self.namespace = str(namespace).replace("\\", "/").rstrip("/") or "/data"
        if not self.namespace.startswith("/"):
            raise ValueError("CHECKPOINT_NAMESPACE_MUST_BE_ABSOLUTE")

    def _path(self, job_id: str, stage: str) -> tuple[Path, str]:
        safe_job = _component(job_id, "CHECKPOINT_JOB_ID_INVALID")
        safe_stage = _component(str(stage).upper(), "CHECKPOINT_STAGE_INVALID")
        relative = Path("processing-checkpoints") / safe_job / f"{safe_stage}.json"
        path = self.root.joinpath(relative).resolve(strict=False)
        try:
            path.relative_to(self.root)
        except ValueError as exc:
            raise ValueError("CHECKPOINT_PATH_INVALID") from exc
        key = f"{self.namespace}/{relative.as_posix()}"
        return path, key

    def load(self, job_id: str, stage: str, source_fingerprint: str) -> PipelineCheckpoint | None:
        path, key = self._path(job_id, stage)
        if not path.is_file():
            return None
        try:
            envelope = json.loads(path.read_text(encoding="utf-8"))
            if int(envelope.get("schemaVersion", 0)) != self.SCHEMA_VERSION:
                return None
            if str(envelope.get("jobId")) != str(job_id):
                return None
            if str(envelope.get("stage")).upper() != str(stage).upper():
                return None
            if str(envelope.get("sourceFingerprint")) != str(source_fingerprint):
                return None
            result = envelope.get("result")
            if not isinstance(result, dict):
                return None
            payload = _canonical_json(result)
            digest = hashlib.sha256(payload).hexdigest()
            if digest != str(envelope.get("artifactSha256", "")).lower():
                return None
            return PipelineCheckpoint(
                job_id=str(job_id),
                stage=str(stage).upper(),
                source_fingerprint=str(source_fingerprint),
                artifact_key=key,
                artifact_sha256=digest,
                result=result,
            )
        except (OSError, ValueError, TypeError, json.JSONDecodeError):
            # A derived checkpoint is an optimization. Corruption forces a
            # deterministic stage recomputation instead of failing the job.
            return None

    def save(
        self,
        job_id: str,
        stage: str,
        source_fingerprint: str,
        result: dict[str, Any],
    ) -> PipelineCheckpoint:
        path, key = self._path(job_id, stage)
        payload = _canonical_json(result)
        digest = hashlib.sha256(payload).hexdigest()
        envelope = {
            "schemaVersion": self.SCHEMA_VERSION,
            "jobId": str(job_id),
            "stage": str(stage).upper(),
            "sourceFingerprint": str(source_fingerprint),
            "artifactSha256": digest,
            "result": result,
        }
        encoded = _canonical_json(envelope)
        path.parent.mkdir(parents=True, exist_ok=True)
        part = path.with_suffix(path.suffix + ".part")
        try:
            with part.open("wb") as target:
                target.write(encoded)
                target.flush()
                os.fsync(target.fileno())
            os.replace(part, path)
        except Exception:
            # Only the unpublished derived file may be removed.  A previously
            # published checkpoint is intentionally left untouched.
            part.unlink(missing_ok=True)
            raise
        return PipelineCheckpoint(
            job_id=str(job_id),
            stage=str(stage).upper(),
            source_fingerprint=str(source_fingerprint),
            artifact_key=key,
            artifact_sha256=digest,
            result=result,
        )


def checkpoint_store_from_env() -> PipelineCheckpointStore:
    enabled = os.getenv("PIPELINE_CHECKPOINTS_ENABLED", "true").lower() in {"1", "true", "yes"}
    if not enabled:
        return NullPipelineCheckpointStore()
    if os.name == "nt":
        root = os.getenv("WHISPERX_DATA_HOST", "").strip() or os.getenv("MEDIA_ROOT", "").strip()
    else:
        root = os.getenv("MEDIA_ROOT", "/data").strip()
    if not root:
        return NullPipelineCheckpointStore()
    return LocalPipelineCheckpointStore(root)


def checkpoint_fingerprint(values: dict[str, Any]) -> str:
    """Build a stable provenance key without including transcript text."""

    return hashlib.sha256(_canonical_json(values)).hexdigest()
