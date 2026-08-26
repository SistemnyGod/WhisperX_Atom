import hashlib
import json
import os
import subprocess
import tempfile
import uuid
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from fastapi import UploadFile

from whisperx_atom.media_policy import ALLOWED_AUDIO_EXTENSIONS, CHUNK_SIZE, MAX_UPLOAD_BYTES, VIDEO_EXTENSIONS
from whisperx_atom.runtime_state import (
    JOBS_DIR,
    job_path,
    read_job_json,
    write_job_json,
)

BASE_DIR = Path(__file__).resolve().parent.parent
DATA_DIR = BASE_DIR / "data"
UPLOAD_DIR = DATA_DIR / "uploads"
JOBS_DIR = DATA_DIR / "jobs"


def ensure_dirs():
    UPLOAD_DIR.mkdir(parents=True, exist_ok=True)
    JOBS_DIR.mkdir(parents=True, exist_ok=True)


def _probe_audio(path: Path) -> dict:
    """Validate the real container, not only the client supplied extension."""
    try:
        completed = subprocess.run(
            [
                "ffprobe", "-v", "error", "-show_streams", "-show_format",
                "-of", "json", str(path),
            ],
            check=True,
            capture_output=True,
            text=True,
            timeout=60,
        )
    except FileNotFoundError:
        return {"probe_skipped": True}
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as exc:
        raise ValueError("Файл не является корректным аудиофайлом") from exc

    payload = json.loads(completed.stdout or "{}")
    streams = payload.get("streams", [])
    if not any(stream.get("codec_type") == "audio" for stream in streams):
        raise ValueError("В файле отсутствует аудиодорожка")
    return payload


async def save_upload(file: "UploadFile"):
    job_id = uuid.uuid4().hex[:12]
    suffix = (Path(file.filename).suffix or ".bin").lower()
    if suffix in VIDEO_EXTENSIONS:
        raise ValueError("Видео в legacy API не поддерживается; используйте аудиофайл")
    if suffix not in ALLOWED_AUDIO_EXTENSIONS:
        raise ValueError(f"Неподдерживаемый аудиоформат: {suffix}")
    audio_path = UPLOAD_DIR / f"{job_id}{suffix}"

    fd, part_name = tempfile.mkstemp(prefix=f"{job_id}-", suffix=".part", dir=UPLOAD_DIR)
    part_path = Path(part_name)
    digest = hashlib.sha256()
    size = 0
    try:
        with os.fdopen(fd, "wb") as destination:
            while True:
                chunk = await file.read(CHUNK_SIZE)
                if not chunk:
                    break
                size += len(chunk)
                if size > MAX_UPLOAD_BYTES:
                    raise ValueError("Файл превышает максимальный размер загрузки")
                digest.update(chunk)
                destination.write(chunk)
            destination.flush()
            os.fsync(destination.fileno())
        _probe_audio(part_path)
        part_path.replace(audio_path)
    except Exception:
        part_path.unlink(missing_ok=True)
        raise

    return job_id, audio_path
