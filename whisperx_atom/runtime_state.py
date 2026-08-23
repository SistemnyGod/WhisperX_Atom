"""Atomic runtime job-state storage used by the production pipeline.

The legacy FastAPI shim still keeps its upload helpers in ``app.storage``;
the production transcription path depends only on this small boundary.  A
replace-based write prevents a worker crash from leaving a truncated JSON
state file for the next retry.
"""

from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path


BASE_DIR = Path(__file__).resolve().parents[1]
DATA_DIR = BASE_DIR / "data"
JOBS_DIR = DATA_DIR / "jobs"


def ensure_dirs() -> None:
    JOBS_DIR.mkdir(parents=True, exist_ok=True)


def job_path(job_id: str) -> Path:
    value = str(job_id or "").strip()
    if not value or Path(value).name != value or any(char in value for char in "\\/:"):
        raise ValueError("invalid_job_id")
    return JOBS_DIR / f"{value}.json"


def read_job_json(job_id: str) -> dict:
    with job_path(job_id).open("r", encoding="utf-8") as stream:
        return json.load(stream)


def write_job_json(job_id: str, data: dict) -> None:
    ensure_dirs()
    destination = job_path(job_id)
    fd, temporary = tempfile.mkstemp(prefix=f".{destination.stem}-", suffix=".json.tmp", dir=JOBS_DIR)
    temporary_path = Path(temporary)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(data, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_path, destination)
    finally:
        temporary_path.unlink(missing_ok=True)
