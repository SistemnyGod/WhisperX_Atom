from __future__ import annotations

import hashlib
import os
import subprocess
from dataclasses import dataclass
from pathlib import Path

try:
    import psycopg
except ImportError:  # pragma: no cover - the container always installs psycopg
    psycopg = None  # type: ignore[assignment]


@dataclass(frozen=True)
class Chunk:
    sequence: int
    storage_key: str
    start_sample: int
    sample_count: int
    size_bytes: int
    sha256: str


@dataclass(frozen=True)
class Track:
    track_id: str
    track_type: str
    chunks: tuple[Chunk, ...]


def _conninfo() -> str:
    return os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _storage_path(storage_key: str) -> Path:
    normalized = storage_key.replace("\\", "/")
    if normalized.startswith("/data/"):
        return Path(normalized)
    if normalized.startswith("data/"):
        return Path("/") / normalized
    raise ValueError("invalid_recording_chunk_storage_key")


def _run_ffmpeg(args: list[str]) -> None:
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args], check=True, timeout=1800)


def _concat_track(track: Track, output: Path) -> None:
    if not track.chunks:
        raise ValueError(f"recording_track_empty:{track.track_id}")
    previous_end: int | None = None
    list_path = output.with_suffix(".concat.txt")
    lines: list[str] = []
    for expected_sequence, chunk in enumerate(track.chunks):
        if chunk.sequence != expected_sequence:
            raise ValueError(f"recording_chunk_sequence_gap:{track.track_id}:{expected_sequence}")
        source = _storage_path(chunk.storage_key)
        if not source.is_file() or source.stat().st_size != chunk.size_bytes:
            raise FileNotFoundError(source)
        if _sha256(source) != chunk.sha256.lower():
            raise ValueError(f"recording_chunk_checksum_mismatch:{track.track_id}:{chunk.sequence}")
        if previous_end is not None and chunk.start_sample != previous_end:
            raise ValueError(f"recording_chunk_sample_gap:{track.track_id}:{chunk.sequence}")
        previous_end = chunk.start_sample + chunk.sample_count
        escaped = str(source).replace("'", "'\\''")
        lines.append(f"file '{escaped}'")

    list_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    try:
        _run_ffmpeg(["-f", "concat", "-safe", "0", "-i", str(list_path), "-c:a", "flac", str(output)])
    finally:
        list_path.unlink(missing_ok=True)


def _load_tracks(session_id: str) -> list[Track]:
    with psycopg.connect(_conninfo()) as connection:
        rows = connection.execute(
            """
            SELECT t.id::text,t.track_type,c.sequence,c.storage_key,c.start_sample,c.sample_count,c.size_bytes,c.sha256
            FROM recording_tracks t
            LEFT JOIN recording_chunks c ON c.track_id=t.id AND c.status='CONFIRMED'
            WHERE t.session_id=%s
            ORDER BY t.id,c.sequence
            """,
            (session_id,),
        ).fetchall()

    grouped: dict[str, tuple[str, list[Chunk]]] = {}
    for track_id, track_type, sequence, storage_key, start_sample, sample_count, size_bytes, sha256 in rows:
        if track_id not in grouped:
            grouped[track_id] = (track_type, [])
        if sequence is not None:
            grouped[track_id][1].append(Chunk(sequence, storage_key, start_sample, sample_count, size_bytes, sha256))
    return [Track(track_id, track_type, tuple(chunks)) for track_id, (track_type, chunks) in grouped.items()]


def assemble_recording_session(session_id: str, output_dir: Path) -> Path:
    tracks = _load_tracks(session_id)
    if not tracks:
        raise ValueError("recording_tracks_required")
    output_dir.mkdir(parents=True, exist_ok=True)
    assembled: list[Path] = []
    for track in tracks:
        output = output_dir / f"track-{track.track_id}.flac"
        _concat_track(track, output)
        assembled.append(output)

    if len(assembled) == 1:
        source = assembled[0]
    else:
        source = output_dir / "mixed.flac"
        inputs: list[str] = []
        for path in assembled:
            inputs.extend(["-i", str(path)])
        filter_spec = f"amix=inputs={len(assembled)}:duration=longest:normalize=0,aresample=48000"
        _run_ffmpeg([*inputs, "-filter_complex", filter_spec, "-ac", "1", "-c:a", "flac", str(source)])

    with psycopg.connect(_conninfo()) as connection:
        connection.execute("UPDATE recording_sessions SET state='INGESTED' WHERE id=%s", (session_id,))
    return source
