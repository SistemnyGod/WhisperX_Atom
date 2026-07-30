from __future__ import annotations

import hashlib
import json
import os
import subprocess
from dataclasses import dataclass
from pathlib import Path


MAX_BYTES = int(os.getenv("MAX_MEDIA_BYTES", str(8 * 1024 * 1024 * 1024)))
MAX_DURATION_MS = int(os.getenv("MAX_MEDIA_DURATION_MS", str(4 * 60 * 60 * 1000)))


@dataclass(frozen=True)
class MediaDerivatives:
    original: Path
    archive_flac: Path
    preview_opus: Path
    asr_wav: Path
    sha256: str
    duration_ms: int
    probe: dict


def probe_audio(path: Path) -> dict:
    if not path.is_file():
        raise FileNotFoundError(path)
    if path.stat().st_size > MAX_BYTES:
        raise ValueError("media exceeds configured size limit")

    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)],
        check=True,
        capture_output=True,
        text=True,
        timeout=120,
    )
    payload = json.loads(result.stdout or "{}")
    streams = payload.get("streams", [])
    audio = [stream for stream in streams if stream.get("codec_type") == "audio"]
    if not audio:
        raise ValueError("media has no audio stream")
    duration = float((payload.get("format") or {}).get("duration") or audio[0].get("duration") or 0)
    duration_ms = int(duration * 1000)
    if duration_ms <= 0:
        raise ValueError("media duration is unavailable")
    if duration_ms > MAX_DURATION_MS:
        raise ValueError("media exceeds configured duration limit")
    return {"streams": streams, "format": payload.get("format", {}), "duration_ms": duration_ms}


def _run_ffmpeg(input_path: Path, output_path: Path, args: list[str]) -> None:
    temporary = output_path.with_name(output_path.stem + ".part" + output_path.suffix)
    temporary.unlink(missing_ok=True)
    command = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(input_path), *args, str(temporary)]
    subprocess.run(command, check=True, timeout=900)
    temporary.replace(output_path)


def prepare_media(input_path: Path, output_dir: Path) -> MediaDerivatives:
    probe = probe_audio(input_path)
    output_dir.mkdir(parents=True, exist_ok=True)
    archive = output_dir / "archive.flac"
    preview = output_dir / "preview.opus"
    asr = output_dir / "asr.wav"

    _run_ffmpeg(input_path, archive, ["-map", "0:a:0", "-ac", "1", "-ar", "48000", "-c:a", "flac", "-compression_level", "5"])
    _run_ffmpeg(input_path, preview, ["-map", "0:a:0", "-ac", "1", "-ar", "48000", "-c:a", "libopus", "-b:a", "48k"])
    _run_ffmpeg(input_path, asr, ["-map", "0:a:0", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le"])

    digest = hashlib.sha256()
    with input_path.open("rb") as source:
        for chunk in iter(lambda: source.read(4 * 1024 * 1024), b""):
            digest.update(chunk)

    return MediaDerivatives(
        original=input_path,
        archive_flac=archive,
        preview_opus=preview,
        asr_wav=asr,
        sha256=digest.hexdigest(),
        duration_ms=int(probe["duration_ms"]),
        probe=probe,
    )

