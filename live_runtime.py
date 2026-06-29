from __future__ import annotations

import importlib.util
import time
import wave
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

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
    def __init__(self, *, sample_rate: int = 16000, channels: int = 1):
        self.sample_rate = int(sample_rate)
        self.channels = int(channels)

    @staticmethod
    def is_available() -> bool:
        return importlib.util.find_spec("sounddevice") is not None

    def record_chunk(self, path: Path, seconds: float) -> None:
        if not self.is_available():
            raise RuntimeError("Для live-записи установите зависимость sounddevice")
        import sounddevice as sd

        frames = int(float(seconds) * self.sample_rate)
        data = sd.rec(frames, samplerate=self.sample_rate, channels=self.channels, dtype="int16")
        sd.wait()
        path.parent.mkdir(parents=True, exist_ok=True)
        with wave.open(str(path), "wb") as wav:
            wav.setnchannels(self.channels)
            wav.setsampwidth(2)
            wav.setframerate(self.sample_rate)
            wav.writeframes(data.tobytes())


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
