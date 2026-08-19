"""Storage boundary for media artifacts.

Workers exchange opaque ``/data/...`` storage keys.  Only this module maps a
key to a local mount, keeping path-traversal checks identical for recorder
assemblies, imported files and GPU processing.
"""

from __future__ import annotations

import os
from pathlib import Path, PurePosixPath
from typing import Protocol


class MediaStorage(Protocol):
    def resolve(self, storage_key: str) -> Path:
        ...


class LocalMediaStorage:
    """Filesystem implementation of the media-storage contract."""

    def __init__(self, root: Path | str, namespace: str = "/data") -> None:
        self.root = Path(root).expanduser()
        self.namespace = str(namespace).replace("\\", "/").rstrip("/") or "/data"
        if not self.namespace.startswith("/"):
            raise ValueError("storage_namespace_must_be_absolute")

    def resolve(self, storage_key: str) -> Path:
        value = str(storage_key or "").strip().replace("\\", "/")
        try:
            relative = PurePosixPath(value).relative_to(self.namespace)
        except ValueError as exc:
            raise ValueError("invalid_storage_key") from exc
        if not relative.parts or any(
            part in {"", ".", ".."} or ":" in part for part in relative.parts
        ):
            raise ValueError("invalid_storage_key")

        root = self.root.resolve(strict=False)
        candidate = root.joinpath(*relative.parts).resolve(strict=False)
        try:
            candidate.relative_to(root)
        except ValueError as exc:
            raise ValueError("invalid_storage_key") from exc
        return candidate

    def exists(self, storage_key: str) -> bool:
        return self.resolve(storage_key).is_file()


def media_storage_from_env(*, root: Path | str | None = None) -> LocalMediaStorage:
    """Build the current local storage adapter without exposing environment details to callers."""

    configured_root = root if root is not None else os.getenv("MEDIA_ROOT", "/data")
    return LocalMediaStorage(configured_root)
