from __future__ import annotations

import asyncio
import copy
import json
import gc
import logging
import os
import re
import subprocess
import threading
import time
import unicodedata
import warnings
from collections import OrderedDict
from pathlib import Path
from queue import Empty, Queue
from typing import Any, Callable

import customtkinter as ctk
import torch
import whisperx
from tkinter import Menu, filedialog, messagebox
from whisperx.diarize import DiarizationPipeline as WhisperXDiarizationPipeline

from diarization_quality import (
    choose_best_diarization_candidate,
    diarization_profiles_for_processing_profile,
    score_diarization_result,
    smooth_speaker_turns,
)
from glossary_utils import apply_glossary_rules, load_glossary_text, load_hotwords_text, parse_glossary_rules
from live_runtime import (
    LiveChunk,
    LiveChunkStatus,
    LiveSession,
    LiveSessionStore,
    LiveStatus,
    SoundDeviceChunkRecorder,
    SpeakerRegistry,
    concatenate_wav_files,
)
from local_io import atomic_write_json, atomic_write_text
from media_binaries import MissingBinaryError, media_has_audio_stream, require_binary, require_ffmpeg_tools
from processing_runtime import (
    AUDIO_EXTENSIONS,
    MEDIA_EXTENSIONS,
    PROFILE_LABELS,
    VIDEO_EXTENSIONS,
    RunJournal,
    RunStage,
    RunStatus,
    STAGE_LABELS_RU,
    bounded_auto_model,
    delete_history_item,
    apply_speaker_mapping_to_history_item,
    export_history_day_texts,
    export_history_item_text,
    format_history_browser_entry,
    format_speaker_mapping_template,
    ensure_text_file,
    format_quality_report,
    format_run_history_details,
    get_processing_profile,
    group_history_by_day,
    history_item_day,
    parse_speaker_mapping_text,
    now_iso,
    quality_from_result,
    retry_source_from_history_item,
    scan_run_history,
    unique_result_paths,
)
from runtime_secrets import load_hf_token_from_env

SETTINGS_PATH = Path("whisperx_gui_settings.json")
RESULTS_DIR = Path("whisperx_results")
MEDIA_FILETYPES = [
    ("Media", " ".join(f"*{suffix}" for suffix in sorted(MEDIA_EXTENSIONS))),
    ("Audio", " ".join(f"*{suffix}" for suffix in sorted(AUDIO_EXTENSIONS))),
    ("Video", " ".join(f"*{suffix}" for suffix in sorted(VIDEO_EXTENSIONS))),
    ("All", "*.*"),
]


warnings.filterwarnings(
    "ignore",
    message=r".*torchaudio\._backend\.list_audio_backends has been deprecated.*",
    category=UserWarning,
)
try:
    from pyannote.audio.utils.reproducibility import ReproducibilityWarning

    warnings.filterwarnings("ignore", category=ReproducibilityWarning)
except Exception:
    pass

logging.getLogger("lightning.pytorch.utilities.migration.utils").setLevel(logging.ERROR)
logging.getLogger("lightning.pytorch.utilities.migration").setLevel(logging.ERROR)

try:
    from pyannote.audio.utils import version as pyannote_version_utils

    def _silent_pyannote_check_version(*args, **kwargs):
        return None

    pyannote_version_utils.check_version = _silent_pyannote_check_version
except Exception:
    pass


class TaskManager:
    def __init__(self, on_idle=None, on_error=None):
        self._queue: Queue[Any] = Queue()
        self._on_idle = on_idle
        self._on_error = on_error
        self._current_id: int | None = None
        self._current_cancel: threading.Event | None = None
        self._next_id = 1
        self._cancel_map: dict[int, threading.Event] = {}
        self._lock = threading.Lock()
        self._running = True
        self._worker = threading.Thread(target=self._loop, daemon=True)
        self._worker.start()

    def submit(self, fn):
        with self._lock:
            job_id = self._next_id
            self._next_id += 1
        cancel = threading.Event()
        with self._lock:
            self._cancel_map[job_id] = cancel
        self._queue.put((job_id, fn, cancel))
        return job_id

    def cancel(self, job_id: int) -> bool:
        with self._lock:
            cancel = self._cancel_map.get(job_id)
        if cancel is None:
            return False
        cancel.set()
        return True

    def busy(self) -> bool:
        return self._current_id is not None or not self._queue.empty()

    def stop(self):
        self._running = False
        self._queue.put(None)

    def _loop(self):
        while self._running:
            try:
                item = self._queue.get(timeout=0.1)
            except Empty:
                continue
            if item is None:
                continue
            job_id, fn, cancel = item
            if cancel.is_set():
                with self._lock:
                    self._cancel_map.pop(job_id, None)
                self._queue.task_done()
                if self._on_idle:
                    self._on_idle(job_id)
                continue
            self._current_id = job_id
            self._current_cancel = cancel
            try:
                fn(cancel)
            except Exception as exc:
                if self._on_error:
                    self._on_error(job_id, exc)
            finally:
                with self._lock:
                    self._cancel_map.pop(job_id, None)
                self._current_id = None
                self._current_cancel = None
                self._queue.task_done()
                if self._on_idle:
                    self._on_idle(job_id)


class ModelCacheManager:
    def __init__(self, max_models: int = 2, max_diarizers: int = 2):
        self.max_models = max(1, int(max_models))
        self.max_diarizers = max(1, int(max_diarizers))
        self._models: OrderedDict[str, Any] = OrderedDict()
        self._diarizers: OrderedDict[str, Any] = OrderedDict()
        self._lock = threading.Lock()

    def _evict_if_needed(self):
        while len(self._models) > self.max_models:
            self._models.popitem(last=False)
        while len(self._diarizers) > self.max_diarizers:
            self._diarizers.popitem(last=False)

    def _release_memory(self):
        gc.collect()
        if torch.cuda.is_available():
            try:
                torch.cuda.synchronize()
            except Exception:
                pass
            torch.cuda.empty_cache()

    def get_or_load_model(self, key: str, loader: Callable[[], Any]) -> Any:
        evicted = None
        with self._lock:
            if key in self._models:
                self._models.move_to_end(key)
                return self._models[key]
            if len(self._models) >= self.max_models:
                _, evicted = self._models.popitem(last=False)
        if evicted is not None:
            try:
                del evicted
            except Exception:
                pass
            self._release_memory()
        obj = loader()
        with self._lock:
            self._models[key] = obj
            self._models.move_to_end(key)
            self._evict_if_needed()
            return obj

    def get_or_load_diarizer(self, key: str, loader: Callable[[], Any]) -> Any:
        with self._lock:
            if key in self._diarizers:
                self._diarizers.move_to_end(key)
                return self._diarizers[key]
        obj = loader()
        with self._lock:
            self._diarizers[key] = obj
            self._diarizers.move_to_end(key)
            self._evict_if_needed()
            return obj

    def clear_all(self):
        with self._lock:
            self._models.clear()
            self._diarizers.clear()


class FasterWhisperAdapter:
    def __init__(
        self,
        model_name: str,
        device: str,
        compute_type: str,
        beam_size: int,
        language: str | None,
        vad_onset: float,
        vad_filter: bool,
        initial_prompt: str,
        hotwords: str,
    ):
        try:
            from faster_whisper import WhisperModel
        except Exception as exc:
            raise RuntimeError(
                "Backend faster-whisper недоступен. Установите пакет faster-whisper."
            ) from exc

        self.model = WhisperModel(model_name, device=device, compute_type=compute_type)
        self.beam_size = int(beam_size)
        self.language = language
        self.vad_onset = float(vad_onset)
        self.vad_filter = bool(vad_filter)
        self.initial_prompt = initial_prompt or ""
        self.hotwords = hotwords or ""

    def transcribe(self, audio, batch_size: int = 8, word_timestamps: bool = True):
        _ = batch_size
        kwargs = {
            "beam_size": self.beam_size,
            "word_timestamps": word_timestamps,
            "vad_filter": self.vad_filter,
            "vad_parameters": {"threshold": self.vad_onset},
            "initial_prompt": self.initial_prompt or None,
            "hotwords": self.hotwords or None,
        }
        if self.language:
            kwargs["language"] = self.language
        segments_iter, info = self.model.transcribe(audio, **kwargs)

        out_segments: list[dict[str, Any]] = []
        for seg in segments_iter:
            words = []
            if getattr(seg, "words", None):
                for w in seg.words:
                    token = str(getattr(w, "word", "") or "").strip()
                    words.append(
                        {
                            "start": float(getattr(w, "start", getattr(seg, "start", 0.0))),
                            "end": float(getattr(w, "end", getattr(seg, "end", 0.0))),
                            "word": token,
                            "text": token,
                        }
                    )
            out_segments.append(
                {
                    "start": float(getattr(seg, "start", 0.0)),
                    "end": float(getattr(seg, "end", 0.0)),
                    "text": str(getattr(seg, "text", "") or "").strip(),
                    "words": words,
                }
            )

        return {
            "language": str(getattr(info, "language", "") or ""),
            "segments": out_segments,
        }


class WhisperXService:
    def __init__(
        self,
        log_cb: Callable[[str], None],
        progress_cb: Callable[[float], None],
        stage_cb: Callable[[str], None] | None = None,
    ):
        self.log = log_cb
        self._progress_cb = progress_cb
        self._stage_cb = stage_cb
        self.cache = ModelCacheManager(max_models=2, max_diarizers=2)
        self.temp_files: list[Path] = []
        self.project_root = Path(__file__).resolve().parent

    def _set_progress(self, value: float):
        try:
            self._progress_cb(value)
        except Exception:
            pass

    def _set_stage(self, stage: str, progress: float | None = None):
        try:
            if self._stage_cb:
                self._stage_cb(stage)
        except Exception:
            pass
        if progress is not None:
            self._set_progress(progress)

    def cleanup_temp_files(self):
        for temp_file in self.temp_files:
            try:
                if temp_file.exists():
                    temp_file.unlink()
            except Exception:
                pass
        self.temp_files.clear()

    def release_resources(self, clear_models: bool = False):
        if clear_models:
            self.cache.clear_all()
        gc.collect()
        if torch.cuda.is_available():
            try:
                torch.cuda.synchronize()
            except Exception:
                pass
            torch.cuda.empty_cache()

    def _maybe_torch_compile(self, model: Any, request: dict[str, Any]) -> Any:
        if not request.get("use_torch_compile"):
            return model
        if request.get("device") != "cuda":
            return model
        if not hasattr(torch, "compile"):
            self.log("torch.compile unavailable in current torch build")
            return model
        if getattr(model, "_torch_compiled", False):
            return model

        candidates = []
        for attr in ("model", "asr_model", "net"):
            obj = getattr(model, attr, None)
            if obj is not None:
                candidates.append((attr, obj))
        root_model = getattr(model, "model", None)
        if root_model is not None:
            nested = getattr(root_model, "model", None)
            if nested is not None:
                candidates.append(("model.model", nested))

        compiled = False
        for attr_name, candidate in candidates:
            if not isinstance(candidate, torch.nn.Module):
                continue
            try:
                compiled_candidate = torch.compile(candidate, mode="reduce-overhead")
                if attr_name == "model":
                    model.model = compiled_candidate
                elif attr_name == "asr_model":
                    model.asr_model = compiled_candidate
                elif attr_name == "net":
                    model.net = compiled_candidate
                elif attr_name == "model.model":
                    model.model.model = compiled_candidate
                compiled = True
            except Exception as exc:
                self.log(f"torch.compile skipped for {attr_name}: {exc}")

        if compiled:
            setattr(model, "_torch_compiled", True)
            self.log("torch.compile enabled")
        return model

    def _model(self, request: dict[str, Any], model_name: str | None = None):
        selected_model = model_name or request["model"]
        cache_key = json.dumps(
            {
                "backend": request.get("backend", "whisperx"),
                "m": selected_model,
                "device": request["device"],
                "compute": request["compute"],
                "lang": request["lang"],
                "beam": request["beam"],
                "vad": request["vad"],
                "vad_filter": bool(request.get("vad_filter", True)),
                "chunk": request["chunk"],
                "initial_prompt": request["initial_prompt"],
                "hotwords": request["hotwords"],
                "torch_compile": bool(request.get("use_torch_compile")),
            },
            ensure_ascii=False,
        )
        backend = str(request.get("backend", "whisperx")).lower()
        if backend == "faster-whisper":
            return self.cache.get_or_load_model(
                cache_key,
                lambda: FasterWhisperAdapter(
                    model_name=selected_model,
                    device=request["device"],
                    compute_type=request["compute"],
                    beam_size=int(request["beam"]),
                    language=request["lang"],
                    vad_onset=float(request["vad"]),
                    vad_filter=bool(request.get("vad_filter", True)),
                    initial_prompt=request["initial_prompt"],
                    hotwords=request["hotwords"],
                ),
            )

        model = self.cache.get_or_load_model(
            cache_key,
            lambda: whisperx.load_model(
                selected_model,
                device=request["device"],
                compute_type=request["compute"],
                language=request["lang"],
                asr_options={
                    "beam_size": int(request["beam"]),
                    "initial_prompt": request["initial_prompt"] or None,
                    "hotwords": request["hotwords"] or None,
                },
                vad_options={"vad_onset": float(request["vad"]), "chunk_size": int(request["chunk"])},
            ),
        )
        return self._maybe_torch_compile(model, request)

    def _diarizer(self, request: dict[str, Any]):
        key = f"{request['device']}:{request['hf_token']}"
        return self.cache.get_or_load_diarizer(
            key,
            lambda: WhisperXDiarizationPipeline(use_auth_token=request["hf_token"], device=request["device"]),
        )

    def _preprocess(self, path: str, diar: bool = False, profile: str | None = None) -> str:
        inp = Path(path)
        profile = profile or ("diar" if diar else "asr")
        suffix_map = {
            "asr": ".asr.wav",
            "asr_soft": ".asr_soft.wav",
            "diar": ".diar.wav",
            "diar_soft": ".diar_soft.wav",
        }
        filter_map = {
            "asr": (
                "highpass=f=60,"
                "lowpass=f=7600,"
                "afftdn=nf=-18,"
                "dynaudnorm=f=150:g=15,"
                "acompressor=threshold=-26dB:ratio=2.5:attack=5:release=70,"
                "alimiter=limit=-1dB"
            ),
            "asr_soft": (
                "highpass=f=40,"
                "lowpass=f=7800,"
                "afftdn=nf=-14,"
                "dynaudnorm=f=120:g=21,"
                "acompressor=threshold=-30dB:ratio=2.0:attack=10:release=120,"
                "alimiter=limit=-1dB"
            ),
            "diar": (
                "highpass=f=90,"
                "lowpass=f=7600,"
                "afftdn=nf=-18,"
                "dynaudnorm=f=200:g=17,"
                "acompressor=threshold=-30dB:ratio=3:attack=5:release=90,"
                "alimiter=limit=-1dB"
            ),
            "diar_soft": (
                "highpass=f=65,"
                "lowpass=f=7800,"
                "afftdn=nf=-14,"
                "dynaudnorm=f=180:g=22,"
                "acompressor=threshold=-32dB:ratio=2.2:attack=8:release=120,"
                "alimiter=limit=-1dB"
            ),
        }
        out = inp.parent / f"{inp.stem}{suffix_map.get(profile, '.tmp.wav')}"
        flt = filter_map.get(profile, filter_map["diar" if diar else "asr"])
        ffmpeg_path = require_binary("ffmpeg", extra_roots=[self.project_root])
        cmd = [str(ffmpeg_path), "-y", "-i", str(inp), "-ac", "1", "-ar", "16000", "-af", flt, str(out)]
        try:
            subprocess.run(cmd, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
            self.temp_files.append(out)
            return str(out)
        except Exception:
            return path

    def _get_audio_duration_sec(self, path: str) -> float:
        try:
            ffprobe_path = require_binary("ffprobe", extra_roots=[self.project_root])
            cmd = [
                str(ffprobe_path),
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
                path,
            ]
            out = subprocess.check_output(cmd, stderr=subprocess.STDOUT, timeout=30)
            return float(out.decode("utf-8", errors="ignore").strip())
        except Exception:
            return 0.0

    def _is_video_input(self, path: Any) -> bool:
        value = str(path or "").strip()
        return bool(value) and Path(value).suffix.lower() in VIDEO_EXTENSIONS

    def _iter_video_inputs(self, request: dict[str, Any]):
        for key in ("selected_file", "reference_file", "source_file"):
            value = str(request.get(key) or "").strip()
            if value and self._is_video_input(value):
                yield Path(value)

    def _ensure_video_audio_streams(self, request: dict[str, Any]) -> None:
        for video_path in self._iter_video_inputs(request):
            if not media_has_audio_stream(video_path, extra_roots=[self.project_root]):
                raise RuntimeError(f"В видео нет аудиодорожки: {video_path}")

    def ensure_audio_dependencies(self, request: dict[str, Any]):
        uses_video = any(self._iter_video_inputs(request))
        needs_ffmpeg = any(
            [
                request.get("preprocess"),
                request.get("diarize"),
                request.get("backend") == "whisperx",
                bool(request.get("reference_file")),
                uses_video,
            ]
        )
        if not needs_ffmpeg and not request.get("auto_model"):
            return

        ffmpeg_path, ffprobe_path = require_ffmpeg_tools(
            require_ffprobe=bool(request.get("auto_model")) or uses_video,
            extra_roots=[self.project_root],
        )
        self.log(f"FFmpeg: {ffmpeg_path}")
        if ffprobe_path is not None:
            self.log(f"FFprobe: {ffprobe_path}")
        if uses_video:
            self._ensure_video_audio_streams(request)

    def pick_model_by_audio_complexity(self, path: str, device: str) -> str:
        duration_sec = self._get_audio_duration_sec(path)
        duration_min = duration_sec / 60.0 if duration_sec > 0 else 0.0
        if duration_min <= 12:
            picked = "small"
        elif duration_min <= 45:
            picked = "medium"
        else:
            picked = "large-v3"
        if device == "cpu" and picked == "large-v3":
            picked = "medium"
        self.log(f"Auto model selected: {picked} (duration: {duration_min:.1f} min, device: {device})")
        return picked

    def _timed_text_segments(self, result: dict[str, Any]) -> list[dict[str, Any]]:
        segments = []
        for seg in result.get("segments", []) or []:
            text = str(seg.get("text", "") or "").strip()
            if not text:
                continue
            start = float(seg.get("start", 0) or 0)
            end = float(seg.get("end", start) or start)
            if end < start:
                end = start
            segments.append({"start": start, "end": end, "text": text})
        return sorted(segments, key=lambda item: (item["start"], item["end"]))

    def _transcript_health(self, result: dict[str, Any], audio_duration: float) -> dict[str, float]:
        segments = self._timed_text_segments(result)
        if not segments:
            return {
                "audio_duration": float(audio_duration or 0.0),
                "segment_count": 0.0,
                "text_chars": 0.0,
                "leading_gap": float(audio_duration or 0.0),
                "largest_gap": float(audio_duration or 0.0),
                "coverage_end": 0.0,
                "coverage_ratio": 0.0,
                "speech_seconds": 0.0,
            }

        leading_gap = max(0.0, segments[0]["start"])
        largest_gap = leading_gap
        speech_seconds = 0.0
        previous_end = segments[0]["end"]
        for seg in segments:
            speech_seconds += max(0.0, seg["end"] - seg["start"])
        for seg in segments[1:]:
            gap = max(0.0, seg["start"] - previous_end)
            largest_gap = max(largest_gap, gap)
            previous_end = max(previous_end, seg["end"])

        coverage_end = previous_end
        if audio_duration > 0:
            largest_gap = max(largest_gap, max(0.0, audio_duration - coverage_end))
            coverage_ratio = min(1.0, coverage_end / audio_duration)
        else:
            coverage_ratio = 1.0

        return {
            "audio_duration": float(audio_duration or 0.0),
            "segment_count": float(len(segments)),
            "text_chars": float(sum(len(seg["text"]) for seg in segments)),
            "leading_gap": float(leading_gap),
            "largest_gap": float(largest_gap),
            "coverage_end": float(coverage_end),
            "coverage_ratio": float(coverage_ratio),
            "speech_seconds": float(speech_seconds),
        }

    def _should_retry_transcription(self, stats: dict[str, float]) -> bool:
        audio_duration = stats.get("audio_duration", 0.0)
        if audio_duration <= 0:
            return stats.get("segment_count", 0.0) <= 0
        if stats.get("segment_count", 0.0) <= 0:
            return True

        leading_gap = stats.get("leading_gap", 0.0)
        largest_gap = stats.get("largest_gap", 0.0)
        coverage_ratio = stats.get("coverage_ratio", 0.0)
        if leading_gap >= max(90.0, audio_duration * 0.05):
            return True
        if largest_gap >= max(180.0, audio_duration * 0.15):
            return True
        if audio_duration >= 600.0 and coverage_ratio < 0.92:
            return True
        return False

    def _is_retry_result_better(self, current: dict[str, float], candidate: dict[str, float]) -> bool:
        if candidate.get("segment_count", 0.0) <= 0:
            return False
        if candidate.get("text_chars", 0.0) > current.get("text_chars", 0.0) * 1.1:
            return True
        if candidate.get("leading_gap", float("inf")) + 30.0 < current.get("leading_gap", float("inf")):
            return True
        if candidate.get("largest_gap", float("inf")) + 45.0 < current.get("largest_gap", float("inf")):
            return True
        if candidate.get("coverage_end", 0.0) > current.get("coverage_end", 0.0) + 45.0:
            return True
        return False

    def _collect_diar_segments(self, diar_df: Any) -> list[dict[str, Any]]:
        diar_segments: list[dict[str, Any]] = []
        if diar_df is None:
            return diar_segments
        for row in diar_df.itertuples(index=False):
            diar_segments.append(
                {
                    "start": float(row.start),
                    "end": float(row.end),
                    "speaker": str(row.speaker),
                }
            )
        return sorted(diar_segments, key=lambda item: (item["start"], item["end"]))

    def _assign_speakers_by_overlap(self, segments: list[dict[str, Any]], diar_segments: list[dict[str, Any]]) -> list[dict[str, Any]]:
        if not segments or not diar_segments:
            return segments

        out: list[dict[str, Any]] = []
        for seg in segments:
            start = float(seg.get("start", 0) or 0)
            end = float(seg.get("end", start) or start)
            best_overlap = 0.0
            best_speaker = str(seg.get("speaker", "UNKNOWN") or "UNKNOWN")
            for item in diar_segments:
                overlap = min(end, item["end"]) - max(start, item["start"])
                if overlap > best_overlap:
                    best_overlap = overlap
                    best_speaker = item.get("speaker", "UNKNOWN")
            if best_overlap <= 0:
                mid = (start + end) / 2.0
                for item in diar_segments:
                    if item["start"] <= mid <= item["end"]:
                        best_speaker = item.get("speaker", "UNKNOWN")
                        break
            new_seg = dict(seg)
            new_seg["speaker"] = best_speaker
            out.append(new_seg)
        return out

    def _run_asr_pass(self, request: dict[str, Any], selected_model: str) -> dict[str, Any]:
        model = self._model(request, selected_model)
        src = str(request.get("source_file") or request["selected_file"])
        if request["preprocess"]:
            src = self._preprocess(src, diar=False, profile=str(request.get("asr_profile") or "asr"))
        audio = whisperx.load_audio(src)
        try:
            res = model.transcribe(audio, batch_size=int(request["batch"]), word_timestamps=True)
        except TypeError:
            res = model.transcribe(audio, batch_size=int(request["batch"]))

        if res.get("language"):
            try:
                am, md = whisperx.load_align_model(language_code=res["language"], device=request["device"])
                al = whisperx.align(res.get("segments", []), am, md, audio, request["device"], return_char_alignments=False)
                if isinstance(al, dict):
                    res["segments"] = al.get("segments", res.get("segments", []))
                    if "word_segments" in al:
                        res["word_segments"] = al["word_segments"]
            except Exception as exc:
                self.log(f"Alignment skipped: {exc}")
        return res

    def _is_cuda_oom(self, exc: Exception) -> bool:
        message = str(exc).lower()
        return "out of memory" in message or "cuda failed with error out of memory" in message

    def _try_run_asr_pass(
        self,
        request: dict[str, Any],
        selected_model: str,
        *,
        stage_name: str,
        clear_models: bool = False,
    ) -> dict[str, Any] | None:
        try:
            if clear_models:
                self.release_resources(clear_models=True)
            return self._run_asr_pass(request, selected_model)
        except Exception as exc:
            if self._is_cuda_oom(exc):
                self.log(f"{stage_name} skipped because GPU memory was exhausted")
                self.release_resources(clear_models=True)
                return None
            raise

    def _create_audio_excerpt(self, path: str, *, start_sec: float, duration_sec: float, suffix: str) -> str:
        src = Path(path)
        clip_path = src.parent / f"{src.stem}{suffix}.wav"
        ffmpeg_path = require_binary("ffmpeg", extra_roots=[self.project_root])
        cmd = [
            str(ffmpeg_path),
            "-y",
            "-ss",
            f"{max(0.0, start_sec):.3f}",
            "-t",
            f"{max(1.0, duration_sec):.3f}",
            "-i",
            str(src),
            "-ac",
            "1",
            "-ar",
            "16000",
            str(clip_path),
        ]
        subprocess.run(cmd, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
        self.temp_files.append(clip_path)
        return str(clip_path)

    def _concatenate_audio_files(self, paths: list[str], *, output_name: str) -> str:
        valid_paths = [Path(path) for path in paths if path]
        if len(valid_paths) < 2:
            return str(valid_paths[0]) if valid_paths else ""

        output_path = valid_paths[-1].parent / f"{valid_paths[-1].stem}{output_name}.wav"
        ffmpeg_path = require_binary("ffmpeg", extra_roots=[self.project_root])
        cmd = [str(ffmpeg_path), "-y"]
        filter_parts = []
        concat_inputs = []
        for idx, path in enumerate(valid_paths):
            cmd.extend(["-i", str(path)])
            filter_parts.append(
                f"[{idx}:a]aresample=16000,aformat=sample_fmts=s16:channel_layouts=mono,asetpts=N/SR/TB[a{idx}]"
            )
            concat_inputs.append(f"[a{idx}]")
        filter_parts.append(f"{''.join(concat_inputs)}concat=n={len(valid_paths)}:v=0:a=1[outa]")
        cmd.extend(
            [
                "-filter_complex",
                ";".join(filter_parts),
                "-map",
                "[outa]",
                "-ac",
                "1",
                "-ar",
                "16000",
                str(output_path),
            ]
        )
        subprocess.run(cmd, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=600)
        self.temp_files.append(output_path)
        return str(output_path)

    def _merge_asr_results(self, prefix_result: dict[str, Any], main_result: dict[str, Any]) -> dict[str, Any]:
        merged = dict(main_result)
        main_segments = list(main_result.get("segments", []) or [])
        prefix_segments = list(prefix_result.get("segments", []) or [])
        if not prefix_segments:
            return merged

        first_main_start = min(
            (float(seg.get("start", 0) or 0) for seg in main_segments if str(seg.get("text", "")).strip()),
            default=float("inf"),
        )
        cutoff = first_main_start - 2.0
        kept_prefix_segments = [
            dict(seg)
            for seg in prefix_segments
            if str(seg.get("text", "")).strip() and float(seg.get("end", seg.get("start", 0)) or 0) <= cutoff
        ]
        if not kept_prefix_segments:
            return merged

        merged["segments"] = kept_prefix_segments + main_segments

        main_words = list(main_result.get("word_segments", []) or [])
        prefix_words = list(prefix_result.get("word_segments", []) or [])
        if prefix_words:
            kept_prefix_words = [
                dict(word)
                for word in prefix_words
                if float(word.get("end", word.get("start", 0)) or 0) <= cutoff
            ]
            if kept_prefix_words or main_words:
                merged["word_segments"] = kept_prefix_words + main_words
        elif "word_segments" in merged:
            merged["word_segments"] = main_words

        merged["text"] = " ".join(
            str(seg.get("text", "")).strip() for seg in merged["segments"] if str(seg.get("text", "")).strip()
        ).strip()
        return merged

    def _run_diarization_pass(
        self,
        request: dict[str, Any],
        *,
        profile: str = "diar",
    ) -> tuple[Any, dict[str, Any], list[dict[str, Any]]]:
        started = time.perf_counter()
        self.log(f"Diarization pass started (profile={profile})")
        diar_audio = self._preprocess(request["selected_file"], diar=True, profile=profile)
        self.log(f"Diarization audio prepared (profile={profile}, file={Path(diar_audio).name})")
        diar_df, emb = self._diarizer(request)(
            diar_audio,
            min_speakers=request["mn"],
            max_speakers=request["mx"],
            return_embeddings=True,
        )
        diar_segments = self._collect_diar_segments(diar_df)
        self.log(
            f"Diarization pass finished (profile={profile}, segments={len(diar_segments)}, "
            f"elapsed={time.perf_counter() - started:.1f}s)"
        )
        return diar_df, emb, diar_segments

    def _build_diarization_candidate(
        self,
        base_result: dict[str, Any],
        request: dict[str, Any],
        *,
        profile_name: str,
        is_primary: bool,
    ) -> dict[str, Any]:
        diar_df, emb, diar_segments = self._run_diarization_pass(request, profile=profile_name)
        diar_df, emb, merged_map = self._merge_similar_speaker_labels(diar_df, emb, similarity_threshold=0.82)
        if merged_map:
            self.log(
                f"Merged similar speakers ({profile_name}): "
                + ", ".join(f"{old} -> {new}" for old, new in sorted(merged_map.items()))
            )

        candidate_result = copy.deepcopy(base_result)
        diar_speaker_count = len({item["speaker"] for item in diar_segments if item.get("speaker")})
        try:
            assign_started = time.perf_counter()
            self.log(f"assign_word_speakers started (profile={profile_name})")
            candidate_result = whisperx.assign_word_speakers(diar_df, candidate_result, emb)
            assigned_speakers = {
                str(seg.get("speaker", "UNKNOWN"))
                for seg in candidate_result.get("segments", [])
                if str(seg.get("text", "")).strip()
            }
            self.log(
                f"assign_word_speakers finished "
                f"(profile={profile_name}, speakers={len(assigned_speakers)}, elapsed={time.perf_counter() - assign_started:.1f}s)"
            )
            if len(assigned_speakers) <= 1 and diar_speaker_count > 1:
                self.log(f"assign_word_speakers collapsed speakers; applying overlap fallback (profile={profile_name})")
                candidate_result["segments"] = self._assign_speakers_by_overlap(
                    candidate_result.get("segments", []),
                    diar_segments,
                )
        except Exception as exc:
            if self._is_cuda_oom(exc):
                self.log(f"assign_word_speakers skipped because GPU memory was exhausted; using overlap fallback (profile={profile_name})")
                self.release_resources(clear_models=True)
            else:
                self.log(f"assign_word_speakers failed (profile={profile_name}): {exc}")
            candidate_result["segments"] = self._assign_speakers_by_overlap(
                candidate_result.get("segments", []),
                diar_segments,
            )

        candidate_result["segments"] = smooth_speaker_turns(candidate_result.get("segments", []))
        score = score_diarization_result(
            candidate_result.get("segments", []),
            diar_segments,
            profile_name,
            int(request["mn"]),
            int(request["mx"]),
        )
        self.log(
            f"Diarization candidate scored: profile={profile_name}, score={score.score:.1f}, "
            f"speakers={score.assigned_speaker_count}, reasons={', '.join(score.reasons) or 'ok'}"
        )
        return {
            "profile": profile_name,
            "is_primary": is_primary,
            "result": candidate_result,
            "diar_df": diar_df,
            "embeddings": emb,
            "diar_segments": diar_segments,
            "score": score,
        }

    def _cosine_similarity(self, left, right) -> float:
        import numpy as np

        a = np.asarray(left, dtype=np.float32)
        b = np.asarray(right, dtype=np.float32)
        denom = float(np.linalg.norm(a) * np.linalg.norm(b))
        if denom == 0.0:
            return -1.0
        return float(np.dot(a, b) / denom)

    def _apply_glossary(self, res: dict[str, Any], glossary_text: str):
        rules = parse_glossary_rules(glossary_text)
        for seg in res.get("segments", []):
            txt = str(seg.get("text", ""))
            seg["text"], _ = apply_glossary_rules(txt, rules)
        for word in res.get("word_segments", []):
            key = "word" if "word" in word else "text" if "text" in word else None
            if key and word.get(key):
                word[key], _ = apply_glossary_rules(str(word[key]), rules)

    def _repeat_token_key(self, token: str) -> str:
        normalized = unicodedata.normalize("NFKC", str(token or ""))
        return re.sub(r"(^[^\wА-Яа-яЁёA-Za-z0-9]+|[^\wА-Яа-яЁёA-Za-z0-9]+$)", "", normalized).casefold()

    def _normalize_display_text(self, text: str) -> str:
        normalized = unicodedata.normalize("NFKC", str(text or ""))
        normalized = re.sub(r"\s{2,}", " ", normalized)
        return normalized.strip()

    def _comparison_key(self, text: str) -> str:
        normalized = self._normalize_display_text(text)
        normalized = re.sub(r"[^\wА-Яа-яЁёA-Za-z0-9]+", " ", normalized)
        normalized = re.sub(r"\s{2,}", " ", normalized)
        return normalized.casefold().strip()

    def _repair_split_words(self, text: str) -> str:
        repaired = self._normalize_display_text(text)
        repairs = [
            (r"\bад\s+министратив(н\w*)\b", r"административ\1"),
            (r"\bэксплуацион\s+ной\b", "эксплуатационной"),
            (r"\bэксплуаций\s+ной\b", "эксплуатационной"),
            (r"\bэнерго\s+механическ(ого|ий|ом|ий)\b", r"энергомеханическ\1"),
        ]
        for pattern, replacement in repairs:
            repaired = re.sub(pattern, replacement, repaired, flags=re.IGNORECASE)
        repaired = re.sub(r"\bHR\b", "HR", repaired, flags=re.IGNORECASE)
        return repaired

    def _squash_elongated_letters(self, text: str) -> str:
        squashed = self._normalize_display_text(text)
        squashed = re.sub(r"([A-Za-zА-Яа-яЁё])\1{2,}", r"\1\1", squashed)
        squashed = re.sub(r"\s{2,}", " ", squashed)
        return squashed.strip()

    def _drop_low_information_noise(self, text: str) -> str:
        normalized = self._normalize_display_text(text)
        if not normalized:
            return ""

        token_keys = [self._repeat_token_key(token) for token in re.split(r"\s+", normalized) if self._repeat_token_key(token)]
        if not token_keys:
            return normalized

        if len(token_keys) == 1:
            key = token_keys[0]
            letters = re.findall(r"[A-Za-zА-Яа-яЁё]", key)
            if letters and len(key) >= 6 and len(set(ch.casefold() for ch in letters)) <= 2 and not re.search(r"[А-Яа-яЁё]{3,}", key):
                return ""

        if len(token_keys) <= 2 and max(token_keys.count(key) for key in set(token_keys)) >= max(3, len(token_keys)):
            return token_keys[0] if len(token_keys[0]) >= 3 else ""

        return normalized

    def _is_probable_hallucination_segment(self, seg: dict[str, Any], expected_language: str | None) -> bool:
        text = self._normalize_display_text(str(seg.get("text", "")))
        if not text:
            return True

        duration = max(0.0, float(seg.get("end", seg.get("start", 0.0)) or 0.0) - float(seg.get("start", 0.0) or 0.0))
        token_keys = [
            self._repeat_token_key(token)
            for token in re.split(r"\s+", text)
            if self._repeat_token_key(token)
        ]
        if not token_keys:
            return True

        latin_count = len(re.findall(r"[A-Za-z]", text))
        cyrillic_count = len(re.findall(r"[А-Яа-яЁё]", text))

        if (expected_language or "").startswith("ru") and latin_count >= 6 and cyrillic_count == 0 and len(token_keys) <= 6:
            return True

        if duration >= 10.0 and len(token_keys) <= 2:
            return True

        if duration >= 16.0 and len(set(token_keys)) <= 2:
            return True

        if max(token_keys.count(key) for key in set(token_keys)) >= max(4, len(token_keys) - 1):
            return True

        return False

    def _dedupe_adjacent_segments(self, segments: list[dict[str, Any]]) -> list[dict[str, Any]]:
        if not segments:
            return []

        deduped: list[dict[str, Any]] = []
        for seg in segments:
            if not deduped:
                deduped.append(dict(seg))
                continue

            prev = deduped[-1]
            prev_key = self._comparison_key(str(prev.get("text", "")))
            curr_key = self._comparison_key(str(seg.get("text", "")))
            prev_end = float(prev.get("end", prev.get("start", 0.0)) or 0.0)
            curr_start = float(seg.get("start", 0.0) or 0.0)
            gap = curr_start - prev_end

            is_duplicate = (
                prev_key
                and curr_key
                and gap <= 3.0
                and (
                    prev_key == curr_key
                    or (len(curr_key) >= 12 and curr_key in prev_key)
                    or (len(prev_key) >= 12 and prev_key in curr_key)
                )
            )
            if is_duplicate:
                prev_duration = max(0.0, float(prev.get("end", prev.get("start", 0.0)) or 0.0) - float(prev.get("start", 0.0) or 0.0))
                curr_duration = max(0.0, float(seg.get("end", seg.get("start", 0.0)) or 0.0) - float(seg.get("start", 0.0) or 0.0))
                if curr_duration > prev_duration:
                    deduped[-1] = dict(seg)
                continue

            deduped.append(dict(seg))

        return deduped

    def _drop_repeated_word_runs(self, text: str) -> str:
        tokens = [token for token in re.split(r"\s+", str(text or "").strip()) if token]
        if len(tokens) < 4:
            return str(text or "").strip()

        cleaned: list[str] = []
        i = 0
        while i < len(tokens):
            key = self._repeat_token_key(tokens[i])
            if not key:
                cleaned.append(tokens[i])
                i += 1
                continue

            j = i + 1
            while j < len(tokens) and self._repeat_token_key(tokens[j]) == key:
                j += 1
            run_len = j - i

            if run_len >= 3:
                next_token = tokens[j] if j < len(tokens) else ""
                next_key = self._repeat_token_key(next_token)
                prev_key = self._repeat_token_key(cleaned[-1]) if cleaned else ""
                if next_key and next_key != key and prev_key != key:
                    i = j
                    continue
                cleaned.append(tokens[i])
                i = j
                continue

            cleaned.extend(tokens[i:j])
            i = j

        normalized = " ".join(cleaned)
        normalized = re.sub(r"\s+([,.:;!?])", r"\1", normalized)
        normalized = re.sub(r"\s{2,}", " ", normalized)
        return normalized.strip()

    def _collapse_repeated_phrases(self, text: str) -> str:
        tokens = [token for token in re.split(r"\s+", str(text or "").strip()) if token]
        if len(tokens) < 4:
            return str(text or "").strip()

        output: list[str] = []
        i = 0
        while i < len(tokens):
            best: tuple[int, int, list[str]] | None = None
            max_span = min(12, (len(tokens) - i) // 2)
            for span in range(max_span, 0, -1):
                phrase = tokens[i : i + span]
                phrase_keys = [self._repeat_token_key(token) for token in phrase]
                phrase_text = " ".join(phrase).strip()
                if span == 1 and len(phrase_text) < 4:
                    continue
                if span > 1 and len(phrase_text) < 14:
                    continue
                reps = 1
                while i + (reps + 1) * span <= len(tokens):
                    candidate = tokens[i + reps * span : i + (reps + 1) * span]
                    candidate_keys = [self._repeat_token_key(token) for token in candidate]
                    if candidate_keys != phrase_keys:
                        break
                    reps += 1
                if reps >= (4 if span == 1 else 2):
                    best = (span, reps, phrase)
                    break
            if best is None:
                output.append(tokens[i])
                i += 1
                continue
            output.extend(best[2])
            i += best[0] * best[1]

        cleaned = " ".join(output)
        cleaned = re.sub(r"\s+([,.:;!?])", r"\1", cleaned)
        cleaned = re.sub(r"\s{2,}", " ", cleaned)
        return cleaned.strip()

    def _cleanup_result_text(self, res: dict[str, Any]) -> dict[str, Any]:
        expected_language = str(res.get("language") or "").strip().lower()
        segments = []
        for seg in res.get("segments", []):
            cleaned = self._normalize_display_text(str(seg.get("text", "")))
            cleaned = self._repair_split_words(cleaned)
            cleaned = self._collapse_repeated_phrases(cleaned)
            cleaned = self._drop_repeated_word_runs(cleaned)
            cleaned = self._squash_elongated_letters(cleaned)
            cleaned = self._repair_split_words(cleaned)
            cleaned = self._drop_low_information_noise(cleaned)
            if not cleaned:
                continue
            new_seg = dict(seg)
            new_seg["text"] = cleaned
            if self._is_probable_hallucination_segment(new_seg, expected_language):
                continue
            segments.append(new_seg)
        segments = self._dedupe_adjacent_segments(segments)
        res["segments"] = segments
        res["text"] = " ".join(seg.get("text", "").strip() for seg in segments if seg.get("text"))
        res["text"] = re.sub(r"\s{2,}", " ", res["text"]).strip()
        return res

    def _trim_result_prefix(self, result: dict[str, Any], prefix_sec: float) -> dict[str, Any]:
        if prefix_sec <= 0:
            return dict(result)

        trimmed = dict(result)
        new_segments: list[dict[str, Any]] = []
        for seg in result.get("segments", []) or []:
            start = float(seg.get("start", 0.0) or 0.0)
            end = float(seg.get("end", start) or start)
            if end <= prefix_sec:
                continue
            new_seg = dict(seg)
            new_seg["start"] = max(0.0, start - prefix_sec)
            new_seg["end"] = max(new_seg["start"], end - prefix_sec)
            seg_words = []
            for word in seg.get("words", []) or []:
                w_start = float(word.get("start", 0.0) or 0.0)
                w_end = float(word.get("end", w_start) or w_start)
                if w_end <= prefix_sec:
                    continue
                new_word = dict(word)
                new_word["start"] = max(0.0, w_start - prefix_sec)
                new_word["end"] = max(new_word["start"], w_end - prefix_sec)
                seg_words.append(new_word)
            if seg_words:
                new_seg["words"] = seg_words
            new_segments.append(new_seg)
        trimmed["segments"] = new_segments

        if result.get("word_segments"):
            new_words = []
            for word in result.get("word_segments", []) or []:
                w_start = float(word.get("start", 0.0) or 0.0)
                w_end = float(word.get("end", w_start) or w_start)
                if w_end <= prefix_sec:
                    continue
                new_word = dict(word)
                new_word["start"] = max(0.0, w_start - prefix_sec)
                new_word["end"] = max(new_word["start"], w_end - prefix_sec)
                new_words.append(new_word)
            trimmed["word_segments"] = new_words

        trimmed["text"] = " ".join(
            str(seg.get("text", "")).strip() for seg in new_segments if str(seg.get("text", "")).strip()
        ).strip()
        return trimmed

    def _merge_similar_speaker_labels(self, diar_df: Any, embeddings: dict[str, Any], similarity_threshold: float = 0.82):
        if not embeddings or len(embeddings) < 2:
            return diar_df, embeddings or {}, {}
        speakers = list(embeddings.keys())
        parent: dict[str, str] = {sp: sp for sp in speakers}

        def find(x: str) -> str:
            while parent[x] != x:
                parent[x] = parent[parent[x]]
                x = parent[x]
            return x

        def union(a: str, b: str):
            ra, rb = find(a), find(b)
            if ra != rb:
                parent[rb] = ra

        for i, left in enumerate(speakers):
            for right in speakers[i + 1 :]:
                if self._cosine_similarity(embeddings[left], embeddings[right]) >= similarity_threshold:
                    union(left, right)

        remap: dict[str, str] = {}
        for sp in speakers:
            remap[sp] = find(sp)
        changed = {k: v for k, v in remap.items() if k != v}
        if not changed:
            return diar_df, embeddings, {}
        try:
            diar_df = diar_df.copy()
            diar_df["speaker"] = diar_df["speaker"].map(lambda s: remap.get(str(s), str(s)))
        except Exception:
            pass
        merged_embeddings: dict[str, Any] = {}
        for sp, emb in embeddings.items():
            merged_embeddings[remap.get(sp, sp)] = emb
        return diar_df, merged_embeddings, changed

    def _match_speakers(self, main_embeddings: dict[str, Any], reference_profiles: dict[str, dict[str, Any]], threshold: float):
        if not main_embeddings or not reference_profiles:
            return {}
        candidates: list[tuple[float, str, str]] = []
        for main_sp, main_emb in main_embeddings.items():
            for ref_sp, prof in reference_profiles.items():
                score = self._cosine_similarity(main_emb, prof["embedding"])
                candidates.append((score, main_sp, ref_sp))
        mapping: dict[str, dict[str, Any]] = {}
        used_ref = set()
        for score, main_sp, ref_sp in sorted(candidates, key=lambda x: x[0], reverse=True):
            if score < threshold or main_sp in mapping or ref_sp in used_ref:
                continue
            mapping[main_sp] = {"name": reference_profiles[ref_sp]["name"], "score": score, "reference_speaker": ref_sp}
            used_ref.add(ref_sp)
        return mapping

    def _build_reference_profiles(self, cancel: threading.Event, model, request: dict[str, Any]):
        reference_file = request.get("reference_file")
        if not reference_file or not Path(reference_file).exists() or cancel.is_set():
            return {}
        src = reference_file
        if request["preprocess"]:
            src = self._preprocess(src, diar=False)
        audio = whisperx.load_audio(src)
        try:
            ref_res = model.transcribe(audio, batch_size=int(request["batch"]), word_timestamps=True)
        except TypeError:
            ref_res = model.transcribe(audio, batch_size=int(request["batch"]))
        diar_audio = self._preprocess(reference_file, diar=True)
        diar_df, emb = self._diarizer(request)(diar_audio, min_speakers=request["mn"], max_speakers=request["mx"], return_embeddings=True)
        try:
            ref_res = whisperx.assign_word_speakers(diar_df, ref_res, emb)
        except Exception:
            pass
        ordered = []
        for seg in ref_res.get("segments", []):
            sp = str(seg.get("speaker", "UNKNOWN"))
            if sp not in ordered:
                ordered.append(sp)
        names = request.get("reference_names", [])
        profiles: dict[str, dict[str, Any]] = {}
        for idx, sp in enumerate(ordered, start=1):
            if sp in emb:
                profiles[sp] = {"name": names[idx - 1] if idx - 1 < len(names) else f"Спикер {idx}", "embedding": emb[sp]}
        return profiles

    def install_model(self, request: dict[str, Any]):
        try:
            self.log("Loading model...")
            self._model(request)
            self.log("Model loaded")
        finally:
            self.release_resources(clear_models=False)

    def transcribe(self, cancel: threading.Event, request: dict[str, Any]) -> dict[str, Any]:
        try:
            request = dict(request)
            profile = get_processing_profile(request.get("processing_profile"))
            self._set_stage(RunStage.FFMPEG, 0.05)
            request["hotwords"] = load_hotwords_text(self.project_root, str(request.get("hotwords") or ""))
            self.ensure_audio_dependencies(request)
            self.log(
                f"ASR runtime: backend={request['backend']}, device={request['device']}, compute_type={request['compute']}"
            )
            if request.get("diarize"):
                self.log(f"Diarization runtime: device={request['device']}")
            main_audio_file = str(request.get("selected_file") or "")
            audio_duration = self._get_audio_duration_sec(main_audio_file)
            reference_prefix_sec = 0.0
            if request.get("concat_reference") and request.get("reference_file"):
                reference_prefix_sec = self._get_audio_duration_sec(str(request["reference_file"]))
                request["reference_prefix_sec"] = reference_prefix_sec
                request["selected_file"] = self._concatenate_audio_files(
                    [str(request["reference_file"]), str(request["selected_file"])],
                    output_name=".reference_plus_main",
                )
                self.log("Merged introduction audio with the main recording for a joint transcription pass")
                if reference_prefix_sec > 0:
                    self.log(f"Reference intro will be trimmed from final output: {reference_prefix_sec / 60.0:.1f} min")
            selected_model = request["model"]
            if request["auto_model"] and request["selected_file"]:
                selected_model = self.pick_model_by_audio_complexity(request["selected_file"], request["device"])
                selected_model = bounded_auto_model(selected_model, profile)
            if audio_duration > 0:
                self.log(f"Audio duration: {audio_duration / 60.0:.1f} min")
            if request["hotwords"]:
                self.log(f"Hotwords loaded: {len([line for line in request['hotwords'].splitlines() if line.strip()])} item(s)")
            self._set_stage(RunStage.ASR, 0.2)
            if cancel.is_set():
                return {}

            res = self._run_asr_pass(request, selected_model)
            self._set_stage(RunStage.ALIGNMENT, 0.45)
            if cancel.is_set():
                return {}

            primary_view = self._trim_result_prefix(res, reference_prefix_sec)
            primary_stats = self._transcript_health(primary_view, audio_duration)
            self.log(
                "Primary ASR coverage: "
                f"segments={int(primary_stats['segment_count'])}, "
                f"start_gap={primary_stats['leading_gap'] / 60.0:.1f} min, "
                f"last_end={primary_stats['coverage_end'] / 60.0:.1f} min, "
                f"largest_gap={primary_stats['largest_gap'] / 60.0:.1f} min"
            )

            if self._should_retry_transcription(primary_stats) and not cancel.is_set():
                safe_request = dict(request)
                safe_request["preprocess"] = True
                safe_request["asr_profile"] = "asr_soft"
                safe_request["vad"] = min(float(request["vad"]), 0.22)
                safe_request["chunk"] = max(int(request["chunk"]), 30)
                self.log(
                    "Large timing gap detected; retrying ASR with safer settings "
                    f"(profile={safe_request['asr_profile']}, vad={safe_request['vad']}, chunk={safe_request['chunk']})"
                )
                retry_res = self._try_run_asr_pass(
                    safe_request,
                    selected_model,
                    stage_name="Fallback ASR",
                    clear_models=True,
                )
                if retry_res is not None:
                    retry_view = self._trim_result_prefix(retry_res, reference_prefix_sec)
                    retry_stats = self._transcript_health(retry_view, audio_duration)
                    self.log(
                        "Fallback ASR coverage: "
                        f"segments={int(retry_stats['segment_count'])}, "
                        f"start_gap={retry_stats['leading_gap'] / 60.0:.1f} min, "
                        f"last_end={retry_stats['coverage_end'] / 60.0:.1f} min, "
                        f"largest_gap={retry_stats['largest_gap'] / 60.0:.1f} min"
                    )
                    if self._is_retry_result_better(primary_stats, retry_stats):
                        self.log("Using fallback ASR result because it covers the recording better")
                        res = retry_res
                        primary_stats = retry_stats
                    else:
                        self.log("Keeping primary ASR result; fallback did not improve coverage")
                else:
                    self.log("Keeping primary ASR result because fallback could not be completed")

            if primary_stats.get("leading_gap", 0.0) >= 90.0 and not cancel.is_set():
                no_vad_request = dict(request)
                no_vad_request["backend"] = "faster-whisper"
                no_vad_request["preprocess"] = True
                no_vad_request["asr_profile"] = "asr_soft"
                no_vad_request["vad_filter"] = False
                no_vad_request["vad"] = min(float(request["vad"]), 0.18)
                self.log("Large initial gap detected; retrying ASR with faster-whisper and VAD disabled")
                no_vad_res = self._try_run_asr_pass(
                    no_vad_request,
                    selected_model,
                    stage_name="No-VAD ASR",
                    clear_models=True,
                )
                if no_vad_res is not None:
                    no_vad_view = self._trim_result_prefix(no_vad_res, reference_prefix_sec)
                    no_vad_stats = self._transcript_health(no_vad_view, audio_duration)
                    self.log(
                        "No-VAD ASR coverage: "
                        f"segments={int(no_vad_stats['segment_count'])}, "
                        f"start_gap={no_vad_stats['leading_gap'] / 60.0:.1f} min, "
                        f"last_end={no_vad_stats['coverage_end'] / 60.0:.1f} min, "
                        f"largest_gap={no_vad_stats['largest_gap'] / 60.0:.1f} min"
                    )
                    if self._is_retry_result_better(primary_stats, no_vad_stats):
                        self.log("Using no-VAD ASR result because it recovered missing beginning of the recording")
                        res = no_vad_res
                        primary_stats = no_vad_stats
                    else:
                        self.log("Keeping previous ASR result; no-VAD pass did not improve the start of the recording")
                else:
                    self.log("Keeping previous ASR result because the no-VAD pass could not be completed")

            if primary_stats.get("leading_gap", 0.0) >= 45.0 and not cancel.is_set():
                rescue_request = dict(request)
                rescue_request["backend"] = "faster-whisper"
                rescue_request["preprocess"] = True
                rescue_request["asr_profile"] = "asr_soft"
                rescue_request["vad_filter"] = False
                rescue_request["source_file"] = self._create_audio_excerpt(
                    request["selected_file"],
                    start_sec=0.0,
                    duration_sec=min(max(primary_stats["leading_gap"] + 90.0, 180.0), 8 * 60.0),
                    suffix=".head_rescue",
                )
                self.log("Attempting dedicated head-rescue pass for the beginning of the recording")
                rescue_res = self._try_run_asr_pass(
                    rescue_request,
                    selected_model,
                    stage_name="Head-rescue ASR",
                    clear_models=True,
                )
                if rescue_res is not None:
                    merged_res = self._merge_asr_results(rescue_res, res)
                    merged_view = self._trim_result_prefix(merged_res, reference_prefix_sec)
                    merged_stats = self._transcript_health(merged_view, audio_duration)
                    self.log(
                        "Head-rescue ASR coverage: "
                        f"segments={int(merged_stats['segment_count'])}, "
                        f"start_gap={merged_stats['leading_gap'] / 60.0:.1f} min, "
                        f"last_end={merged_stats['coverage_end'] / 60.0:.1f} min, "
                        f"largest_gap={merged_stats['largest_gap'] / 60.0:.1f} min"
                    )
                    if self._is_retry_result_better(primary_stats, merged_stats):
                        self.log("Using head-rescue merge because it recovered more of the beginning")
                        res = merged_res
                        primary_stats = merged_stats
                    else:
                        self.log("Keeping previous ASR result; head-rescue merge did not improve coverage")
                else:
                    self.log("Keeping previous ASR result because the head-rescue pass could not be completed")

            speaker_mapping: dict[str, dict[str, Any]] = {}
            emb: dict[str, Any] = {}
            if request["diarize"]:
                self._set_stage(RunStage.DIARIZATION, 0.62)
                self.release_resources(clear_models=True)
                self.log("Released ASR model cache before diarization to reduce GPU memory usage")
                diar_profile = str(request.get("diar_profile") or profile.diarization_profile)
                diarization_candidates: list[dict[str, Any]] = []
                failed_candidates: list[dict[str, Any]] = []
                for candidate_profile in diarization_profiles_for_processing_profile(profile.key, diar_profile):
                    try:
                        diarization_candidates.append(
                            self._build_diarization_candidate(
                                res,
                                request,
                                profile_name=candidate_profile,
                                is_primary=candidate_profile == diar_profile,
                            )
                        )
                    except Exception as exc:
                        failed_candidates.append({"profile": candidate_profile, "error": str(exc)})
                        self.log(f"Diarization candidate failed: profile={candidate_profile}, error={exc}")
                        if self._is_cuda_oom(exc):
                            self.release_resources(clear_models=True)

                if not diarization_candidates:
                    errors = "; ".join(f"{item['profile']}: {item['error']}" for item in failed_candidates)
                    raise RuntimeError(f"All diarization candidates failed: {errors}")

                best_candidate = choose_best_diarization_candidate(diarization_candidates)
                best_score = best_candidate["score"]
                res = best_candidate["result"]
                emb = best_candidate["embeddings"]
                res["_diarization"] = {
                    "profile": best_score.profile,
                    "score": best_score.score,
                    "score_scope": "pre_reference_filter",
                    "reasons": best_score.reasons,
                    "candidates": [candidate["score"].to_dict() for candidate in diarization_candidates]
                    + [{"profile": item["profile"], "error": item["error"], "failed": True} for item in failed_candidates],
                }
                self.log(
                    f"Selected diarization profile: {best_score.profile} "
                    f"(score={best_score.score:.1f}, reasons={', '.join(best_score.reasons) or 'ok'})"
                )
                if request["reference_file"] and request["only_matched"]:
                    try:
                        ref_started = time.perf_counter()
                        self.log("Reference speaker matching started")
                        model = self._model(request, selected_model)
                        reference_profiles = self._build_reference_profiles(cancel, model, request)
                        speaker_mapping = self._match_speakers(emb or {}, reference_profiles, request["match_threshold"])
                        self.log(
                            f"Reference speaker matching finished "
                            f"(matches={len(speaker_mapping)}, elapsed={time.perf_counter() - ref_started:.1f}s)"
                        )
                    except Exception as exc:
                        if self._is_cuda_oom(exc):
                            self.log("Reference speaker matching skipped because GPU memory was exhausted")
                            self.release_resources(clear_models=True)
                        else:
                            raise

            glossary_text = ""
            self._set_stage(RunStage.POSTPROCESS, 0.86)
            if request.get("use_glossary"):
                glossary_text = load_glossary_text(self.project_root, request["glossary_text"])
                if glossary_text:
                    self.log(f"Glossary loaded: {len(parse_glossary_rules(glossary_text))} rule(s)")
                self._apply_glossary(res, glossary_text)
            if reference_prefix_sec > 0:
                res = self._trim_result_prefix(res, reference_prefix_sec)
                self.log("Trimmed the introduction file from the final transcript and shifted timestamps to the main recording")
            res = self._cleanup_result_text(res)
            if speaker_mapping:
                for seg in res.get("segments", []):
                    sp = str(seg.get("speaker", "UNKNOWN"))
                    if sp in speaker_mapping:
                        seg["speaker_original"] = sp
                        seg["speaker"] = speaker_mapping[sp]["name"]

            if request["reference_file"] and request["only_matched"] and speaker_mapping:
                matched_names = {m["name"] for m in speaker_mapping.values()}
                res["segments"] = [seg for seg in res.get("segments", []) if str(seg.get("speaker", "")) in matched_names]

            res["text"] = " ".join(seg.get("text", "").strip() for seg in res.get("segments", []) if seg.get("text"))
            res["_quality_summary"] = quality_from_result(res, audio_duration)
            self._set_stage(RunStage.DONE, 1.0)
            return res
        finally:
            self.release_resources(clear_models=False)

class WhisperXApp:
    def __init__(self):
        ctk.set_appearance_mode("Dark")
        ctk.set_default_color_theme("blue")
        self.root = ctk.CTk()
        self.root.title("WhisperX Транскрибация")
        self.root.geometry("1240x860")
        self.root.minsize(1120, 760)

        self.selected_file: str | None = None
        self.reference_file: str | None = None
        self.last_result: dict[str, Any] | None = None
        self.last_output_path: Path | None = None
        self.last_txt_path: Path | None = None
        self.last_journal_path: Path | None = None
        self.history_all_rows: list[dict[str, Any]] = []
        self.history_rows: list[dict[str, Any]] = []
        self.history_export_dir = RESULTS_DIR / "history_exports"
        self._active_job: int | None = None
        self._current_stage = RunStage.QUEUED
        self.live_store = LiveSessionStore()
        self.live_session: LiveSession | None = None
        self.live_pause_event = threading.Event()
        self.live_stop_event = threading.Event()
        self.live_chunk_seconds = 20.0

        self.tm = TaskManager(on_idle=self._on_idle, on_error=self._on_task_error)
        self.service = WhisperXService(
            log_cb=self.log,
            progress_cb=lambda v: self.root.after(0, lambda: self.progress.set(v)),
            stage_cb=lambda stage: self.root.after(0, lambda: self._set_stage(stage)),
        )

        self._build_ui()
        self._load_settings()
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)
        self.log(f"GPU: {torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'N/A'}")

    def _build_ui(self):
        main = ctk.CTkFrame(self.root)
        main.pack(fill="both", expand=True, padx=10, pady=10)
        main.grid_columnconfigure(0, weight=0)
        main.grid_columnconfigure(1, weight=1)
        main.grid_rowconfigure(0, weight=1)

        left = ctk.CTkScrollableFrame(main, width=410, label_text="Настройки")
        left.grid(row=0, column=0, sticky="nsew", padx=(0, 10))
        left.grid_columnconfigure(1, weight=1)

        right = ctk.CTkFrame(main)
        right.grid(row=0, column=1, sticky="nsew")
        right.grid_columnconfigure(0, weight=1)
        right.grid_rowconfigure(3, weight=1)

        self.processing_profile_var = ctk.StringVar(value=get_processing_profile("meeting").label)
        self.stage_var = ctk.StringVar(value=STAGE_LABELS_RU[RunStage.QUEUED])
        self.status_var = ctk.StringVar(value="Свободно")
        self.model_var = ctk.StringVar(value="large-v3")
        self.backend_var = ctk.StringVar(value="whisperx")
        self.lang_var = ctk.StringVar(value="ru")
        self.device_var = ctk.StringVar(value="cuda" if torch.cuda.is_available() else "cpu")
        self.compute_var = ctk.StringVar(value="float16" if torch.cuda.is_available() else "float32")
        self.batch_var = ctk.StringVar(value="8")
        self.beam_var = ctk.StringVar(value="7")
        self.vad_var = ctk.StringVar(value="0.40")
        self.chunk_var = ctk.StringVar(value="20")
        self.min_spk_var = ctk.StringVar(value="2")
        self.max_spk_var = ctk.StringVar(value="12")
        self.preprocess_var = ctk.BooleanVar(value=True)
        self.diarize_var = ctk.BooleanVar(value=True)
        self.auto_model_var = ctk.BooleanVar(value=False)
        self.torch_compile_var = ctk.BooleanVar(value=False)
        self.concat_reference_var = ctk.BooleanVar(value=False)
        self.use_glossary_var = ctk.BooleanVar(value=False)
        self.only_matched_var = ctk.BooleanVar(value=True)
        self.voice_match_threshold_var = ctk.StringVar(value="0.60")

        section_row = 0

        def section(title: str):
            nonlocal section_row
            frame = ctk.CTkFrame(left)
            frame.grid(row=section_row, column=0, columnspan=2, sticky="ew", padx=6, pady=(0, 10))
            frame.grid_columnconfigure(1, weight=1)
            ctk.CTkLabel(frame, text=title, font=ctk.CTkFont(size=14, weight="bold"), anchor="w").grid(
                row=0,
                column=0,
                columnspan=2,
                sticky="ew",
                padx=10,
                pady=(8, 4),
            )
            section_row += 1
            return frame, 1

        def combo(parent, row: int, label: str, values, var) -> int:
            ctk.CTkLabel(parent, text=label).grid(row=row, column=0, sticky="w", padx=10, pady=4)
            ctk.CTkComboBox(parent, values=values, variable=var, width=170).grid(
                row=row,
                column=1,
                sticky="ew",
                padx=10,
                pady=4,
            )
            return row + 1

        files, r = section("Файлы")
        self.file_label = ctk.CTkLabel(files, text="Файл совещания: не выбран", anchor="w")
        self.file_label.grid(row=r, column=0, sticky="ew", padx=10, pady=6)
        ctk.CTkButton(files, text="Обзор", command=self.select_file, width=100).grid(row=r, column=1, sticky="e", padx=10, pady=6)
        r += 1
        self.reference_file_label = ctk.CTkLabel(files, text="Файл представления: не выбран", anchor="w")
        self.reference_file_label.grid(row=r, column=0, sticky="ew", padx=10, pady=6)
        ctk.CTkButton(files, text="Обзор", command=self.select_reference_file, width=100).grid(row=r, column=1, sticky="e", padx=10, pady=6)

        basic, r = section("Основные настройки")
        ctk.CTkLabel(basic, text="Профиль обработки").grid(row=r, column=0, sticky="w", padx=10, pady=4)
        ctk.CTkComboBox(
            basic,
            values=PROFILE_LABELS,
            variable=self.processing_profile_var,
            width=170,
            command=lambda _value: self._apply_processing_profile_to_widgets(),
        ).grid(row=r, column=1, sticky="ew", padx=10, pady=4)
        r += 1
        r = combo(basic, r, "Модель", ["tiny", "base", "small", "medium", "large-v2", "large-v3"], self.model_var)
        r = combo(basic, r, "Язык", ["auto", "ru", "en", "de", "fr", "es", "it", "zh", "ja", "ko"], self.lang_var)
        r = combo(basic, r, "Устройство", ["auto", "cpu", "cuda"], self.device_var)
        ctk.CTkCheckBox(basic, text="Включить диаризацию", variable=self.diarize_var).grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=4)
        r += 1
        ctk.CTkCheckBox(basic, text="Автовыбор модели по сложности аудио", variable=self.auto_model_var).grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=4)

        advanced, r = section("Advanced")
        r = combo(advanced, r, "Backend", ["whisperx", "faster-whisper"], self.backend_var)
        r = combo(advanced, r, "Compute", ["float32", "float16", "int8"], self.compute_var)
        r = combo(advanced, r, "Batch", ["2", "4", "8", "16", "32"], self.batch_var)
        r = combo(advanced, r, "Beam", ["5", "7", "10", "12"], self.beam_var)
        r = combo(advanced, r, "VAD onset", ["0.35", "0.40", "0.45", "0.50"], self.vad_var)
        r = combo(advanced, r, "Chunk", ["15", "20", "30"], self.chunk_var)
        r = combo(advanced, r, "Min speakers", [str(i) for i in range(1, 31)], self.min_spk_var)
        r = combo(advanced, r, "Max speakers", [str(i) for i in range(1, 31)], self.max_spk_var)
        ctk.CTkCheckBox(advanced, text="ASR preprocessing", variable=self.preprocess_var).grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=4)
        r += 1
        ctk.CTkCheckBox(advanced, text="Enable torch.compile (CUDA)", variable=self.torch_compile_var).grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=4)
        r += 1
        ctk.CTkCheckBox(advanced, text="Склеить файл представления с основным аудио", variable=self.concat_reference_var).grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=4)
        r += 1
        ctk.CTkLabel(
            advanced,
            text="HF token берётся из HF_TOKEN; поле ниже только для текущей сессии",
            wraplength=360,
            justify="left",
        ).grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=(8, 4))
        r += 1
        self.hf_entry = ctk.CTkEntry(advanced, show="*")
        self.hf_entry.grid(row=r, column=0, columnspan=2, sticky="ew", padx=10, pady=4)
        r += 1
        r = combo(advanced, r, "Порог совпадения голосов", ["0.45", "0.50", "0.55", "0.60", "0.65", "0.70"], self.voice_match_threshold_var)
        ctk.CTkCheckBox(advanced, text="Только совпавшие голоса из файла представления", variable=self.only_matched_var).grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=4)

        glossary, r = section("Глоссарий и подсказки")
        ctk.CTkCheckBox(glossary, text="Использовать глоссарий", variable=self.use_glossary_var).grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=4)
        r += 1
        ctk.CTkLabel(glossary, text="ФИО из файла представления").grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=(8, 4))
        r += 1
        self.reference_names = ctk.CTkTextbox(glossary, height=72, font=("Consolas", 10))
        self.reference_names.grid(row=r, column=0, columnspan=2, sticky="ew", padx=10, pady=4)
        r += 1
        ctk.CTkLabel(glossary, text="Initial prompt").grid(row=r, column=0, sticky="w", padx=10, pady=4)
        self.prompt_entry = ctk.CTkEntry(glossary)
        self.prompt_entry.grid(row=r, column=1, sticky="ew", padx=10, pady=4)
        r += 1
        ctk.CTkLabel(glossary, text="Hotwords").grid(row=r, column=0, sticky="w", padx=10, pady=4)
        self.hotwords_entry = ctk.CTkEntry(glossary)
        self.hotwords_entry.grid(row=r, column=1, sticky="ew", padx=10, pady=4)
        r += 1
        ctk.CTkButton(glossary, text="Открыть hotwords.txt", command=lambda: self._open_text_file(Path("hotwords.txt"))).grid(row=r, column=0, columnspan=2, sticky="ew", padx=10, pady=4)
        r += 1
        ctk.CTkLabel(glossary, text="Glossary replacements").grid(row=r, column=0, columnspan=2, sticky="w", padx=10, pady=(8, 4))
        r += 1
        self.glossary = ctk.CTkTextbox(glossary, height=110, font=("Consolas", 10))
        self.glossary.grid(row=r, column=0, columnspan=2, sticky="ew", padx=10, pady=4)
        r += 1
        ctk.CTkButton(glossary, text="Открыть glossary.txt", command=lambda: self._open_text_file(Path("glossary.txt"))).grid(row=r, column=0, columnspan=2, sticky="ew", padx=10, pady=(4, 10))

        bar = ctk.CTkFrame(right)
        bar.grid(row=0, column=0, sticky="ew", padx=10, pady=10)
        bar.grid_columnconfigure((0, 1, 2), weight=1)
        self.install_btn = ctk.CTkButton(bar, text="Загрузить модель", command=self.start_install)
        self.run_btn = ctk.CTkButton(bar, text="Транскрибировать", command=self.start_transcribe)
        self.cancel_btn = ctk.CTkButton(bar, text="Отмена", command=self.cancel_job, state="disabled")
        self.export_btn = ctk.CTkButton(bar, text="Экспорт TXT", command=self.export_txt)
        self.open_results_btn = ctk.CTkButton(bar, text="Открыть папку результата", command=self.open_results_folder)
        self.open_json_btn = ctk.CTkButton(bar, text="Открыть JSON", command=lambda: self.open_last_output("json"), state="disabled")
        self.open_txt_btn = ctk.CTkButton(bar, text="Открыть TXT", command=lambda: self.open_last_output("txt"), state="disabled")
        self.refresh_history_btn = ctk.CTkButton(bar, text="Обновить историю", command=self.refresh_history)
        self.diagnostics_btn = ctk.CTkButton(bar, text="Проверить окружение", command=self.run_environment_diagnostics)
        self.history_retry_index_var = ctk.StringVar(value="1")
        self.history_retry_entry = ctk.CTkEntry(bar, textvariable=self.history_retry_index_var, width=64)
        self.retry_history_btn = ctk.CTkButton(bar, text="Повторить №", command=self.retry_history_selection)
        self.install_btn.grid(row=0, column=0, padx=4, pady=8, sticky="ew")
        self.run_btn.grid(row=0, column=1, padx=4, pady=8, sticky="ew")
        self.cancel_btn.grid(row=0, column=2, padx=4, pady=8, sticky="ew")
        self.export_btn.grid(row=1, column=0, padx=4, pady=(0, 8), sticky="ew")
        self.open_results_btn.grid(row=1, column=1, columnspan=2, padx=4, pady=(0, 8), sticky="ew")
        self.open_json_btn.grid(row=2, column=0, padx=4, pady=(0, 8), sticky="ew")
        self.open_txt_btn.grid(row=2, column=1, padx=4, pady=(0, 8), sticky="ew")
        self.refresh_history_btn.grid(row=2, column=2, padx=4, pady=(0, 8), sticky="ew")
        self.diagnostics_btn.grid(row=3, column=0, padx=4, pady=(0, 8), sticky="ew")
        self.history_retry_entry.grid(row=3, column=1, padx=4, pady=(0, 8), sticky="ew")
        self.retry_history_btn.grid(row=3, column=2, padx=4, pady=(0, 8), sticky="ew")

        self.stage_label = ctk.CTkLabel(right, textvariable=self.stage_var, anchor="w")
        self.stage_label.grid(row=1, column=0, sticky="ew", padx=10, pady=(0, 4))

        self.progress = ctk.CTkProgressBar(right)
        self.progress.grid(row=2, column=0, sticky="ew", padx=10, pady=(0, 8))
        self.progress.set(0)

        tabs = ctk.CTkTabview(right)
        tabs.grid(row=3, column=0, sticky="nsew", padx=10, pady=(0, 10))
        tabs.add("Результат")
        tabs.add("Логи")
        tabs.add("История")
        tabs.add("Совещание")
        self.result = ctk.CTkTextbox(tabs.tab("Результат"), font=("Consolas", 10))
        self.result.pack(fill="both", expand=True, padx=8, pady=8)
        self.logs = ctk.CTkTextbox(tabs.tab("Логи"), font=("Consolas", 10))
        self.logs.pack(fill="both", expand=True, padx=8, pady=8)
        history_tab = tabs.tab("История")
        history_tab.grid_columnconfigure(0, weight=1)
        history_tab.grid_rowconfigure(1, weight=1)
        history_tab.grid_rowconfigure(2, weight=2)

        history_filter = ctk.CTkFrame(history_tab)
        history_filter.grid(row=0, column=0, sticky="ew", padx=8, pady=(8, 4))
        history_filter.grid_columnconfigure(5, weight=1)
        ctk.CTkLabel(history_filter, text="День").grid(row=0, column=0, sticky="w", padx=6, pady=6)
        self.history_day_var = ctk.StringVar(value="Все дни")
        self.history_day_combo = ctk.CTkComboBox(
            history_filter,
            values=["Все дни"],
            variable=self.history_day_var,
            width=130,
            command=lambda _value: self.refresh_history(),
        )
        self.history_day_combo.grid(row=0, column=1, sticky="w", padx=6, pady=6)
        ctk.CTkLabel(history_filter, text="Запись №").grid(row=0, column=2, sticky="e", padx=6, pady=6)
        self.history_detail_index_entry = ctk.CTkEntry(
            history_filter,
            textvariable=self.history_retry_index_var,
            width=64,
        )
        self.history_detail_index_entry.grid(row=0, column=3, sticky="w", padx=6, pady=6)
        ctk.CTkButton(history_filter, text="Обновить", command=self.refresh_history).grid(row=0, column=4, sticky="ew", padx=4, pady=6)
        self.history_actions_btn = ctk.CTkButton(history_filter, text="Действия...", command=self.open_history_action_menu)
        self.history_actions_btn.grid(row=0, column=5, sticky="e", padx=4, pady=6)

        self.history = ctk.CTkTextbox(history_tab, font=("Consolas", 10), height=170)
        self.history.grid(row=1, column=0, sticky="nsew", padx=8, pady=(4, 4))
        self.history.bind("<ButtonRelease-1>", self._select_history_from_event)
        self.history.bind("<Button-3>", self._open_history_context_menu)
        self.history_details = ctk.CTkTextbox(history_tab, font=("Consolas", 10))
        self.history_details.grid(row=2, column=0, sticky="nsew", padx=8, pady=(4, 8))
        self._build_live_tab(tabs.tab("Совещание"))
        self.refresh_history()

    def _build_live_tab(self, parent):
        parent.grid_columnconfigure(0, weight=1)
        parent.grid_rowconfigure(3, weight=1)
        parent.grid_rowconfigure(5, weight=1)

        controls = ctk.CTkFrame(parent)
        controls.grid(row=0, column=0, sticky="ew", padx=8, pady=(8, 4))
        controls.grid_columnconfigure((0, 1, 2, 3, 4), weight=1)
        self.live_start_btn = ctk.CTkButton(controls, text="Начать запись", command=self.start_live_session)
        self.live_pause_btn = ctk.CTkButton(controls, text="Пауза", command=self.pause_live_session, state="disabled")
        self.live_resume_btn = ctk.CTkButton(controls, text="Продолжить", command=self.resume_live_session, state="disabled")
        self.live_finish_btn = ctk.CTkButton(controls, text="Завершить", command=self.finish_live_session, state="disabled")
        self.live_open_btn = ctk.CTkButton(controls, text="Открыть папку сессии", command=self.open_live_session_folder, state="disabled")
        self.live_start_btn.grid(row=0, column=0, sticky="ew", padx=4, pady=6)
        self.live_pause_btn.grid(row=0, column=1, sticky="ew", padx=4, pady=6)
        self.live_resume_btn.grid(row=0, column=2, sticky="ew", padx=4, pady=6)
        self.live_finish_btn.grid(row=0, column=3, sticky="ew", padx=4, pady=6)
        self.live_open_btn.grid(row=0, column=4, sticky="ew", padx=4, pady=6)

        settings = ctk.CTkFrame(parent)
        settings.grid(row=1, column=0, sticky="ew", padx=8, pady=4)
        settings.grid_columnconfigure(3, weight=1)
        self.live_chunk_seconds_var = ctk.StringVar(value="20")
        self.live_diarize_var = ctk.BooleanVar(value=False)
        ctk.CTkLabel(settings, text="Длина чанка, сек").grid(row=0, column=0, sticky="w", padx=8, pady=6)
        ctk.CTkComboBox(settings, values=["15", "20", "30"], variable=self.live_chunk_seconds_var, width=90).grid(row=0, column=1, sticky="w", padx=8, pady=6)
        ctk.CTkCheckBox(settings, text="Диаризация во время записи", variable=self.live_diarize_var).grid(row=0, column=2, sticky="w", padx=8, pady=6)
        ctk.CTkLabel(
            settings,
            text="Предпросмотр идёт по чанкам; точный финальный прогон выполняется по полному WAV после завершения.",
            wraplength=620,
            justify="left",
        ).grid(row=1, column=0, columnspan=4, sticky="ew", padx=8, pady=(0, 8))

        self.live_status_var = ctk.StringVar(value="Сессия совещания не запущена")
        self.live_stats_var = ctk.StringVar(value="чанки: 0 в очереди / 0 готово / 0 ошибок")
        ctk.CTkLabel(parent, textvariable=self.live_status_var, anchor="w").grid(row=2, column=0, sticky="ew", padx=8, pady=(6, 2))
        ctk.CTkLabel(parent, textvariable=self.live_stats_var, anchor="w").grid(row=4, column=0, sticky="ew", padx=8, pady=(6, 2))

        self.live_transcript = ctk.CTkTextbox(parent, font=("Consolas", 10), height=220)
        self.live_transcript.grid(row=3, column=0, sticky="nsew", padx=8, pady=4)
        self.live_speakers = ctk.CTkTextbox(parent, font=("Consolas", 10), height=120)
        self.live_speakers.grid(row=5, column=0, sticky="nsew", padx=8, pady=(4, 8))

    def _on_idle(self, _job):
        self._active_job = None
        self.root.after(0, lambda: self._set_busy(False))
        self.service.cleanup_temp_files()

    def _on_close(self):
        self.tm.stop()
        self._save_settings()
        self.service.cleanup_temp_files()
        self.service.release_resources(clear_models=True)
        self.root.destroy()

    def _on_task_error(self, job_id: int, exc: Exception):
        self.log(f"Task {job_id} failed: {exc}")
        self.root.after(0, lambda: messagebox.showerror("Ошибка задачи", str(exc)))

    def _set_busy(self, busy: bool):
        st = "disabled" if busy else "normal"
        self.install_btn.configure(state=st)
        self.run_btn.configure(state=st)
        if hasattr(self, "live_start_btn"):
            self.live_start_btn.configure(state=st)
        if hasattr(self, "diagnostics_btn"):
            self.diagnostics_btn.configure(state=st)
        if hasattr(self, "retry_history_btn"):
            self.retry_history_btn.configure(state=st)
        for name in (
            "history_actions_btn",
        ):
            widget = getattr(self, name, None)
            if widget is not None:
                widget.configure(state=st)
        self.cancel_btn.configure(state="normal" if busy else "disabled")
        self.status_var.set("Занято" if busy else "Свободно")
        if not busy and self._current_stage not in {RunStage.DONE, RunStage.ERROR}:
            self._set_stage(RunStage.QUEUED)
        else:
            self._set_stage(self._current_stage)

    def _set_stage(self, stage: str):
        self._current_stage = stage
        self.stage_var.set(f"{self.status_var.get()} | {STAGE_LABELS_RU.get(stage, stage)}")

    def log(self, msg: str):
        ts = time.strftime("%H:%M:%S")
        self.root.after(0, lambda: (self.logs.insert("end", f"[{ts}] {msg}\n"), self.logs.see("end")))

    def _apply_processing_profile_to_widgets(self):
        profile = get_processing_profile(self.processing_profile_var.get())
        self.processing_profile_var.set(profile.label)
        self.model_var.set(profile.model)
        self.batch_var.set(str(profile.batch_size))
        self.beam_var.set(str(profile.beam_size))
        self.vad_var.set(f"{profile.vad_onset:.2f}")
        self.chunk_var.set(str(profile.chunk_size))
        self.preprocess_var.set(profile.preprocess_asr)
        self.log(f"Processing profile selected: {profile.label} ({profile.key})")

    def _set_live_controls(self, active: bool, paused: bool = False):
        if not hasattr(self, "live_start_btn"):
            return
        self.live_start_btn.configure(state="disabled" if active or self.tm.busy() else "normal")
        self.live_pause_btn.configure(state="normal" if active and not paused else "disabled")
        self.live_resume_btn.configure(state="normal" if active and paused else "disabled")
        self.live_finish_btn.configure(state="normal" if active else "disabled")
        self.live_open_btn.configure(state="normal" if self.live_session is not None else "disabled")

    def _update_live_view(self, session: LiveSession | None = None):
        session = session or self.live_session
        if session is None or not hasattr(self, "live_status_var"):
            return
        status_labels = {
            LiveStatus.RECORDING: "запись",
            LiveStatus.PAUSED: "пауза",
            LiveStatus.PROCESSING: "обработка",
            LiveStatus.FINALIZING: "финальный прогон",
            LiveStatus.DONE: "готово",
            LiveStatus.FAILED: "ошибка",
        }
        queued = len([chunk for chunk in session.chunks if chunk.status == LiveChunkStatus.QUEUED])
        processing = len([chunk for chunk in session.chunks if chunk.status == LiveChunkStatus.PROCESSING])
        done = len([chunk for chunk in session.chunks if chunk.status == LiveChunkStatus.DONE])
        failed = len([chunk for chunk in session.chunks if chunk.status == LiveChunkStatus.FAILED])
        self.live_status_var.set(
            f"{session.session_id} | {status_labels.get(session.status, session.status)} | "
            f"начато={session.started_at} | завершено={session.finished_at or '-'}"
        )
        self.live_stats_var.set(
            f"чанки: {queued} в очереди / {processing} обрабатывается / {done} готово / {failed} ошибок"
        )
        self.live_transcript.delete("1.0", "end")
        self.live_transcript.insert("end", session.partial_transcript or "Здесь появится предварительная расшифровка.\n")
        self.live_speakers.delete("1.0", "end")
        stable_names = (session.speaker_registry or {}).get("stable_names") or {}
        if stable_names:
            for stable_id, name in stable_names.items():
                self.live_speakers.insert("end", f"{stable_id}: {name}\n")
        else:
            self.live_speakers.insert("end", "Список спикеров пока пуст.\n")

    def start_live_session(self):
        if self.tm.busy():
            self._show_error("Задача уже выполняется", "Дождитесь завершения текущей задачи или отмените её.")
            return
        if not SoundDeviceChunkRecorder.is_available():
            self._show_error(
                "Микрофон недоступен",
                "Для live-записи установите зависимость sounddevice и проверьте устройство ввода.",
            )
            return
        try:
            self.live_chunk_seconds = float(self.live_chunk_seconds_var.get())
        except Exception:
            self.live_chunk_seconds = 20.0
            self.live_chunk_seconds_var.set("20")
        request = self._build_service_request()
        request["final_diarize"] = bool(request.get("diarize"))
        request["diarize"] = bool(self.live_diarize_var.get())
        if (request["diarize"] or request["final_diarize"]) and not request["hf_token"]:
            self._show_error("Отсутствует токен", "Для live-диаризации требуется HF_TOKEN или session-only token.")
            return
        profile = get_processing_profile(request.get("processing_profile"))
        self.live_session = self.live_store.create_session(profile=profile.key)
        self.live_pause_event.clear()
        self.live_stop_event.clear()
        self._update_live_view(self.live_session)
        self._set_live_controls(active=True, paused=False)
        self._active_job = self.tm.submit(lambda cancel: self._live_worker(cancel, request))
        self._set_busy(True)
        self.log(f"Сессия совещания запущена: {self.live_session.session_id}")

    def pause_live_session(self):
        self.live_pause_event.set()
        if self.live_session is not None:
            self.live_store.set_status(self.live_session, LiveStatus.PAUSED)
            self._update_live_view(self.live_session)
        self._set_live_controls(active=True, paused=True)

    def resume_live_session(self):
        self.live_pause_event.clear()
        if self.live_session is not None:
            self.live_store.set_status(self.live_session, LiveStatus.RECORDING)
            self._update_live_view(self.live_session)
        self._set_live_controls(active=True, paused=False)

    def finish_live_session(self):
        if self.live_session is None:
            return
        self.live_stop_event.set()
        self.live_store.set_status(self.live_session, LiveStatus.FINALIZING)
        self._update_live_view(self.live_session)
        self._set_live_controls(active=True, paused=False)
        self.log(f"Сессия совещания завершается: {self.live_session.session_id}")

    def open_live_session_folder(self):
        if self.live_session is None:
            self._show_error("Сессия не выбрана", "Live-сессия ещё не создана.")
            return
        try:
            os.startfile(str(Path(self.live_session.root_dir).resolve()))
        except Exception as exc:
            self._show_error("Ошибка открытия папки", str(exc))

    def _chunk_transcript_text(self, chunk: LiveChunk) -> str:
        if chunk.segments:
            lines = []
            for segment in chunk.segments:
                speaker = segment.get("speaker") or "UNKNOWN"
                text = str(segment.get("text") or "").strip()
                if text:
                    lines.append(f"[чанк {chunk.chunk_id:04d}] {speaker}: {text}")
            return "\n".join(lines)
        return f"[чанк {chunk.chunk_id:04d}] {chunk.text}".strip()

    def _offset_segments(self, segments: list[dict[str, Any]], offset_sec: float) -> list[dict[str, Any]]:
        updated: list[dict[str, Any]] = []
        for segment in segments:
            item = dict(segment)
            for key in ("start", "end"):
                if key in item:
                    try:
                        item[key] = float(item[key]) + offset_sec
                    except (TypeError, ValueError):
                        pass
            words = []
            for word in item.get("words") or []:
                if not isinstance(word, dict):
                    continue
                word_item = dict(word)
                for key in ("start", "end"):
                    if key in word_item:
                        try:
                            word_item[key] = float(word_item[key]) + offset_sec
                        except (TypeError, ValueError):
                            pass
                words.append(word_item)
            if words:
                item["words"] = words
            updated.append(item)
        return updated

    def _process_live_chunk(
        self,
        cancel: threading.Event,
        session: LiveSession,
        chunk: LiveChunk,
        request: dict[str, Any],
        registry: SpeakerRegistry,
    ) -> None:
        self.live_store.update_chunk(session, chunk, status=LiveChunkStatus.PROCESSING)
        self.root.after(0, lambda: self._update_live_view(session))
        chunk_request = dict(request)
        chunk_request.update(
            {
                "selected_file": chunk.audio_path,
                "reference_file": "",
                "concat_reference": False,
                "auto_model": False,
            }
        )
        result = self.service.transcribe(cancel, chunk_request)
        if not result:
            raise RuntimeError("Пустой результат обработки чанка")
        segments = self._offset_segments(result.get("segments") or [], chunk.global_offset_sec)
        segments, speaker_map = registry.apply_to_segments(segments)
        result["segments"] = segments
        result["text"] = " ".join(str(seg.get("text") or "").strip() for seg in segments if seg.get("text"))
        chunk_json_path = Path(chunk.audio_path).with_suffix(".json")
        atomic_write_json(chunk_json_path, result)
        self.live_store.update_chunk(
            session,
            chunk,
            status=LiveChunkStatus.DONE,
            asr_json_path=str(chunk_json_path),
            text=str(result.get("text") or ""),
            segments=segments,
            speaker_map=speaker_map,
        )
        self.live_store.update_speaker_registry(session, registry)
        self.live_store.append_partial_transcript(session, self._chunk_transcript_text(chunk))
        self.root.after(0, lambda: self._update_live_view(session))

    def _finalize_live_session(self, cancel: threading.Event, session: LiveSession, request: dict[str, Any]) -> None:
        self.live_store.set_status(session, LiveStatus.FINALIZING)
        self.root.after(0, lambda: self._update_live_view(session))
        final_dir = self.live_store.final_dir(session)
        chunk_paths = [Path(chunk.audio_path) for chunk in session.chunks if Path(chunk.audio_path).exists()]
        final_wav = final_dir / f"{session.session_id}.wav"
        concatenate_wav_files(chunk_paths, final_wav)

        final_request = dict(request)
        final_request.update(
            {
                "selected_file": str(final_wav),
                "reference_file": "",
                "concat_reference": False,
                "diarize": bool(request.get("final_diarize")),
            }
        )
        timestamp = time.strftime("%Y%m%d_%H%M%S")
        result_paths = unique_result_paths(final_dir, final_wav, timestamp)
        json_path = result_paths["json"]
        txt_path = result_paths["txt"]
        quality_path = result_paths["quality"]
        journal = RunJournal(
            path=result_paths["journal"],
            input_path=str(final_wav),
            profile=str(final_request.get("processing_profile") or "meeting"),
            model=str(final_request.get("model") or ""),
            backend=str(final_request.get("backend") or "whisperx"),
            device=str(final_request.get("device") or ""),
        )
        journal.write()
        journal.update(stage=RunStage.ASR, status=RunStatus.RUNNING)
        result = self.service.transcribe(cancel, final_request)
        if not result:
            raise RuntimeError("Пустой результат финального прогона live-сессии")
        journal.update(stage=RunStage.SAVE)
        text = self._render_dialogue_text(result)
        atomic_write_json(json_path, result)
        atomic_write_text(txt_path, text)
        quality = result.get("_quality_summary") or quality_from_result(result)
        output_paths = {
            "audio": str(final_wav),
            "json": str(json_path),
            "txt": str(txt_path),
            "quality": str(quality_path),
        }
        atomic_write_text(
            quality_path,
            format_quality_report(quality, input_path=str(final_wav), output_paths=output_paths),
        )
        journal.finish(output_paths=output_paths, quality=quality)
        output_paths["journal"] = str(journal.path)
        self.live_store.set_final_outputs(session, output_paths)
        self.live_store.set_status(session, LiveStatus.DONE)
        self.root.after(0, lambda: self._show_completed_result(result, text, self._format_quality_summary(quality), output_paths, str(journal.path)))
        self.root.after(0, lambda: self._update_live_view(session))

    def _live_worker(self, cancel: threading.Event, request: dict[str, Any]):
        session = self.live_session
        if session is None:
            return
        recorder = SoundDeviceChunkRecorder()
        registry = SpeakerRegistry(session.speaker_registry)
        chunk_queue: Queue[LiveChunk | None] = Queue()
        recording_started = time.monotonic()

        def record_loop():
            try:
                while not cancel.is_set() and not self.live_stop_event.is_set():
                    if self.live_pause_event.is_set():
                        self.live_store.set_status(session, LiveStatus.PAUSED)
                        self.root.after(0, lambda: self._update_live_view(session))
                        time.sleep(0.2)
                        continue
                    self.live_store.set_status(session, LiveStatus.RECORDING)
                    offset_sec = max(0.0, time.monotonic() - recording_started)
                    chunk = self.live_store.create_chunk(session, global_offset_sec=offset_sec)
                    self.root.after(0, lambda: self._update_live_view(session))
                    try:
                        recorder.record_chunk(Path(chunk.audio_path), self.live_chunk_seconds)
                        self.live_store.update_chunk(
                            session,
                            chunk,
                            status=LiveChunkStatus.QUEUED,
                            finished_at=now_iso(),
                        )
                        chunk_queue.put(chunk)
                    except Exception as exc:
                        self.live_store.update_chunk(
                            session,
                            chunk,
                            status=LiveChunkStatus.FAILED,
                            finished_at=now_iso(),
                            error=str(exc),
                        )
                        self.root.after(0, lambda: self._update_live_view(session))
                        raise
            finally:
                chunk_queue.put(None)

        recorder_thread = threading.Thread(target=record_loop, daemon=True)
        recorder_thread.start()
        try:
            while True:
                item = chunk_queue.get()
                try:
                    if item is None:
                        break
                    if cancel.is_set():
                        break
                    self._process_live_chunk(cancel, session, item, request, registry)
                except Exception as exc:
                    if item is not None:
                        self.live_store.update_chunk(
                            session,
                            item,
                            status=LiveChunkStatus.FAILED,
                            finished_at=now_iso(),
                            error=str(exc),
                        )
                        self.root.after(0, lambda: self._update_live_view(session))
                    self.log(f"Ошибка обработки live-чанка: {exc}")
                finally:
                    chunk_queue.task_done()
            recorder_thread.join(timeout=2.0)
            if cancel.is_set():
                self.live_store.set_status(session, LiveStatus.FAILED, error="Сессия совещания отменена")
                self.root.after(0, lambda: self._update_live_view(session))
                return
            done_chunks = [chunk for chunk in session.chunks if chunk.status == LiveChunkStatus.DONE]
            recorded_chunks = [chunk for chunk in session.chunks if Path(chunk.audio_path).exists()]
            if recorded_chunks:
                self._finalize_live_session(cancel, session, request)
            elif not done_chunks:
                raise RuntimeError("Сессия завершилась без записанных аудиочанков")
        except Exception as exc:
            self.live_store.set_status(session, LiveStatus.FAILED, error=str(exc))
            self.root.after(0, lambda: self._update_live_view(session))
            self._show_error("Ошибка Live-сессии", str(exc))
        finally:
            self.root.after(0, lambda: self._set_live_controls(active=False, paused=False))

    def open_results_folder(self):
        try:
            RESULTS_DIR.mkdir(exist_ok=True)
            os.startfile(str(RESULTS_DIR.resolve()))
        except Exception as exc:
            self._show_error("Ошибка открытия папки", str(exc))

    def _last_journal_folder(self) -> Path | None:
        if self.last_journal_path is not None and self.last_journal_path.exists():
            return self.last_journal_path.parent
        return RESULTS_DIR if RESULTS_DIR.exists() else None

    def _update_output_buttons(self):
        if not hasattr(self, "open_json_btn") or not hasattr(self, "open_txt_btn"):
            return
        fallback_ready = self._last_journal_folder() is not None
        json_ready = self.last_output_path is not None and (self.last_output_path.exists() or fallback_ready)
        txt_ready = self.last_txt_path is not None and (self.last_txt_path.exists() or fallback_ready)
        self.open_json_btn.configure(state="normal" if json_ready else "disabled")
        self.open_txt_btn.configure(state="normal" if txt_ready else "disabled")

    def _remember_output_paths(self, output_paths: dict[str, str], journal_path: str | None = None):
        json_path = output_paths.get("json")
        txt_path = output_paths.get("txt")
        if json_path:
            self.last_output_path = Path(json_path)
        if txt_path:
            self.last_txt_path = Path(txt_path)
        if journal_path:
            self.last_journal_path = Path(journal_path)
        self._update_output_buttons()

    def _show_completed_result(
        self,
        result: dict[str, Any],
        text: str,
        summary: str,
        output_paths: dict[str, str],
        journal_path: str,
    ):
        self.last_result = result
        self._remember_output_paths(output_paths, journal_path=journal_path)
        self.result.delete("1.0", "end")
        self.result.insert("end", f"{text}\n\n{summary}")
        self.refresh_history()
        self._show_completion_notification(output_paths)

    def open_last_output(self, kind: str):
        target = self.last_output_path if kind == "json" else self.last_txt_path
        if target is None:
            self._show_error("Файл не найден", "Последний результат ещё не создан.")
            return
        if not target.exists():
            fallback = self._last_journal_folder()
            if fallback is None:
                self._show_error("Файл не найден", "Результат был удалён, и папка журнала недоступна.")
                return
            try:
                self.log(f"Result file is missing, opening journal folder: {fallback}")
                os.startfile(str(fallback.resolve()))
            except Exception as exc:
                self._show_error("Ошибка открытия папки", str(exc))
            return
        try:
            os.startfile(str(target.resolve()))
        except Exception as exc:
            self._show_error("Ошибка открытия файла", str(exc))

    def _open_text_file(self, path: Path):
        try:
            target = ensure_text_file(Path(path))
            os.startfile(str(target.resolve()))
        except Exception as exc:
            self._show_error("Ошибка открытия файла", str(exc))

    def refresh_history(self):
        if not hasattr(self, "history"):
            return
        all_rows = scan_run_history(RESULTS_DIR, limit=200)
        self.history_all_rows = all_rows
        grouped = group_history_by_day(all_rows)
        day_values = ["Все дни"] + list(grouped.keys())
        current_day = self.history_day_var.get() if hasattr(self, "history_day_var") else "Все дни"
        if current_day not in day_values:
            current_day = "Все дни"
            if hasattr(self, "history_day_var"):
                self.history_day_var.set(current_day)
        if hasattr(self, "history_day_combo"):
            self.history_day_combo.configure(values=day_values)
        rows = all_rows if current_day == "Все дни" else grouped.get(current_day, [])
        self.history_rows = rows
        if rows:
            try:
                current_index = int(self.history_retry_index_var.get().strip())
            except ValueError:
                current_index = 1
                self.history_retry_index_var.set("1")
            if current_index < 1 or current_index > len(rows):
                self.history_retry_index_var.set("1")
        self.history.delete("1.0", "end")
        if hasattr(self, "history_details"):
            self.history_details.delete("1.0", "end")
        if not rows:
            self.history.insert("end", "История пока пуста для выбранного дня.\n")
            if hasattr(self, "history_details"):
                self.history_details.insert(
                    "end",
                    "Выберите день и запись, чтобы посмотреть детали запуска, диаризации и скачать TXT.\n",
                )
            return
        last_day = None
        for index, item in enumerate(rows, start=1):
            output_paths = item.get("output_paths") or {}
            journal_path = item.get("_path")
            if self.last_output_path is None and output_paths.get("json"):
                self.last_output_path = Path(output_paths["json"])
                if journal_path:
                    self.last_journal_path = Path(journal_path)
            if self.last_txt_path is None and output_paths.get("txt"):
                self.last_txt_path = Path(output_paths["txt"])
                if journal_path and self.last_journal_path is None:
                    self.last_journal_path = Path(journal_path)
            item_day = history_item_day(item)
            if current_day == "Все дни" and item_day != last_day:
                self.history.insert("end", f"\n{item_day}\n" + "-" * 96 + "\n")
                last_day = item_day
            self.history.insert("end", f"{index:02d}. {format_history_browser_entry(item)}\n")
        self.show_history_details(silent=True)
        self._update_output_buttons()

    def _selected_history_item(self) -> tuple[int, dict[str, Any]] | None:
        if not self.history_rows:
            self.refresh_history()
        if not self.history_rows:
            self._show_error("История пуста", "Нет записей History для выбранного действия.")
            return None
        try:
            index = int(self.history_retry_index_var.get().strip())
        except ValueError:
            self._show_error("Неверный номер", "Введите номер строки из History.")
            return None
        if index < 1 or index > len(self.history_rows):
            self._show_error("Неверный номер", f"Доступны строки History: 1-{len(self.history_rows)}.")
            return None
        return index, self.history_rows[index - 1]

    def _history_index_from_event(self, event) -> int | None:
        if not hasattr(self, "history"):
            return None
        try:
            text_index = self.history.index(f"@{event.x},{event.y}")
            line_number = int(str(text_index).split(".", 1)[0])
            line = self.history.get(f"{line_number}.0", f"{line_number}.end")
        except Exception:
            return None
        match = re.match(r"\s*(\d{1,3})\.", line)
        if not match:
            return None
        index = int(match.group(1))
        if 1 <= index <= len(self.history_rows):
            return index
        return None

    def _select_history_index(self, index: int, *, silent: bool = True) -> bool:
        if index < 1 or index > len(self.history_rows):
            return False
        self.history_retry_index_var.set(str(index))
        self.show_history_details(silent=silent)
        return True

    def _select_history_from_event(self, event):
        index = self._history_index_from_event(event)
        if index is not None:
            self._select_history_index(index, silent=True)

    def _open_history_context_menu(self, event):
        index = self._history_index_from_event(event)
        if index is not None:
            self._select_history_index(index, silent=True)
        self.open_history_action_menu(event)
        return "break"

    def open_history_action_menu(self, event=None):
        if not self.history_rows:
            self.refresh_history()
        if not self.history_rows:
            self._show_error("История пуста", "Нет записей History для действий.")
            return
        menu = Menu(self.root, tearoff=0)
        menu.add_command(label="Детали", command=self.show_history_details)
        menu.add_command(label="Повторить", command=self.retry_history_selection)
        menu.add_separator()
        menu.add_command(label="Спикеры: переименовать/объединить", command=self.edit_history_speakers_selection)
        menu.add_separator()
        menu.add_command(label="Скачать TXT", command=self.download_history_txt_selection)
        menu.add_command(label="Скачать выбранный день", command=self.download_history_day_txt)
        menu.add_separator()
        menu.add_command(label="Открыть TXT", command=lambda: self.open_history_artifact("txt"))
        menu.add_command(label="Открыть JSON", command=lambda: self.open_history_artifact("json"))
        menu.add_command(label="Открыть отчёт качества", command=lambda: self.open_history_artifact("quality"))
        menu.add_command(label="Открыть журнал", command=lambda: self.open_history_artifact("journal"))
        menu.add_command(label="Папка записи", command=self.open_history_folder)
        menu.add_command(label="Папка экспорта", command=self.open_history_export_folder)
        menu.add_separator()
        menu.add_command(label="Удалить запись", command=self.delete_history_selection)
        if event is not None:
            x_root = event.x_root
            y_root = event.y_root
        else:
            try:
                x_root = self.history_actions_btn.winfo_rootx()
                y_root = self.history_actions_btn.winfo_rooty() + self.history_actions_btn.winfo_height()
            except Exception:
                x_root = self.root.winfo_pointerx()
                y_root = self.root.winfo_pointery()
        try:
            menu.tk_popup(x_root, y_root)
        finally:
            menu.grab_release()

    def show_history_details(self, silent: bool = False):
        selected = self._selected_history_item()
        if selected is None:
            if not silent:
                return
            return
        index, item = selected
        details = f"History #{index:02d}\n\n{format_run_history_details(item)}"
        if hasattr(self, "history_details"):
            self.history_details.delete("1.0", "end")
            self.history_details.insert("end", details)

    def open_history_artifact(self, kind: str):
        selected = self._selected_history_item()
        if selected is None:
            return
        _index, item = selected
        output_paths = item.get("output_paths") or {}
        if kind == "journal":
            value = item.get("_path") or item.get("path")
        else:
            value = output_paths.get(kind)
        if not value:
            self._show_error("Файл не найден", f"Для выбранной записи нет пути: {kind}.")
            return
        path = Path(str(value))
        if not path.exists():
            self._show_error("Файл не найден", f"Файл недоступен: {path}")
            return
        try:
            os.startfile(str(path.resolve()))
        except Exception as exc:
            self._show_error("Ошибка открытия файла", str(exc))

    def open_history_folder(self):
        selected = self._selected_history_item()
        if selected is None:
            return
        _index, item = selected
        candidates = [
            item.get("_path"),
            item.get("path"),
            (item.get("output_paths") or {}).get("quality"),
            (item.get("output_paths") or {}).get("txt"),
            (item.get("output_paths") or {}).get("json"),
        ]
        for candidate in candidates:
            if not candidate:
                continue
            folder = Path(str(candidate)).parent
            if folder.exists():
                try:
                    os.startfile(str(folder.resolve()))
                except Exception as exc:
                    self._show_error("Ошибка открытия папки", str(exc))
                return
        self._show_error("Папка не найдена", "Не удалось найти папку выбранной записи History.")

    def edit_history_speakers_selection(self):
        selected = self._selected_history_item()
        if selected is None:
            return
        index, item = selected
        output_paths = item.get("output_paths") or {}
        json_value = output_paths.get("json")
        if not json_value:
            self._show_error("JSON не найден", "Для выбранной записи нет JSON результата.")
            return
        json_path = Path(str(json_value))
        if not json_path.exists():
            self._show_error("JSON не найден", f"Файл недоступен: {json_path}")
            return
        try:
            result = json.loads(json_path.read_text(encoding="utf-8"))
        except Exception as exc:
            self._show_error("Ошибка чтения JSON", str(exc))
            return
        if not isinstance(result, dict):
            self._show_error("Ошибка чтения JSON", "Формат результата не поддерживается.")
            return

        window = ctk.CTkToplevel(self.root)
        window.title(f"Спикеры History #{index:02d}")
        window.geometry("720x520")
        window.minsize(620, 420)
        window.grid_columnconfigure(0, weight=1)
        window.grid_rowconfigure(1, weight=1)
        ctk.CTkLabel(
            window,
            text=(
                "Измените правую часть строк. Чтобы объединить спикеров, задайте им одинаковое имя. "
                "Пример: Спикер 1 = Иван Петров"
            ),
            wraplength=680,
            justify="left",
            anchor="w",
        ).grid(row=0, column=0, sticky="ew", padx=12, pady=(12, 6))
        editor = ctk.CTkTextbox(window, font=("Consolas", 11))
        editor.grid(row=1, column=0, sticky="nsew", padx=12, pady=6)
        editor.insert("1.0", format_speaker_mapping_template(result))

        actions = ctk.CTkFrame(window)
        actions.grid(row=2, column=0, sticky="ew", padx=12, pady=(6, 12))
        actions.grid_columnconfigure((0, 1), weight=1)

        def save_mapping():
            mapping = parse_speaker_mapping_text(editor.get("1.0", "end"))
            if not mapping:
                messagebox.showinfo("Спикеры", "Нет изменений для сохранения.")
                return
            try:
                paths = apply_speaker_mapping_to_history_item(item, mapping)
            except Exception as exc:
                self._show_error("Ошибка сохранения спикеров", str(exc))
                return
            self.log(f"Speaker mapping saved for History #{index}: {mapping}")
            self.refresh_history()
            self.history_retry_index_var.set(str(index))
            self.show_history_details(silent=True)
            window.destroy()
            messagebox.showinfo(
                "Спикеры сохранены",
                f"Обновлены файлы:\n{paths['json']}\n{paths['txt']}",
            )

        ctk.CTkButton(actions, text="Сохранить JSON/TXT", command=save_mapping).grid(row=0, column=0, sticky="ew", padx=4, pady=8)
        ctk.CTkButton(actions, text="Отмена", command=window.destroy).grid(row=0, column=1, sticky="ew", padx=4, pady=8)
        window.transient(self.root)
        window.focus()

    def download_history_txt_selection(self):
        selected = self._selected_history_item()
        if selected is None:
            return
        index, item = selected
        try:
            target = export_history_item_text(item, self.history_export_dir)
            self.log(f"History TXT exported #{index}: {target}")
            os.startfile(str(target.resolve()))
        except Exception as exc:
            self._show_error("Ошибка экспорта TXT", str(exc))

    def download_history_day_txt(self):
        if not self.history_all_rows:
            self.refresh_history()
        day = self.history_day_var.get() if hasattr(self, "history_day_var") else "Все дни"
        try:
            exported = export_history_day_texts(self.history_all_rows, day, self.history_export_dir)
        except Exception as exc:
            self._show_error("Ошибка экспорта дня", str(exc))
            return
        if not exported:
            self._show_error("Нечего экспортировать", "Для выбранного дня нет записей истории.")
            return
        self.log(f"History day exported ({day}): {len(exported)} txt file(s)")
        try:
            os.startfile(str(exported[0].parent.resolve()))
        except Exception as exc:
            self._show_error("Ошибка открытия папки экспорта", str(exc))

    def open_history_export_folder(self):
        try:
            self.history_export_dir.mkdir(parents=True, exist_ok=True)
            os.startfile(str(self.history_export_dir.resolve()))
        except Exception as exc:
            self._show_error("Ошибка открытия папки экспорта", str(exc))

    def delete_history_selection(self):
        selected = self._selected_history_item()
        if selected is None:
            return
        index, item = selected
        input_name = Path(str(item.get("input_path") or "")).name or f"#{index}"
        confirmed = messagebox.askyesno(
            "Удалить запись History",
            (
                f"Удалить запись #{index:02d} ({input_name}) и связанные JSON/TXT/quality/journal файлы?\n\n"
                "Аудиофайлы processed_audio/failed_audio не удаляются."
            ),
        )
        if not confirmed:
            return
        deleted = delete_history_item(item, include_audio=False)
        self.log(f"Deleted history #{index}: {len(deleted)} file(s)")
        self.history_retry_index_var.set("1")
        self.refresh_history()

    def _history_retry_source(self, item: dict[str, Any]) -> Path | None:
        return retry_source_from_history_item(item, MEDIA_EXTENSIONS)

    def retry_history_selection(self):
        if self.tm.busy():
            self._show_error("Задача уже выполняется", "Дождитесь завершения текущей задачи или отмените её.")
            return
        selected = self._selected_history_item()
        if selected is None:
            return
        index, item = selected
        source = self._history_retry_source(item)
        if source is None:
            self._show_error(
                "Файл не найден",
                "Не удалось найти исходный, failed_audio или processed_audio файл для выбранной строки History.",
            )
            return

        self.selected_file = str(source)
        self.file_label.configure(text=source.name)
        self.log(f"Retry from History #{index}: {source}")
        self.start_transcribe()

    def run_environment_diagnostics(self):
        lines: list[str] = []
        try:
            ffmpeg_path, ffprobe_path = require_ffmpeg_tools(extra_roots=[self.service.project_root])
            lines.append(f"FFmpeg: OK ({ffmpeg_path})")
            lines.append(f"FFprobe: OK ({ffprobe_path})")
        except Exception as exc:
            lines.append(f"FFmpeg/FFprobe: ERROR ({exc})")

        cuda_available = torch.cuda.is_available()
        lines.append(f"CUDA: {'OK' if cuda_available else 'not available'}")
        if cuda_available:
            try:
                lines.append(f"GPU: {torch.cuda.get_device_name(0)}")
            except Exception as exc:
                lines.append(f"GPU name: ERROR ({exc})")
        lines.append(f"Torch: {getattr(torch, '__version__', '-')}")
        lines.append(f"WhisperX: {getattr(whisperx, '__version__', '-')}")
        lines.append(f"HF_TOKEN: {'set' if load_hf_token_from_env() else 'not set'}")
        try:
            RESULTS_DIR.mkdir(exist_ok=True)
            probe = RESULTS_DIR / ".write_test"
            atomic_write_text(probe, "ok")
            probe.unlink(missing_ok=True)
            lines.append(f"Results dir write access: OK ({RESULTS_DIR.resolve()})")
        except Exception as exc:
            lines.append(f"Results dir write access: ERROR ({exc})")

        message = "\n".join(lines)
        self.log("Environment diagnostics:\n" + message)
        messagebox.showinfo("Диагностика окружения", message)

    def _show_error(self, title: str, message: str):
        self.log(f"ERROR: {message}")
        self.root.after(0, lambda: messagebox.showerror(title, message))

    def _show_completion_notification(self, output_paths: dict[str, str]):
        txt_path = output_paths.get("txt")
        json_path = output_paths.get("json")
        details = txt_path or json_path or "результат сохранен"
        self.log(f"Transcription completed: {details}")
        try:
            self.root.bell()
        except Exception:
            pass
        messagebox.showinfo("Транскрибация завершена", f"Результат сохранен:\n{details}")

    def _validate_audio_file(self, path: str | None, label: str) -> tuple[bool, str]:
        if not path:
            return False, f"Не выбран файл: {label}"
        p = Path(path)
        if not p.exists() or not p.is_file():
            return False, f"Файл не найден: {path}"
        suffix = p.suffix.lower()
        if suffix not in MEDIA_EXTENSIONS:
            return False, (
                f"Неподдерживаемый формат для '{label}': {p.suffix or '[без расширения]'}\n"
                f"Поддерживаются: {', '.join(sorted(MEDIA_EXTENSIONS))}"
            )
        if suffix in VIDEO_EXTENSIONS:
            service = getattr(self, "service", None)
            extra_root = getattr(service, "project_root", Path.cwd())
            try:
                has_audio = media_has_audio_stream(p, extra_roots=[extra_root])
            except MissingBinaryError as exc:
                return False, str(exc)
            if not has_audio:
                return False, f"В видео нет аудиодорожки: {path}"
        return True, ""

    def _cfg(self):
        dev = self.device_var.get()
        if dev == "auto":
            dev = "cuda" if torch.cuda.is_available() else "cpu"
        compute = self.compute_var.get()
        if dev == "cpu" and compute == "float16":
            compute = "float32"
        lang = None if self.lang_var.get() == "auto" else self.lang_var.get()
        mn, mx = self._safe_int(self.min_spk_var, 2), self._safe_int(self.max_spk_var, 12)
        if mn > mx:
            mn, mx = mx, mn
        return {
            "device": dev,
            "compute": compute,
            "lang": lang,
            "mn": mn,
            "mx": mx,
            "match_threshold": self._safe_float(self.voice_match_threshold_var, 0.60),
            "only_matched": bool(self.only_matched_var.get()),
        }

    def _parse_reference_names(self) -> list[str]:
        raw = self.reference_names.get("1.0", "end").strip()
        return [ln.strip() for ln in raw.splitlines() if ln.strip()] if raw else []

    def _safe_int(self, var: ctk.StringVar, default: int) -> int:
        try:
            return int(var.get())
        except Exception:
            return default

    def _safe_float(self, var: ctk.StringVar, default: float) -> float:
        try:
            return float(var.get())
        except Exception:
            return default

    def _build_service_request(self) -> dict[str, Any]:
        c = self._cfg()
        hf_token = self.hf_entry.get().strip() or load_hf_token_from_env()
        profile = get_processing_profile(self.processing_profile_var.get())
        return {
            "selected_file": self.selected_file or "",
            "reference_file": self.reference_file or "",
            "processing_profile": profile.key,
            "model": self.model_var.get(),
            "backend": self.backend_var.get(),
            "auto_model": bool(self.auto_model_var.get()),
            "use_torch_compile": bool(self.torch_compile_var.get()),
            "concat_reference": bool(self.concat_reference_var.get()),
            "use_glossary": bool(self.use_glossary_var.get()),
            "preprocess": bool(self.preprocess_var.get()),
            "diarize": bool(self.diarize_var.get()),
            "hf_token": hf_token,
            "batch": self._safe_int(self.batch_var, 8),
            "beam": self._safe_int(self.beam_var, 7),
            "vad": self._safe_float(self.vad_var, 0.40),
            "chunk": self._safe_int(self.chunk_var, 20),
            "asr_profile": profile.asr_profile,
            "diar_profile": profile.diarization_profile,
            "initial_prompt": self.prompt_entry.get().strip(),
            "hotwords": self.hotwords_entry.get().strip(),
            "reference_names": self._parse_reference_names(),
            "glossary_text": self.glossary.get("1.0", "end").strip(),
            **c,
        }

    def select_file(self):
        fp = filedialog.askopenfilename(filetypes=MEDIA_FILETYPES)
        if fp:
            ok, err = self._validate_audio_file(fp, "файл совещания")
            if not ok:
                messagebox.showerror("Ошибка файла", err)
                return
            self.selected_file = fp
            self.file_label.configure(text=Path(fp).name)

    def select_reference_file(self):
        fp = filedialog.askopenfilename(filetypes=MEDIA_FILETYPES)
        if fp:
            ok, err = self._validate_audio_file(fp, "файл представления")
            if not ok:
                messagebox.showerror("Ошибка файла", err)
                return
            self.reference_file = fp
            self.reference_file_label.configure(text=Path(fp).name)

    def start_install(self):
        if self.tm.busy():
            self.log("TaskManager занят: дождитесь завершения текущей задачи или отмените её.")
            return
        self._active_job = self.tm.submit(self._install_worker)
        self._set_busy(True)
        self._set_stage(RunStage.ASR)

    def _install_worker(self, cancel: threading.Event):
        _ = cancel
        try:
            self.service.install_model(self._build_service_request())
            self.root.after(0, lambda: self._set_stage(RunStage.DONE))
        except Exception as e:
            self.root.after(0, lambda: self._set_stage(RunStage.ERROR))
            self._show_error("Ошибка загрузки модели", f"Не удалось загрузить модель:\n{e}")

    def start_transcribe(self):
        ok, err = self._validate_audio_file(self.selected_file, "файл совещания")
        if not ok:
            messagebox.showerror("Ошибка файла", err)
            return
        if self.reference_file:
            ok_ref, err_ref = self._validate_audio_file(self.reference_file, "файл представления")
            if not ok_ref:
                self.log(f"Файл представления недоступен, запуск без сопоставления голосов: {err_ref}")
                self.reference_file = None
                self.reference_file_label.configure(text="Файл представления: не выбран")
        request = self._build_service_request()
        if request["diarize"] and not request["hf_token"]:
            messagebox.showerror(
                "Отсутствует токен",
                "Включена диаризация, но HF token не указан.\n"
                "Установите переменную окружения HF_TOKEN, заполните поле только для текущей сессии или отключите диаризацию.",
            )
            return
        if self.tm.busy():
            self.log("TaskManager занят: дождитесь завершения текущей задачи или отмените её.")
            self._set_stage(RunStage.QUEUED)
            return
        try:
            self.service.ensure_audio_dependencies(request)
        except MissingBinaryError as exc:
            messagebox.showerror("Не найден FFmpeg", str(exc))
            self.log(f"ERROR: {exc}")
            return
        self._save_settings()
        self._active_job = self.tm.submit(self._transcribe_worker)
        self._set_busy(True)
        self._set_stage(RunStage.QUEUED)
        self.progress.set(0.05)

    def cancel_job(self):
        if self._active_job is not None:
            self.tm.cancel(self._active_job)

    def _transcribe_worker(self, cancel: threading.Event):
        request = self._build_service_request()
        RESULTS_DIR.mkdir(exist_ok=True)
        timestamp = time.strftime("%Y%m%d_%H%M%S")
        input_path = Path(request.get("selected_file") or self.selected_file or "result")
        result_paths = unique_result_paths(RESULTS_DIR, input_path, timestamp)
        json_path = result_paths["json"]
        txt_path = result_paths["txt"]
        quality_path = result_paths["quality"]
        journal = RunJournal(
            path=result_paths["journal"],
            input_path=str(input_path),
            profile=str(request.get("processing_profile") or "meeting"),
            model=str(request.get("model") or ""),
            backend=str(request.get("backend") or "whisperx"),
            device=str(request.get("device") or ""),
        )
        journal.write()
        current_stage = RunStage.QUEUED
        try:
            if request["diarize"] and not request["hf_token"]:
                raise RuntimeError("Для диаризации требуется HF token")
            current_stage = RunStage.ASR
            journal.update(stage=current_stage, status=RunStatus.RUNNING)
            res = self.service.transcribe(cancel, request)
            if not res:
                journal.fail(error="Cancelled or empty result", stage=self._current_stage or current_stage)
                self.root.after(0, self.refresh_history)
                return

            current_stage = RunStage.SAVE
            journal.update(stage=current_stage)
            txt = self._render_dialogue_text(res)
            atomic_write_json(json_path, res)
            atomic_write_text(txt_path, txt)
            quality = res.get("_quality_summary") or quality_from_result(res)
            output_paths = {"json": str(json_path), "txt": str(txt_path), "quality": str(quality_path)}
            atomic_write_text(
                quality_path,
                format_quality_report(quality, input_path=str(input_path), output_paths=output_paths),
            )
            journal.finish(
                output_paths=output_paths,
                quality=quality,
            )
            summary = self._format_quality_summary(quality)
            self.root.after(
                0,
                lambda: self._show_completed_result(res, txt, summary, output_paths, str(journal.path)),
            )
            self.log(f"Saved JSON: {json_path}")
            self.log(f"Saved TXT: {txt_path}")
            self.log(f"Saved quality report: {quality_path}")
            self.root.after(0, lambda: self.progress.set(1.0))
        except Exception as e:
            journal.fail(error=str(e), stage=self._current_stage or current_stage)
            self.root.after(0, self.refresh_history)
            self.root.after(0, lambda: self._set_stage(RunStage.ERROR))
            self._show_error("Ошибка обработки", f"Транскрибация завершилась ошибкой:\n{e}")

    def export_txt(self):
        if not self.last_result:
            messagebox.showerror("Error", "No result")
            return
        fp = filedialog.asksaveasfilename(defaultextension=".txt", filetypes=[("Text", "*.txt")])
        if not fp:
            return
        atomic_write_text(Path(fp), self.result.get("1.0", "end"))
        self.log(f"TXT exported: {fp}")

    def _format_quality_summary(self, quality: dict[str, Any]) -> str:
        duration = float(quality.get("audio_duration") or 0.0)
        largest_gap = float(quality.get("largest_gap") or 0.0)
        coverage_end = float(quality.get("coverage_end") or 0.0)
        return (
            "Quality summary\n"
            f"Segments: {int(quality.get('segment_count') or 0)}\n"
            f"Audio duration: {duration / 60.0:.1f} min\n"
            f"Largest gap: {largest_gap / 60.0:.1f} min\n"
            f"Last covered point: {coverage_end / 60.0:.1f} min\n"
            f"Speakers: {int(quality.get('speaker_count') or 0)}\n"
            f"Diarization: {quality.get('diarization_profile') or '-'} "
            f"(score={float(quality.get('diarization_score') or 0.0):.1f})"
        )

    def _load_settings(self):
        if not SETTINGS_PATH.exists():
            return
        try:
            s = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
        except Exception:
            return
        profile = get_processing_profile(s.get("processing_profile") or s.get("profile"))
        self.processing_profile_var.set(profile.label)
        self.model_var.set(str(s.get("last_model") or s.get("model") or self.model_var.get()))
        self.backend_var.set(str(s.get("backend") or self.backend_var.get()))
        self.lang_var.set(str(s.get("last_language") or s.get("lang") or self.lang_var.get()))
        self.device_var.set(str(s.get("device") or self.device_var.get()))
        self.compute_var.set(str(s.get("compute_type") or s.get("compute") or self.compute_var.get()))
        self.batch_var.set(str(s.get("batch_size") or s.get("batch") or self.batch_var.get()))
        self.beam_var.set(str(s.get("beam_size") or self.beam_var.get()))
        self.vad_var.set(str(s.get("vad_onset") or self.vad_var.get()))
        self.chunk_var.set(str(s.get("chunk_size") or self.chunk_var.get()))
        self.min_spk_var.set(str(s.get("min_speakers") or s.get("mins") or self.min_spk_var.get()))
        self.max_spk_var.set(str(s.get("max_speakers") or s.get("maxs") or self.max_spk_var.get()))
        self.preprocess_var.set(bool(s.get("preprocess_asr", self.preprocess_var.get())))
        self.auto_model_var.set(bool(s.get("auto_model_by_complexity", self.auto_model_var.get())))
        self.torch_compile_var.set(bool(s.get("use_torch_compile", self.torch_compile_var.get())))
        self.concat_reference_var.set(bool(s.get("concat_reference", self.concat_reference_var.get())))
        self.use_glossary_var.set(bool(s.get("use_glossary", False)))
        self.only_matched_var.set(bool(s.get("only_matched_reference", self.only_matched_var.get())))
        self.voice_match_threshold_var.set(str(s.get("voice_match_threshold") or self.voice_match_threshold_var.get()))
        self.prompt_entry.insert(0, str(s.get("initial_prompt") or ""))
        self.hotwords_entry.insert(0, str(s.get("hotwords") or ""))
        self.glossary.insert("1.0", str(s.get("glossary_replacements") or ""))
        self.reference_names.insert("1.0", str(s.get("reference_names") or ""))
        self.reference_file = str(s.get("reference_file") or "").strip() or None
        if self.reference_file:
            self.reference_file_label.configure(text=Path(self.reference_file).name)

    def _save_settings(self):
        p = {
            "processing_profile": get_processing_profile(self.processing_profile_var.get()).key,
            "last_model": self.model_var.get(),
            "model": self.model_var.get(),
            "backend": self.backend_var.get(),
            "last_language": self.lang_var.get(),
            "lang": self.lang_var.get(),
            "device": self.device_var.get(),
            "compute_type": self.compute_var.get(),
            "compute": self.compute_var.get(),
            "batch_size": self._safe_int(self.batch_var, 8),
            "batch": self._safe_int(self.batch_var, 8),
            "beam_size": self._safe_int(self.beam_var, 7),
            "vad_onset": self._safe_float(self.vad_var, 0.40),
            "chunk_size": self._safe_int(self.chunk_var, 20),
            "min_speakers": self._safe_int(self.min_spk_var, 2),
            "mins": self._safe_int(self.min_spk_var, 2),
            "max_speakers": self._safe_int(self.max_spk_var, 12),
            "maxs": self._safe_int(self.max_spk_var, 12),
            "preprocess_asr": bool(self.preprocess_var.get()),
            "auto_model_by_complexity": bool(self.auto_model_var.get()),
            "use_torch_compile": bool(self.torch_compile_var.get()),
            "concat_reference": bool(self.concat_reference_var.get()),
            "use_glossary": bool(self.use_glossary_var.get()),
            "only_matched_reference": bool(self.only_matched_var.get()),
            "voice_match_threshold": self._safe_float(self.voice_match_threshold_var, 0.60),
            "initial_prompt": self.prompt_entry.get(),
            "hotwords": self.hotwords_entry.get(),
            "glossary_replacements": self.glossary.get("1.0", "end").strip(),
            "reference_names": self.reference_names.get("1.0", "end").strip(),
            "reference_file": self.reference_file or "",
        }
        atomic_write_json(SETTINGS_PATH, p)

    def _speaker_display_name(self, raw: str, mapping: dict[str, str], next_idx: list[int]) -> str:
        if raw in mapping:
            return mapping[raw]
        if raw and raw not in {"UNKNOWN", "None"} and not raw.startswith("SPEAKER_"):
            mapping[raw] = raw
            return raw
        label = f"Спикер {next_idx[0]}"
        next_idx[0] += 1
        mapping[raw] = label
        return label

    def _fmt_ts(self, sec: float | int | None) -> str:
        total = max(0, int(float(sec or 0)))
        hh = total // 3600
        mm = (total % 3600) // 60
        ss = total % 60
        return f"{hh:02d}:{mm:02d}:{ss:02d}"

    def _render_dialogue_text(self, result: dict[str, Any]) -> str:
        segments = result.get("segments", []) or []
        if not segments:
            return "Нет реплик для отображения.\n"
        name_map: dict[str, str] = {}
        idx = [1]
        lines: list[str] = []
        current_speaker: str | None = None
        current_text_parts: list[str] = []
        block_start: float | None = None
        block_end: float | None = None
        max_merge_gap_sec = 2.5
        max_block_duration_sec = 45.0

        def flush():
            nonlocal current_speaker, current_text_parts, block_start, block_end
            if not current_speaker or not current_text_parts:
                current_speaker = None
                current_text_parts = []
                block_start = None
                block_end = None
                return
            merged = re.sub(r"\s{2,}", " ", " ".join(current_text_parts)).strip()
            if merged:
                lines.append(f"[{self._fmt_ts(block_start)} - {self._fmt_ts(block_end)}] {current_speaker}: {merged}")
            current_speaker = None
            current_text_parts = []
            block_start = None
            block_end = None

        for seg in segments:
            spk = self._speaker_display_name(str(seg.get("speaker", "UNKNOWN")), name_map, idx)
            text = str(seg.get("text", "")).strip()
            if not text:
                continue
            seg_start = float(seg.get("start", 0) or 0)
            seg_end = float(seg.get("end", seg_start) or seg_start)
            if current_speaker is None:
                current_speaker = spk
                current_text_parts = [text]
                block_start = seg_start
                block_end = seg_end
            elif (
                spk == current_speaker
                and seg_start - float(block_end or seg_start) <= max_merge_gap_sec
                and seg_end - float(block_start or seg_start) <= max_block_duration_sec
            ):
                current_text_parts.append(text)
                block_end = max(float(block_end or seg_end), seg_end)
            else:
                flush()
                current_speaker = spk
                current_text_parts = [text]
                block_start = seg_start
                block_end = seg_end
        flush()
        return "\n".join(lines).strip() + "\n"


if __name__ == "__main__":
    WhisperXApp().root.mainloop()
