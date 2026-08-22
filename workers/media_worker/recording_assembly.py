from __future__ import annotations

import hashlib
import json
import os
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from pathlib import PurePosixPath

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
    sha256: str | None


@dataclass(frozen=True)
class Track:
    track_id: str
    track_type: str
    chunks: tuple[Chunk, ...]
    device_id: str | None = None
    device_name: str | None = None
    selection_mode: str | None = None
    recording_profile: str | None = None
    encoding: str | None = None
    bits_per_sample: int | None = None
    sample_rate: int = 48000


def _conninfo() -> str:
    return os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _storage_path(storage_key: str) -> Path:
    normalized = str(storage_key or "").strip().replace("\\", "/")
    try:
        relative = PurePosixPath(normalized).relative_to("/data")
    except ValueError as exc:
        raise ValueError("invalid_recording_chunk_storage_key") from exc
    if any(part in {"", ".", ".."} for part in relative.parts):
        raise ValueError("invalid_recording_chunk_storage_key")
    root = Path(os.getenv("MEDIA_ROOT", "/data"))
    return root.joinpath(*relative.parts)


def _run_ffmpeg(args: list[str]) -> None:
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args], check=True, timeout=1800)


def _probe_duration_ms(path: Path) -> int:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", str(path)],
        check=True,
        capture_output=True,
        text=True,
        timeout=120,
    )
    try:
        return round(float(result.stdout.strip()) * 1000)
    except ValueError as exc:
        raise ValueError("recording_track_duration_unavailable") from exc


def _probe_audio_format(path: Path) -> dict:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,bits_per_sample,sample_fmt", "-of", "json", str(path)],
        check=True,
        capture_output=True,
        text=True,
        timeout=120,
    )
    streams = json.loads(result.stdout or "{}").get("streams", [])
    if not streams:
        raise ValueError("recording_chunk_audio_stream_missing")
    stream = streams[0]
    return {
        "codec": str(stream.get("codec_name") or "").lower(),
        "sample_rate": int(stream.get("sample_rate") or 0),
        "channels": int(stream.get("channels") or 0),
        "bits_per_sample": int(stream.get("bits_per_sample") or 0),
        "sample_fmt": str(stream.get("sample_fmt") or ""),
    }


def _expected_duration_ms(track: Track) -> int:
    if not track.chunks:
        return 0
    first = track.chunks[0].start_sample
    end = track.chunks[-1].start_sample + track.chunks[-1].sample_count
    return round(max(0, end - first) * 1000 / max(1, track.sample_rate))


def _validate_output(path: Path, track: Track) -> dict:
    details = _probe_audio_format(path)
    if details["codec"] != "flac" or details["sample_rate"] != track.sample_rate or details["channels"] != 1:
        raise ValueError(f"recording_track_output_format_invalid:{track.track_id}")
    actual_duration_ms = _probe_duration_ms(path)
    expected_duration_ms = _expected_duration_ms(track)
    tolerance_ms = max(250, int(os.getenv("AUDIO_TRACK_DRIFT_TOLERANCE_MS", "250")))
    if abs(actual_duration_ms - expected_duration_ms) > tolerance_ms:
        raise ValueError(
            f"recording_track_duration_mismatch:{track.track_id}:"
            f"expected={expected_duration_ms}:actual={actual_duration_ms}"
        )
    details["duration_ms"] = actual_duration_ms
    return details


def _concat_track(track: Track, output: Path) -> dict:
    if not track.chunks:
        raise ValueError(f"recording_track_empty:{track.track_id}")
    previous_end: int | None = None
    attempt = os.urandom(8).hex()
    list_path = output.with_name(f"{output.name}.{attempt}.concat.txt")
    temporary = output.with_name(f"{output.name}.{attempt}.part")
    lines: list[str] = []
    for expected_sequence, chunk in enumerate(track.chunks):
        if chunk.sequence != expected_sequence:
            raise ValueError(f"recording_chunk_sequence_gap:{track.track_id}:{expected_sequence}")
        source = _storage_path(chunk.storage_key)
        if not source.is_file():
            raise FileNotFoundError(source)
        actual_size = source.stat().st_size
        if actual_size != chunk.size_bytes:
            # A size mismatch is an explicit integrity exception: re-hash
            # only this object so truncation is not reported as a generic
            # missing chunk.
            if chunk.sha256 and _sha256(source).lower() != chunk.sha256.lower():
                raise ValueError(f"recording_chunk_checksum_mismatch:{track.track_id}:{chunk.sequence}")
            raise ValueError(f"recording_chunk_size_mismatch:{track.track_id}:{chunk.sequence}")
        # The client hash is authoritative for transport. Re-hash only when
        # it is absent; a supplied mismatch is an integrity failure and must
        # not be silently repaired by the server.
        if chunk.sha256:
            # RecordingFinalizeSupport verifies confirmed chunks before media
            # assembly. Trust the client digest on the normal path and avoid
            # a second full read; probe/decode failures below re-check it.
            pass
        else:
            # Older transport rows may omit the client digest. Compute it
            # once for the integrity record, without penalizing the normal
            # client-hash path with a second full read.
            _sha256(source)
        if previous_end is not None and chunk.start_sample != previous_end:
            raise ValueError(f"recording_chunk_sample_gap:{track.track_id}:{chunk.sequence}")
        previous_end = chunk.start_sample + chunk.sample_count
        escaped = str(source).replace("'", "'\\''")
        lines.append(f"file '{escaped}'")

    list_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    try:
        formats = [_probe_audio_format(_storage_path(chunk.storage_key)) for chunk in track.chunks]
    except (subprocess.CalledProcessError, ValueError):
        # A malformed media object is the other integrity-failure path. Only
        # then re-hash supplied client digests to surface a checksum error.
        for chunk in track.chunks:
            source = _storage_path(chunk.storage_key)
            if chunk.sha256 and _sha256(source).lower() != chunk.sha256.lower():
                raise ValueError(f"recording_chunk_checksum_mismatch:{track.track_id}:{chunk.sequence}")
        raise
    first = formats[0]
    compatible = all(
        item["codec"] == first["codec"]
        and item["sample_rate"] == first["sample_rate"]
        and item["channels"] == first["channels"]
        and item["bits_per_sample"] == first["bits_per_sample"]
        for item in formats[1:]
    ) and first["codec"] == "flac" and first["sample_rate"] == track.sample_rate and first["channels"] == 1
    started = time.perf_counter()
    method = "STREAM_COPY" if compatible else "REENCODE_FALLBACK"
    try:
        # The atomic target intentionally ends in `.part`, so ffmpeg cannot
        # infer the muxer from the filename on Windows.  Keep the target
        # extension-independent and declare the output container explicitly.
        try:
            _run_ffmpeg(["-f", "concat", "-safe", "0", "-i", str(list_path), "-c:a", "copy" if compatible else "flac", "-f", "flac", str(temporary)])
            if compatible:
                _validate_output(temporary, track)
        except (subprocess.CalledProcessError, ValueError):
            if not compatible:
                raise
            # A valid-looking set of FLAC headers can still be rejected by
            # concat when stream metadata differs. Re-encode safely.
            temporary.unlink(missing_ok=True)
            method = "REENCODE_FALLBACK"
            _run_ffmpeg(["-f", "concat", "-safe", "0", "-i", str(list_path), "-c:a", "flac", "-f", "flac", str(temporary)])
            _validate_output(temporary, track)
        if not temporary.is_file() or temporary.stat().st_size == 0:
            raise ValueError(f"recording_track_output_empty:{track.track_id}")
        temporary.replace(output)
    finally:
        list_path.unlink(missing_ok=True)
        temporary.unlink(missing_ok=True)
    return {"method": method, "duration_ms": _probe_duration_ms(output), "elapsed_ms": round((time.perf_counter() - started) * 1000)}


def _load_tracks(session_id: str) -> list[Track]:
    with psycopg.connect(_conninfo()) as connection:
        rows = connection.execute(
            """
            SELECT t.id::text,t.track_type,t.device_id,t.device_name,t.selection_mode,t.recording_profile,t.encoding,t.bits_per_sample,t.sample_rate,c.sequence,c.storage_key,c.start_sample,c.sample_count,c.size_bytes,c.sha256
            FROM recording_tracks t
            LEFT JOIN recording_chunks c ON c.track_id=t.id AND c.status='CONFIRMED'
            WHERE t.session_id=%s
            ORDER BY t.id,c.sequence
            """,
            (session_id,),
        ).fetchall()

    grouped: dict[str, tuple[tuple, list[Chunk]]] = {}
    for track_id, track_type, device_id, device_name, selection_mode, recording_profile, encoding, bits_per_sample, sample_rate, sequence, storage_key, start_sample, sample_count, size_bytes, sha256 in rows:
        if track_id not in grouped:
            grouped[track_id] = ((track_type, device_id, device_name, selection_mode, recording_profile, encoding, bits_per_sample, int(sample_rate or 48000)), [])
        if sequence is not None:
            grouped[track_id][1].append(Chunk(sequence, storage_key, start_sample, sample_count, size_bytes, sha256))
    return [Track(track_id=track_id, track_type=metadata[0], device_id=metadata[1], device_name=metadata[2], selection_mode=metadata[3], recording_profile=metadata[4], encoding=metadata[5], bits_per_sample=metadata[6], sample_rate=metadata[7], chunks=tuple(chunks)) for track_id, (metadata, chunks) in grouped.items()]


def _timeline_metadata(track: Track, path: Path, base_start_sample: int) -> dict:
    if not track.chunks:
        return {
            "track_id": track.track_id,
            "expected_duration_ms": 0,
            "actual_duration_ms": 0,
            "start_offset_ms": 0,
            "drift_ms": 0,
            "sample_rate": track.sample_rate,
            "channels": 1,
        }
    first = track.chunks[0].start_sample
    end = track.chunks[-1].start_sample + track.chunks[-1].sample_count
    rate = max(1, track.sample_rate)
    expected = round((end - first) * 1000 / rate)
    actual = _probe_duration_ms(path)
    return {
        "track_id": track.track_id,
        "expected_duration_ms": expected,
        "actual_duration_ms": actual,
        "start_offset_ms": round((first - base_start_sample) * 1000 / rate),
        "drift_ms": actual - expected,
        "sample_rate": track.sample_rate,
        "channels": 1,
    }


def _track_role(track_type: str) -> str:
    normalized = str(track_type or "").lower()
    if "microphone" in normalized or "room" in normalized:
        return "room_microphone"
    if "system" in normalized or "loopback" in normalized or "render" in normalized:
        return "system_audio"
    return "auxiliary"


def _relative_output_name(path: Path, output_dir: Path) -> str:
    """Return a portable storage reference, never an absolute host path."""
    return path.relative_to(output_dir).as_posix()


def _media_storage_key(path: Path, output_dir: Path) -> str:
    """Return a logical media-root key without leaking a host filesystem path."""
    media_root = Path(os.getenv("MEDIA_ROOT", "/data"))
    try:
        return path.relative_to(media_root).as_posix()
    except ValueError:
        # Unit/fake-storage paths may live outside MEDIA_ROOT. Keep a stable
        # job-local reference in that case; production uses the branch above.
        return f"{output_dir.name}/{path.name}"


def _build_track_files(
    tracks: list[Track],
    assembled: list[Path],
    timeline: list[dict],
    assembly_methods: list[dict],
    output_dir: Path,
    selected_path: Path | None,
) -> list[dict]:
    files: list[dict] = []
    for track, path, item, method in zip(tracks, assembled, timeline, assembly_methods):
        files.append(
            {
                "track_id": track.track_id,
                "track_type": track.track_type,
                "track_role": _track_role(track.track_type),
                "device_id": track.device_id,
                "device_name": track.device_name,
                "selection_mode": track.selection_mode,
                "recording_profile": track.recording_profile,
                "relative_path": _relative_output_name(path, output_dir),
                "storage_key": _media_storage_key(path, output_dir),
                "sample_rate": int(track.sample_rate),
                "channels": 1,
                "duration_ms": int(item.get("actual_duration_ms", 0)),
                "start_offset_ms": int(item.get("start_offset_ms", 0)),
                "drift_ms": int(item.get("drift_ms", 0)),
                "assembly_method": method.get("method"),
                "selected_for_asr": selected_path == path,
                "retained_for_diagnostics": True,
            }
        )
    return files


def assemble_recording_session(session_id: str, output_dir: Path) -> Path:
    tracks = _load_tracks(session_id)
    if not tracks:
        raise ValueError("recording_tracks_required")
    output_dir.mkdir(parents=True, exist_ok=True)
    (output_dir / "assembly-input.json").write_text(json.dumps({"recording_tracks": [{"track_id": track.track_id, "track_type": track.track_type, "device_id": track.device_id, "device_name": track.device_name, "selection_mode": track.selection_mode, "recording_profile": track.recording_profile, "encoding": track.encoding, "bits_per_sample": track.bits_per_sample} for track in tracks]}, ensure_ascii=False), encoding="utf-8")
    assembled: list[Path] = []
    assembly_methods: list[dict] = []
    for track in tracks:
        output = output_dir / f"track-{track.track_id}.flac"
        assembly_methods.append(_concat_track(track, output))
        assembled.append(output)

    profile = next((str(track.recording_profile).upper() for track in tracks if track.recording_profile), "ROOM")
    microphone = next((path for track, path in zip(tracks, assembled) if "microphone" in track.track_type.lower()), None)
    system = next((path for track, path in zip(tracks, assembled) if "system" in track.track_type.lower() or "loopback" in track.track_type.lower()), None)
    first_start = min((track.chunks[0].start_sample for track in tracks if track.chunks), default=0)
    timeline = [_timeline_metadata(track, path, first_start) for track, path in zip(tracks, assembled)]
    drift_tolerance = int(os.getenv("AUDIO_TRACK_DRIFT_TOLERANCE_MS", "250"))
    warnings = ["AUDIO_TRACK_DRIFT_HIGH" for item in timeline if abs(item["drift_ms"]) > drift_tolerance]
    high_drift_online = profile == "ONLINE" and bool(warnings)
    selected_asr_source = "microphone" if profile in {"ROOM", "MIC_ONLY"} and microphone else "loopback" if profile == "SYSTEM_ONLY" and system else "controlled_mix" if profile == "ONLINE" else "single_track"
    mix_strategy = "controlled_online_mix" if profile == "ONLINE" and len(assembled) > 1 else "single_original_track"
    master_kind = "DERIVED_MIX_NO_AEC" if mix_strategy == "controlled_online_mix" else "CANONICAL_TRACK"
    source: Path | None = None
    if high_drift_online:
        # Drift makes a synchronized mix unsafe. Keep the original tracks and
        # continue with the first valid preferred source instead of losing the
        # transcript entirely.
        source = microphone or system or next((path for path in assembled if path.is_file()), None)
        if source is None:
            raise ValueError("recording_source_unavailable")
        selected_asr_source = "microphone" if source == microphone else "loopback" if source == system else "single_track"
        mix_strategy = "online_single_track_fallback"
        warnings.append("ONLINE_MIX_DEGRADED")
    elif profile in {"ROOM", "MIC_ONLY"} and microphone is not None:
        source = microphone
    elif profile == "SYSTEM_ONLY" and system is not None:
        source = system
    elif len(assembled) == 1:
        source = assembled[0]
    assembly_result = {
        "schema_version": 1,
        "session_id": session_id,
        "tracks_are_independent": True,
        "recording_profile": profile,
        "track_count": len(tracks),
        "selected_asr_source": selected_asr_source,
        "mix_strategy": mix_strategy,
        "master_kind": master_kind,
        "echo_cancellation": "NONE",
        "drift_tolerance_ms": drift_tolerance,
        "tracks": timeline,
        "warnings": sorted(set(warnings)),
        "assembly": [
            {"track_id": track.track_id, **method}
            for track, method in zip(tracks, assembly_methods)
        ],
        "track_files": _build_track_files(tracks, assembled, timeline, assembly_methods, output_dir, source),
        "asr_input": None,
        "manifest": {
            "relative_path": "assembly-result.json",
            "storage_key": _media_storage_key(output_dir / "assembly-result.json", output_dir),
        },
    }
    if warnings and not high_drift_online:
        (output_dir / "assembly-result.json").write_text(json.dumps(assembly_result, ensure_ascii=False, indent=2), encoding="utf-8")
        raise ValueError("AUDIO_TRACK_DRIFT_HIGH")
    if source is None:
        source = output_dir / "mixed.flac"
        inputs: list[str] = []
        for path in assembled:
            inputs.extend(["-i", str(path)])
        chains = []
        for index, item in enumerate(timeline):
            delay = max(0, int(item["start_offset_ms"]))
            chains.append(f"[{index}:a]aresample=48000:async=1000:first_pts=0,adelay={delay}:all=1[a{index}]")
        labels = "".join(f"[a{index}]" for index in range(len(assembled)))
        filter_spec = ";".join(chains) + f";{labels}amix=inputs={len(assembled)}:duration=longest:dropout_transition=2:normalize=1,alimiter=limit=0.95,aresample=48000[mix]"
        temporary = source.with_name(f"{source.name}.{os.urandom(8).hex()}.part")
        try:
            _run_ffmpeg([*inputs, "-filter_complex", filter_spec, "-map", "[mix]", "-ac", "1", "-c:a", "flac", "-f", "flac", str(temporary)])
            if not temporary.is_file() or temporary.stat().st_size == 0:
                raise ValueError("recording_mix_output_empty")
            temporary.replace(source)
        finally:
            temporary.unlink(missing_ok=True)
    assembly_result["asr_input"] = {
        "relative_path": _relative_output_name(source, output_dir),
        "storage_key": _media_storage_key(source, output_dir),
        "source_kind": "derived_mix" if source.name == "mixed.flac" else "independent_track",
        "selected_asr_source": selected_asr_source,
    }
    # The mix (when required) is produced only after every independent track
    # has been assembled and validated. Never mark source tracks as replaced.
    if source.name == "mixed.flac":
        for item in assembly_result["track_files"]:
            item["selected_for_asr"] = False
    (output_dir / "assembly-result.json").write_text(json.dumps(assembly_result, ensure_ascii=False, indent=2), encoding="utf-8")
    return source
