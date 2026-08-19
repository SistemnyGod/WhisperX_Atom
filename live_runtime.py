from __future__ import annotations

import importlib.util
import os
import queue
import threading
import time
import wave
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Callable

from local_io import atomic_write_json
from processing_runtime import now_iso


LIVE_SESSIONS_DIR = Path("whisperx_results") / "live_sessions"


class LiveStatus:
    RECORDING = "recording"
    PAUSED = "paused"
    PROCESSING = "processing"
    FINALIZING = "finalizing"
    DONE = "done"
    FAILED = "failed"


class LiveChunkStatus:
    RECORDING = "recording"
    QUEUED = "queued"
    PROCESSING = "processing"
    DONE = "done"
    FAILED = "failed"


@dataclass
class LiveChunk:
    chunk_id: int
    started_at: str
    finished_at: str = ""
    audio_path: str = ""
    status: str = LiveChunkStatus.RECORDING
    asr_json_path: str = ""
    text: str = ""
    segments: list[dict[str, Any]] = field(default_factory=list)
    global_offset_sec: float = 0.0
    speaker_map: dict[str, str] = field(default_factory=dict)
    error: str = ""


@dataclass
class LiveSession:
    session_id: str
    root_dir: str
    started_at: str
    finished_at: str = ""
    status: str = LiveStatus.RECORDING
    profile: str = "meeting"
    chunks: list[LiveChunk] = field(default_factory=list)
    partial_transcript: str = ""
    speaker_registry: dict[str, Any] = field(default_factory=dict)
    final_output_paths: dict[str, str] = field(default_factory=dict)
    error: str = ""


class SpeakerRegistry:
    def __init__(self, state: dict[str, Any] | None = None):
        state = state or {}
        self._local_to_stable: dict[str, str] = dict(state.get("local_to_stable") or {})
        self._stable_names: dict[str, str] = dict(state.get("stable_names") or {})
        self._next_index = int(state.get("next_index") or 1)

    def map_speaker(self, local_speaker: str) -> str:
        local = str(local_speaker or "UNKNOWN")
        if local in {"UNKNOWN", "None", ""}:
            return "UNKNOWN"
        if local not in self._local_to_stable:
            stable_id = f"speaker_{self._next_index:02d}"
            self._next_index += 1
            self._local_to_stable[local] = stable_id
            self._stable_names[stable_id] = f"Спикер {len(self._stable_names) + 1}"
        return self._stable_names[self._local_to_stable[local]]

    def apply_to_segments(self, segments: list[dict[str, Any]]) -> tuple[list[dict[str, Any]], dict[str, str]]:
        speaker_map: dict[str, str] = {}
        updated: list[dict[str, Any]] = []
        for segment in segments:
            item = dict(segment)
            original = str(item.get("speaker") or "UNKNOWN")
            stable = self.map_speaker(original)
            speaker_map[original] = stable
            item["speaker_original"] = original
            item["speaker"] = stable
            for word in item.get("words") or []:
                if isinstance(word, dict):
                    word["speaker"] = stable
            updated.append(item)
        return updated, speaker_map

    def to_dict(self) -> dict[str, Any]:
        return {
            "local_to_stable": dict(self._local_to_stable),
            "stable_names": dict(self._stable_names),
            "next_index": self._next_index,
        }


class LiveSessionStore:
    def __init__(self, base_dir: Path = LIVE_SESSIONS_DIR):
        self.base_dir = Path(base_dir)

    def create_session(self, *, profile: str) -> LiveSession:
        session_id = f"live_{time.strftime('%Y%m%d_%H%M%S')}"
        root = self.base_dir / session_id
        suffix = 1
        while root.exists():
            suffix += 1
            session_id = f"live_{time.strftime('%Y%m%d_%H%M%S')}_{suffix:02d}"
            root = self.base_dir / session_id
        (root / "chunks").mkdir(parents=True, exist_ok=True)
        (root / "final").mkdir(parents=True, exist_ok=True)
        session = LiveSession(
            session_id=session_id,
            root_dir=str(root),
            started_at=now_iso(),
            profile=profile,
        )
        self.write_session(session)
        return session

    def session_path(self, session: LiveSession) -> Path:
        return Path(session.root_dir) / "session.json"

    def chunks_dir(self, session: LiveSession) -> Path:
        path = Path(session.root_dir) / "chunks"
        path.mkdir(parents=True, exist_ok=True)
        return path

    def final_dir(self, session: LiveSession) -> Path:
        path = Path(session.root_dir) / "final"
        path.mkdir(parents=True, exist_ok=True)
        return path

    def write_session(self, session: LiveSession) -> None:
        payload = asdict(session)
        atomic_write_json(self.session_path(session), payload)

    def create_chunk(self, session: LiveSession, *, global_offset_sec: float) -> LiveChunk:
        chunk_id = len(session.chunks) + 1
        audio_path = self.chunks_dir(session) / f"chunk_{chunk_id:04d}.wav"
        chunk = LiveChunk(
            chunk_id=chunk_id,
            started_at=now_iso(),
            audio_path=str(audio_path),
            global_offset_sec=global_offset_sec,
        )
        session.chunks.append(chunk)
        self.write_session(session)
        return chunk

    def update_chunk(self, session: LiveSession, chunk: LiveChunk, **fields: Any) -> None:
        for key, value in fields.items():
            if hasattr(chunk, key):
                setattr(chunk, key, value)
        self.write_session(session)

    def append_partial_transcript(self, session: LiveSession, text: str) -> None:
        text = text.strip()
        if text:
            session.partial_transcript = (session.partial_transcript + "\n" + text).strip()
        self.write_session(session)

    def update_speaker_registry(self, session: LiveSession, registry: SpeakerRegistry) -> None:
        session.speaker_registry = registry.to_dict()
        self.write_session(session)

    def set_status(self, session: LiveSession, status: str, *, error: str = "") -> None:
        session.status = status
        if error:
            session.error = error
        if status in {LiveStatus.DONE, LiveStatus.FAILED} and not session.finished_at:
            session.finished_at = now_iso()
        self.write_session(session)

    def set_final_outputs(self, session: LiveSession, output_paths: dict[str, str]) -> None:
        session.final_output_paths = dict(output_paths)
        self.write_session(session)


class SoundDeviceChunkRecorder:
    """Continuous microphone capture for the legacy live UI.

    The installed Recorder uses AudioGraph, but the legacy Tk live view still
    has to remain safe when it is used directly.  Keep one sounddevice
    ``InputStream`` open for the whole session and rotate only the processing
    files.  The callback does not touch disk: it copies a bounded block into a
    queue, while the recording thread writes and checkpoints the current WAV.
    ``recorded_samples`` is the media clock, so a pause never creates a fake
    wall-clock gap in transcript offsets.
    """

    def __init__(self, *, sample_rate: int = 16000, channels: int = 1):
        self.sample_rate = int(sample_rate)
        self.channels = int(channels)

    @staticmethod
    def is_available() -> bool:
        return importlib.util.find_spec("sounddevice") is not None

    def record_chunk(self, path: Path, seconds: float) -> None:
        """Record one file through the same continuous-stream implementation.

        This compatibility method intentionally has no ``sd.rec`` fallback.
        Tests and callers that only need one clip still receive the same
        callback-based behaviour as a meeting session.
        """
        stop = threading.Event()
        result: list[Path] = []

        def on_chunk_ready(final_path: Path, _start_sample: int, _sample_count: int) -> None:
            result.append(final_path)

        self.record_session(
            path.parent,
            max(0.1, float(seconds)),
            stop_event=stop,
            pause_event=threading.Event(),
            on_chunk_ready=on_chunk_ready,
            file_prefix=path.stem,
            duration_seconds=max(0.1, float(seconds)),
        )
        if not result:
            raise RuntimeError("Запись не создала аудиофайл")
        produced = result[0]
        if produced != path:
            path.parent.mkdir(parents=True, exist_ok=True)
            produced.replace(path)

    def record_session(
        self,
        chunks_dir: Path,
        chunk_seconds: float,
        *,
        stop_event: threading.Event,
        pause_event: threading.Event,
        on_chunk_ready: Callable[[Path, int, int], None],
        cancel_event: threading.Event | None = None,
        file_prefix: str = "chunk",
        duration_seconds: float | None = None,
    ) -> int:
        """Capture a session with one ``sounddevice.InputStream``.

        ``on_chunk_ready`` is called only after a file is closed and atomically
        promoted from ``.part``.  It receives ``(path, start_sample,
        sample_count)``; callers should derive transcript offsets from those
        samples instead of ``time.monotonic()``.  The queue is bounded so an
        overloaded disk fails loudly rather than silently losing meeting
        audio.
        """
        if not self.is_available():
            raise RuntimeError("Для live-записи установите зависимость sounddevice")
        import sounddevice as sd

        chunks_dir = Path(chunks_dir)
        chunks_dir.mkdir(parents=True, exist_ok=True)
        chunk_frames = max(1, int(float(chunk_seconds) * self.sample_rate))
        bytes_per_frame = self.channels * 2
        blocks: queue.Queue[bytes] = queue.Queue(maxsize=128)
        stream_error: list[str] = []
        stream_failed = threading.Event()

        def callback(indata, _frames, _time_info, status) -> None:
            if status:
                stream_error.append(str(status))
                stream_failed.set()
            # A paused stream stays open but does not advance the media clock.
            if pause_event.is_set() or stop_event.is_set() or (cancel_event and cancel_event.is_set()):
                return
            try:
                payload = indata.tobytes()
                if payload:
                    blocks.put_nowait(payload)
            except queue.Full:
                stream_error.append("AUDIO_INPUT_QUEUE_OVERRUN")
                stream_failed.set()

        stream = None
        writer: wave.Wave_write | None = None
        part_path: Path | None = None
        final_path: Path | None = None
        current_start_sample = 0
        current_samples = 0
        recorded_samples = 0
        sequence = 1
        last_checkpoint = time.monotonic()
        checkpoint_seconds = 5.0

        def checkpoint() -> None:
            if writer is None:
                return
            # wave keeps the data length in the RIFF header until close.  The
            # private helper is present on CPython's Wave_write and lets crash
            # recovery open the durable prefix, while fsync bounds the lost
            # tail to the checkpoint interval.
            patch_header = getattr(writer, "_patchheader", None)
            if callable(patch_header):
                patch_header()
            file_obj = getattr(writer, "_file", None)
            if file_obj is not None:
                file_obj.flush()
                os.fsync(file_obj.fileno())

        def open_chunk() -> None:
            nonlocal writer, part_path, final_path, current_start_sample, current_samples, sequence
            name = f"{file_prefix}_{sequence:04d}.wav"
            part_path = chunks_dir / f"{name}.part"
            final_path = chunks_dir / name
            writer = wave.open(str(part_path), "wb")
            writer.setnchannels(self.channels)
            writer.setsampwidth(2)
            writer.setframerate(self.sample_rate)
            current_start_sample = recorded_samples
            current_samples = 0

        def close_chunk() -> None:
            nonlocal writer, part_path, final_path, current_samples, sequence
            if writer is None or part_path is None or final_path is None or current_samples <= 0:
                if writer is not None:
                    writer.close()
                writer = None
                part_path = None
                final_path = None
                return
            checkpoint()
            writer.close()
            part_path.replace(final_path)
            ready_path = final_path
            ready_start = current_start_sample
            ready_count = current_samples
            writer = None
            part_path = None
            final_path = None
            current_samples = 0
            sequence += 1
            on_chunk_ready(ready_path, ready_start, ready_count)

        deadline = None if duration_seconds is None else time.monotonic() + max(0.1, float(duration_seconds))
        try:
            stream = sd.InputStream(
                samplerate=self.sample_rate,
                channels=self.channels,
                dtype="int16",
                callback=callback,
            )
            stream.start()
            while not stop_event.is_set() and not (cancel_event and cancel_event.is_set()):
                if deadline is not None and time.monotonic() >= deadline:
                    break
                if stream_failed.is_set():
                    raise RuntimeError(stream_error[-1] if stream_error else "AUDIO_INPUT_STREAM_FAILED")
                try:
                    payload = blocks.get(timeout=0.1)
                except queue.Empty:
                    continue
                usable = len(payload) - (len(payload) % bytes_per_frame)
                if usable <= 0:
                    continue
                offset = 0
                while offset < usable:
                    if writer is None:
                        open_chunk()
                    frames_available = (usable - offset) // bytes_per_frame
                    frames_to_write = min(frames_available, chunk_frames - current_samples)
                    byte_count = frames_to_write * bytes_per_frame
                    writer.writeframes(payload[offset : offset + byte_count])
                    current_samples += frames_to_write
                    recorded_samples += frames_to_write
                    offset += byte_count
                    if time.monotonic() - last_checkpoint >= checkpoint_seconds:
                        checkpoint()
                        last_checkpoint = time.monotonic()
                    if current_samples >= chunk_frames:
                        close_chunk()
            # Stop the stream before draining the queue; callbacks are no
            # longer allowed to enqueue frames after this point.
            if stream is not None:
                stream.stop()
            while True:
                try:
                    payload = blocks.get_nowait()
                except queue.Empty:
                    break
                usable = len(payload) - (len(payload) % bytes_per_frame)
                if usable <= 0:
                    continue
                if writer is None:
                    open_chunk()
                writer.writeframes(payload[:usable])
                frames = usable // bytes_per_frame
                current_samples += frames
                recorded_samples += frames
                if current_samples >= chunk_frames:
                    close_chunk()
            close_chunk()
            return recorded_samples
        finally:
            if stream is not None:
                try:
                    stream.stop()
                except Exception:
                    pass
                try:
                    stream.close()
                except Exception:
                    pass
            if writer is not None:
                try:
                    close_chunk()
                except Exception:
                    try:
                        writer.close()
                    except Exception:
                        pass


def concatenate_wav_files(paths: list[Path], output_path: Path) -> Path:
    if not paths:
        raise RuntimeError("Нет аудиочанков для финальной сборки")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    params = None
    with wave.open(str(output_path), "wb") as writer:
        for path in paths:
            with wave.open(str(path), "rb") as reader:
                current_params = reader.getparams()
                if params is None:
                    params = current_params
                    writer.setparams(current_params)
                elif current_params[:3] != params[:3]:
                    raise RuntimeError(f"Несовместимые WAV-параметры чанка: {path}")
                writer.writeframes(reader.readframes(reader.getnframes()))
    return output_path
