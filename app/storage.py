import json
import uuid
from pathlib import Path

from fastapi import UploadFile

BASE_DIR = Path(__file__).resolve().parent.parent
DATA_DIR = BASE_DIR / "data"
UPLOAD_DIR = DATA_DIR / "uploads"
JOBS_DIR = DATA_DIR / "jobs"


def ensure_dirs():
    UPLOAD_DIR.mkdir(parents=True, exist_ok=True)
    JOBS_DIR.mkdir(parents=True, exist_ok=True)


def job_path(job_id: str) -> Path:
    return JOBS_DIR / f"{job_id}.json"


def read_job_json(job_id: str) -> dict:
    with job_path(job_id).open("r", encoding="utf-8") as f:
        return json.load(f)


def write_job_json(job_id: str, data: dict) -> None:
    with job_path(job_id).open("w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


async def save_upload(file: UploadFile):
    job_id = uuid.uuid4().hex[:12]

    # сохраняем как есть (webm / wav / etc)
    suffix = Path(file.filename).suffix or ".bin"
    audio_path = UPLOAD_DIR / f"{job_id}{suffix}"

    content = await file.read()
    audio_path.write_bytes(content)

    return job_id, audio_path
