from __future__ import annotations

import hashlib
import json
import os
import subprocess
import wave
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
    quality_report: dict


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


def _measure_pcm_quality(path: Path) -> dict:
    """Measure the canonical ASR PCM without loading the whole recording."""
    with wave.open(str(path), "rb") as source:
        sample_rate = source.getframerate()
        frame_count = source.getnframes()
        total_samples = 0
        sum_squares = 0.0
        peak = 0
        clipped = 0
        silent = 0
        first_audio = None
        last_audio = None
        frame_offset = 0
        while True:
            block = source.readframes(max(1, sample_rate * 5))
            if not block:
                break
            values = memoryview(block).cast("h")
            block_peak = max((abs(value) for value in values), default=0)
            block_sum_squares = sum((value / 32768.0) ** 2 for value in values)
            block_rms = (block_sum_squares / len(values)) ** 0.5 if len(values) else 0.0
            peak = max(peak, block_peak)
            sum_squares += block_sum_squares
            total_samples += len(values)
            clipped += sum(1 for value in values if abs(value) >= 32604)
            if block_rms < 0.003:
                silent += len(values)
            elif first_audio is None:
                first_audio = frame_offset
            last_audio = frame_offset + len(values)
            frame_offset += len(values)

    if total_samples == 0:
        return {"sample_count": 0, "rms": 0.0, "peak": 0.0, "clipping_ratio": 0.0, "silence_ratio": 1.0}
    return {
        "sample_count": total_samples,
        "rms": (sum_squares / total_samples) ** 0.5,
        "peak": peak / 32768.0,
        "clipping_ratio": clipped / total_samples,
        "silence_ratio": silent / total_samples,
        "first_audio_ms": None if first_audio is None else round(first_audio * 1000 / sample_rate),
        "last_audio_ms": None if last_audio is None else round(last_audio * 1000 / sample_rate),
        "duration_ms": round(frame_count * 1000 / sample_rate),
    }


def prepare_media(input_path: Path, output_dir: Path) -> MediaDerivatives:
    probe = probe_audio(input_path)
    output_dir.mkdir(parents=True, exist_ok=True)
    archive = output_dir / "archive.flac"
    preview = output_dir / "preview.opus"
    asr = output_dir / "asr.wav"

    _run_ffmpeg(input_path, archive, ["-map", "0:a:0", "-ac", "1", "-ar", "48000", "-c:a", "flac", "-compression_level", "5"])
    _run_ffmpeg(input_path, preview, ["-map", "0:a:0", "-ac", "1", "-ar", "48000", "-c:a", "libopus", "-b:a", "48k"])
    _run_ffmpeg(input_path, asr, ["-map", "0:a:0", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le"])

    audio_stream = next(stream for stream in probe["streams"] if stream.get("codec_type") == "audio")
    assembly_input = input_path.parent / "assembly-input.json"
    try:
        recording_tracks = json.loads(assembly_input.read_text(encoding="utf-8")).get("recording_tracks", []) if assembly_input.is_file() else []
    except (OSError, json.JSONDecodeError):
        recording_tracks = []
    assembly_result_path = input_path.parent / "assembly-result.json"
    try:
        assembly_result = json.loads(assembly_result_path.read_text(encoding="utf-8")) if assembly_result_path.is_file() else {}
    except (OSError, json.JSONDecodeError):
        assembly_result = {}
    drift_values = [float(item.get("driftMs", item.get("drift_ms", 0))) for item in assembly_result.get("tracks", []) if isinstance(item, dict)]
    quality_report = {
        "duration_ms": int(probe["duration_ms"]),
        "sample_rate": int(audio_stream.get("sample_rate") or 0),
        "channels": int(audio_stream.get("channels") or 0),
        "codec": audio_stream.get("codec_name"),
        "selected_asr_source": assembly_result.get("selectedAsrSource", assembly_result.get("selected_asr_source", "audio_stream_0")),
        "recording_profile": assembly_result.get("recordingProfile", assembly_result.get("recording_profile")),
        "track_count": assembly_result.get("trackCount", assembly_result.get("track_count", len(recording_tracks))),
        "drift_ms": max((abs(value) for value in drift_values), default=0),
        "mix_strategy": assembly_result.get("mixStrategy", assembly_result.get("mix_strategy", "single_original_track")),
        "derived_sample_rate": 16000,
        "derived_channels": 1,
        "warnings": [],
        "recording_tracks": recording_tracks,
        "assembly": assembly_result,
        **_measure_pcm_quality(asr),
    }

    digest = hashlib.sha256()
    with input_path.open("rb") as source:
        for chunk in iter(lambda: source.read(4 * 1024 * 1024), b""):
            digest.update(chunk)
    (output_dir / "audio-quality.json").write_text(
        json.dumps(quality_report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    return MediaDerivatives(
        original=input_path,
        archive_flac=archive,
        preview_opus=preview,
        asr_wav=asr,
        sha256=digest.hexdigest(),
        duration_ms=int(probe["duration_ms"]),
        probe=probe,
        quality_report=quality_report,
    )
