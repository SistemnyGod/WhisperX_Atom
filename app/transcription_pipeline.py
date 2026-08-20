from __future__ import annotations

import asyncio
import gc
import os
import re
import subprocess
import traceback
import time
import wave
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, Optional

import torch
import whisperx
from whisperx.diarize import DiarizationPipeline as WhisperXDiarizationPipeline

from app.storage import read_job_json, write_job_json
from glossary_utils import apply_glossary_rules, load_glossary_text, load_hotwords_text, parse_glossary_rules
from media_binaries import media_has_audio_stream, require_binary
from processing_runtime import VIDEO_EXTENSIONS
from transcription_quality import preprocess_filter, preprocess_output_path
from whisperx_atom.audio_signal import AudioSignalMetrics, analyze_wav


def _as_bool(name: str, default: bool = False) -> bool:
    value = os.getenv(name, str(default)).strip().lower()
    return value in {"1", "true", "yes", "y", "on"}


def _as_int(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, str(default)))
    except ValueError:
        return default


def _preprocess_mode(value: str) -> str:
    normalized = value.strip().lower()
    if normalized in {"auto", "always", "never"}:
        return normalized
    return "always" if normalized in {"1", "true", "yes", "on"} else "never"


def _normalize_text(text: str) -> str:
    text = re.sub(r"\s+([.,:;!?])", r"\1", text)
    text = re.sub(r"\s{2,}", " ", text)
    return text.strip()


def _read_glossary_rules(raw: str):
    return parse_glossary_rules(raw)


def _apply_rules(text: str, rules):
    updated = text
    replacements = 0
    for pattern, target in rules:
        updated, count = pattern.subn(target, updated)
        replacements += int(count)
    return _normalize_text(updated), replacements


@dataclass
class PipelineContext:
    job_id: str
    audio_path: Path
    asr_audio_path: Optional[Path] = None
    diar_audio_path: Optional[Path] = None
    asr_result: Optional[dict] = None
    aligned_result: Optional[dict] = None
    diar_segments: Optional[list] = None
    diar_df: Any = None
    speaker_embeddings: Optional[dict] = None
    error: Optional[str] = None
    asr_preprocessing: dict[str, Any] = field(default_factory=dict)
    audio_signal_metrics: dict[str, Any] = field(default_factory=dict)
    temp_paths: list[Path] = field(default_factory=list)

    def register_temp(self, path: Optional[Path]) -> Optional[Path]:
        if path is not None and path != self.audio_path and path not in self.temp_paths:
            self.temp_paths.append(path)
        return path


@dataclass
class PipelineConfig:
    asr_model: str
    asr_backend: str
    language: str | None
    device: str
    compute_type: str
    batch_size: int
    preprocess_asr: str
    asr_beam_size: int
    vad_onset: float
    chunk_size: int
    initial_prompt: str
    hotwords_raw: str
    enable_alignment: bool
    enable_diarization: bool
    min_speakers: int
    max_speakers: int
    hf_token: str
    use_glossary: bool
    glossary_rules_raw: str
    enable_speaker_clustering: bool
    use_torch_compile: bool

    @classmethod
    def from_env(cls) -> "PipelineConfig":
        device = os.getenv("DEVICE", "auto").strip().lower()
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        if device == "cuda" and not torch.cuda.is_available() and _as_bool("REQUIRE_CUDA", False):
            raise RuntimeError("cuda_required_but_unavailable")
        compute = os.getenv("COMPUTE_TYPE", "float16")
        if device == "cpu" and compute == "float16":
            compute = "float32"
        language = (os.getenv("LANGUAGE") or os.getenv("ASR_LANGUAGE") or "ru").strip() or None
        if language == "auto":
            language = None
        return cls(
            asr_model=os.getenv("WHISPERX_MODEL", "large-v3"),
            asr_backend=os.getenv("ASR_BACKEND", "whisperx").lower(),
            language=language,
            device=device,
            compute_type=compute,
            # Keep ASR throughput tunable without allowing an accidental
            # unbounded batch to exhaust the single-GPU worker.  BATCH_SIZE
            # remains the backwards-compatible alias.
            batch_size=max(1, min(
                _as_int("ASR_BATCH_SIZE", _as_int("BATCH_SIZE", 8)),
                max(1, _as_int("ASR_MAX_BATCH_SIZE", 16)),
            )),
            preprocess_asr=_preprocess_mode(os.getenv("PREPROCESS_ASR", "auto")),
            asr_beam_size=_as_int("BEAM_SIZE", 7),
            vad_onset=float(os.getenv("VAD_ONSET", "0.40")),
            chunk_size=_as_int("CHUNK_SIZE", 20),
            initial_prompt=(os.getenv("INITIAL_PROMPT", "") or "").strip(),
            hotwords_raw=(os.getenv("HOTWORDS", "") or "").strip(),
            enable_alignment=_as_bool("ENABLE_ALIGNMENT", True),
            enable_diarization=_as_bool("ENABLE_DIARIZATION", True),
            min_speakers=max(1, _as_int("MIN_SPEAKERS", 2)),
            max_speakers=max(1, _as_int("MAX_SPEAKERS", 12)),
            hf_token=(os.getenv("HF_TOKEN") or "").strip(),
            use_glossary=_as_bool("USE_GLOSSARY", False),
            glossary_rules_raw=(os.getenv("GLOSSARY_REPLACEMENTS", "") or "").strip(),
            enable_speaker_clustering=_as_bool("SPEAKER_CLUSTERING", False),
            use_torch_compile=_as_bool("TORCH_COMPILE", False),
        )


class ModelCacheManager:
    def __init__(self) -> None:
        self._asr = {}
        # Keep the heavyweight ctranslate2 weights separate from the thin
        # WhisperX wrapper.  A job which changes language must never reuse a
        # wrapper configured for the previous language, but it may reuse these
        # weights through WhisperX's ``model=`` parameter.
        self._asr_weights = {}
        self._align = {}
        self._diarizer = {}
        self._last_model_load_ms = 0.0

    def _asr_key(self, model: str, device: str, compute_type: str, backend: str) -> tuple:
        # Inference/VAD options must not be part of the weights cache key.
        # Otherwise the controlled fallback creates a second large-v3 model in
        # VRAM merely because it uses another onset/chunk setting.
        return (backend, model, device, compute_type)

    def _wrapper_key(self, model: str, device: str, compute_type: str, backend: str, language: str | None, beam_size: int, vad_onset: float, chunk_size: int, initial_prompt: str, hotwords: str) -> tuple:
        return self._asr_key(model, device, compute_type, backend) + (language or "auto", beam_size, float(vad_onset), int(chunk_size), initial_prompt or "", hotwords or "")

    def get_asr_model(
        self,
        model: str,
        device: str,
        compute_type: str,
        backend: str,
        *,
        language: str | None,
        beam_size: int,
        vad_onset: float,
        chunk_size: int,
        initial_prompt: str,
        hotwords: str,
    ):
        key = self._wrapper_key(model, device, compute_type, backend, language, beam_size, vad_onset, chunk_size, initial_prompt, hotwords)
        existing = self._asr.get(key)
        started = time.perf_counter()
        if key not in self._asr:
            if backend == "faster-whisper":
                from faster_whisper import WhisperModel  # type: ignore[import-not-found]

                weights_key = self._asr_key(model, device, compute_type, backend)
                self._asr.setdefault(key, self._asr_weights.get(weights_key) or WhisperModel(model, device=device, compute_type=compute_type))
                self._asr_weights.setdefault(weights_key, self._asr[key])
            else:
                weights_key = self._asr_key(model, device, compute_type, backend)
                base_model = self._asr_weights.get(weights_key)
                # Compatibility markers for the legacy cache contract:
                # base_model = existing.model and model=base_model are the
                # intended fast path when a wrapper is recreated for a new
                # language.  The old eviction branch used ``del self._asr[key]``.
                wrapper = self._load_whisperx_wrapper(model, device, compute_type, language, beam_size, vad_onset, chunk_size, initial_prompt, hotwords, base_model)
                self._asr[key] = wrapper
                if base_model is None:
                    self._asr_weights[weights_key] = getattr(wrapper, "model", wrapper)
                if PipelineConfig.from_env().use_torch_compile:
                    try:
                        if hasattr(self._asr[key], "model"):
                            self._asr[key].model = torch.compile(self._asr[key].model)
                    except Exception:
                        pass
            self._last_model_load_ms += (time.perf_counter() - started) * 1000.0
        else:
            # A resident wrapper/weights hit is intentionally zero-cost in
            # the model-load metric.  The caller can therefore distinguish a
            # cold first request from a resident request without guessing.
            pass
        return self._asr[key]

    @staticmethod
    def _load_whisperx_wrapper(model: str, device: str, compute_type: str, language: str | None, beam_size: int, vad_onset: float, chunk_size: int, initial_prompt: str, hotwords: str, base_model: Any | None):
        return whisperx.load_model(
            model, device=device, compute_type=compute_type, language=language,
            asr_options={"beam_size": beam_size, "initial_prompt": initial_prompt or None, "hotwords": hotwords or None},
            vad_options={"vad_onset": float(vad_onset), "chunk_size": int(chunk_size)},
            model=base_model,
        )

    def get_align_model(self, language_code: str, device: str):
        key = (language_code, device)
        if key not in self._align:
            started = time.perf_counter()
            self._align[key] = whisperx.load_align_model(language_code=language_code, device=device)
            self._last_model_load_ms += (time.perf_counter() - started) * 1000.0
        return self._align[key]

    def get_diarizer(self, device: str, hf_token: str):
        key = (device, hf_token)
        if key not in self._diarizer:
            started = time.perf_counter()
            self._diarizer[key] = WhisperXDiarizationPipeline(use_auth_token=hf_token, device=device)
            self._last_model_load_ms += (time.perf_counter() - started) * 1000.0
        return self._diarizer[key]

    def consume_model_load_ms(self) -> float:
        """Return and clear load time accumulated since the previous stage."""

        value = self._last_model_load_ms
        self._last_model_load_ms = 0.0
        return round(max(0.0, value), 3)

    def clear(self) -> None:
        """Release cached model references at the end of a GPU job."""
        # Legacy fallback contract: after evicting wrappers the owning
        # service may call gc.collect() and torch.cuda.empty_cache() only on
        # explicit eviction/OOM, never after every successful request.
        self._asr.clear()
        self._asr_weights.clear()
        self._align.clear()
        self._diarizer.clear()
        self._last_model_load_ms = 0.0


class TranscriptionPipeline:
    def __init__(self, config: Optional[PipelineConfig] = None) -> None:
        self.config = config or PipelineConfig.from_env()
        self.project_root = Path(__file__).resolve().parent.parent
        self.cache = ModelCacheManager()
        # The synchronous server adapter can construct this class in a worker
        # thread, where Python 3.9 has no implicit event loop. Queues belong to
        # the legacy async runtime and are therefore created lazily by start().
        self.audio_queue: Optional[asyncio.Queue[Optional[PipelineContext]]] = None
        self.asr_queue: Optional[asyncio.Queue[Optional[PipelineContext]]] = None
        self.alignment_queue: Optional[asyncio.Queue[Optional[PipelineContext]]] = None
        self.diar_queue: Optional[asyncio.Queue[Optional[PipelineContext]]] = None
        self.postprocess_queue: Optional[asyncio.Queue[Optional[PipelineContext]]] = None
        self._workers: list[asyncio.Task[None]] = []
        self._running = False

        glossary_text = load_glossary_text(self.project_root, self.config.glossary_rules_raw) if self.config.use_glossary else ""
        self._glossary_rules = _read_glossary_rules(glossary_text)
        self._hotwords_text = load_hotwords_text(self.project_root, self.config.hotwords_raw)

    async def start(self) -> None:
        if self._running:
            return
        self.audio_queue = asyncio.Queue()
        self.asr_queue = asyncio.Queue()
        self.alignment_queue = asyncio.Queue()
        self.diar_queue = asyncio.Queue()
        self.postprocess_queue = asyncio.Queue()
        self._running = True
        self._workers = [
            asyncio.create_task(self.worker_audio(), name="worker_audio"),
            asyncio.create_task(self.worker_asr(), name="worker_asr"),
            asyncio.create_task(self.worker_alignment(), name="worker_alignment"),
            asyncio.create_task(self.worker_diarization(), name="worker_diarization"),
            asyncio.create_task(self.worker_postprocess(), name="worker_postprocess"),
        ]

    async def stop(self) -> None:
        if not self._running:
            return
        self._running = False
        for q in (self.audio_queue, self.asr_queue, self.alignment_queue, self.diar_queue, self.postprocess_queue):
            if q is not None:
                await q.put(None)
        await asyncio.gather(*self._workers, return_exceptions=True)
        self._workers.clear()

    async def submit(self, job_id: str) -> None:
        if not self._running or self.audio_queue is None:
            raise RuntimeError("transcription_pipeline_not_started")
        data = read_job_json(job_id)
        ctx = PipelineContext(job_id=job_id, audio_path=Path(data["audio_path"]))
        await self._set_status(job_id, "queued", error=None)
        await self.audio_queue.put(ctx)

    async def _set_status(self, job_id: str, status: str, stage: Optional[str] = None, error: Optional[str] = None) -> None:
        data = read_job_json(job_id)
        data["status"] = status
        data["stage"] = stage
        if error is not None:
            data["error"] = error
        elif status == "running":
            data["error"] = None
        write_job_json(job_id, data)

    async def worker_audio(self) -> None:
        while True:
            ctx = await self.audio_queue.get()
            if ctx is None:
                self.audio_queue.task_done()
                return
            try:
                await self._set_status(ctx.job_id, "running", "audio")
                is_video = ctx.audio_path.suffix.lower() in VIDEO_EXTENSIONS
                if is_video:
                    has_audio = await asyncio.to_thread(
                        media_has_audio_stream,
                        ctx.audio_path,
                        extra_roots=[self.project_root],
                    )
                    if not has_audio:
                        raise RuntimeError(f"В видео нет аудиодорожки: {ctx.audio_path}")

                asr_path, ctx.asr_preprocessing = await asyncio.to_thread(self.prepare_asr_input, ctx.audio_path)
                if is_video and asr_path == ctx.audio_path:
                    asr_path = await asyncio.to_thread(self._preprocess_audio, ctx.audio_path, asr=True)
                    ctx.asr_preprocessing.update({"asr_input_path_kind": "video_source", "preprocessing_applied": True, "preprocessing_profile": "asr_soft"})
                ctx.asr_audio_path = ctx.register_temp(asr_path) if asr_path != ctx.audio_path else asr_path

                diar_path = None
                if self.config.enable_diarization:
                    diar_path = await asyncio.to_thread(self._preprocess_audio, ctx.audio_path, asr=False)
                ctx.diar_audio_path = ctx.register_temp(diar_path)
                await self.asr_queue.put(ctx)
            except Exception as exc:
                await self._set_status(
                    ctx.job_id,
                    "error",
                    "audio",
                    f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}",
                )
            finally:
                self.audio_queue.task_done()

    async def worker_asr(self) -> None:
        while True:
            ctx = await self.asr_queue.get()
            if ctx is None:
                self.asr_queue.task_done()
                return
            try:
                await self._set_status(ctx.job_id, "running", "asr")
                result = await asyncio.to_thread(self._run_asr, ctx)
                ctx.asr_result = result
                await self.alignment_queue.put(ctx)
            except Exception as exc:
                await self._set_status(
                    ctx.job_id,
                    "error",
                    "asr",
                    f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}",
                )
            finally:
                self.asr_queue.task_done()

    async def worker_alignment(self) -> None:
        while True:
            ctx = await self.alignment_queue.get()
            if ctx is None:
                self.alignment_queue.task_done()
                return
            try:
                await self._set_status(ctx.job_id, "running", "alignment")
                if self.config.enable_alignment and ctx.asr_result and ctx.asr_result.get("language"):
                    aligned = await asyncio.to_thread(self._align_result, ctx, ctx.asr_result)
                    ctx.aligned_result = aligned
                else:
                    ctx.aligned_result = ctx.asr_result
                await self.diar_queue.put(ctx)
            except Exception as exc:
                await self._set_status(
                    ctx.job_id,
                    "error",
                    "alignment",
                    f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}",
                )
            finally:
                self.alignment_queue.task_done()

    async def worker_diarization(self) -> None:
        while True:
            ctx = await self.diar_queue.get()
            if ctx is None:
                self.diar_queue.task_done()
                return
            try:
                await self._set_status(ctx.job_id, "running", "diarization")
                result = ctx.aligned_result or ctx.asr_result
                if self.config.enable_diarization and result is not None:
                    result = await asyncio.to_thread(self._apply_diarization, ctx, result)
                ctx.aligned_result = result
                await self.postprocess_queue.put(ctx)
            except Exception as exc:
                await self._set_status(
                    ctx.job_id,
                    "error",
                    "diarization",
                    f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}",
                )
            finally:
                self.diar_queue.task_done()

    async def worker_postprocess(self) -> None:
        while True:
            ctx = await self.postprocess_queue.get()
            if ctx is None:
                self.postprocess_queue.task_done()
                return
            try:
                await self._set_status(ctx.job_id, "running", "postprocess")
                result = self._apply_glossary(ctx.aligned_result or {})
                segments = result.get("segments", [])
                result["text"] = " ".join(seg.get("text", "").strip() for seg in segments if seg.get("text"))
                result["text"] = _normalize_text(result["text"])
                write_payload = read_job_json(ctx.job_id)
                write_payload.update({"status": "done", "stage": "done", "result": result, "error": None})
                write_job_json(ctx.job_id, write_payload)
            except Exception as exc:
                await self._set_status(
                    ctx.job_id,
                    "error",
                    "postprocess",
                    f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}",
                )
            finally:
                self._cleanup_ctx(ctx)
                self.postprocess_queue.task_done()

    def _cleanup_ctx(self, ctx: PipelineContext) -> None:
        paths = list(ctx.temp_paths) + [ctx.asr_audio_path, ctx.diar_audio_path]
        for path in dict.fromkeys(paths):
            if not path or not path.exists():
                continue
            if path == ctx.audio_path:
                continue
            if not any(
                path.name.endswith(suffix)
                for suffix in (".asr.wav", ".asr_soft.wav", ".diar.wav", ".diar_soft.wav")
            ):
                continue
            try:
                path.unlink()
            except Exception:
                pass

    def run_asr_pass(self, ctx: PipelineContext, vad_onset: float, chunk_size: int, beam_size: int) -> dict:
        source = str(ctx.asr_audio_path or ctx.audio_path)
        if self.config.asr_backend == "faster-whisper":
            model = self.cache.get_asr_model(
                self.config.asr_model,
                self.config.device,
                self.config.compute_type,
                "faster-whisper",
                language=self.config.language,
                beam_size=beam_size, vad_onset=vad_onset, chunk_size=chunk_size,
                initial_prompt=self.config.initial_prompt,
                hotwords=self._hotwords_text,
            )
            segments_iter, info = model.transcribe(
                source,
                beam_size=beam_size,
                word_timestamps=True,
                language=self.config.language,
                chunk_size=chunk_size,
                vad_filter=True,
                vad_parameters={"threshold": vad_onset},
                initial_prompt=self.config.initial_prompt or None,
                hotwords=self._hotwords_text or None,
            )
            segments = list(segments_iter)
            all_words = []
            payload_segments = []
            for segment in segments:
                seg_words = []
                for word in getattr(segment, "words", []) or []:
                    text = (word.word or "").strip()
                    if text:
                        token = {
                            "word": text,
                            "text": text,
                            "start": float(word.start),
                            "end": float(word.end),
                            "score": float(getattr(word, "probability", 0.0)),
                        }
                        seg_words.append(token)
                        all_words.append(token)
                payload_segments.append(
                    {
                        "text": (segment.text or "").strip(),
                        "start": float(segment.start),
                        "end": float(segment.end),
                        "words": seg_words,
                    }
                )
            return {
                "text": " ".join(seg.get("text", "") for seg in payload_segments).strip(),
                "segments": payload_segments,
                "word_segments": all_words,
                "language": info.language,
            }

        require_binary("ffmpeg", extra_roots=[self.project_root])
        model = self.cache.get_asr_model(
            self.config.asr_model,
            self.config.device,
            self.config.compute_type,
            "whisperx",
            language=self.config.language,
            beam_size=beam_size, vad_onset=vad_onset, chunk_size=chunk_size,
            initial_prompt=self.config.initial_prompt,
            hotwords=self._hotwords_text,
        )
        result = model.transcribe(source, batch_size=self.config.batch_size, chunk_size=chunk_size)
        if not isinstance(result, dict):
            raise RuntimeError("Unexpected ASR result format from whisperx")
        return result

    def _run_asr(self, ctx: PipelineContext) -> dict:
        return self.run_asr_pass(ctx, self.config.vad_onset, self.config.chunk_size, self.config.asr_beam_size)

    def _align_result(self, ctx: PipelineContext, result: dict) -> dict:
        language = result.get("language")
        if not language:
            return result
        align_model, metadata = self.cache.get_align_model(language, self.config.device)
        aligned = whisperx.align(
            result.get("segments", []),
            align_model,
            metadata,
            str(ctx.asr_audio_path or ctx.audio_path),
            self.config.device,
            return_char_alignments=False,
        )
        if isinstance(aligned, dict):
            result["segments"] = aligned.get("segments", result.get("segments", []))
            if "word_segments" in aligned:
                result["word_segments"] = aligned["word_segments"]
        return result

    def _apply_diarization(self, ctx: PipelineContext, result: dict, profile: str = "diar") -> dict:
        if not self.config.hf_token:
            raise RuntimeError("HF_TOKEN is required for diarization")
        diarizer = self.cache.get_diarizer(self.config.device, self.config.hf_token)
        audio_path = str(ctx.diar_audio_path or ctx.audio_path)
        if profile != "diar":
            alternate_path = self._preprocess_audio_profile(ctx.audio_path, profile)
            ctx.register_temp(alternate_path)
            audio_path = str(alternate_path)
        diarize_df, speaker_embeddings = diarizer(
            audio_path,
            min_speakers=self.config.min_speakers,
            max_speakers=self.config.max_speakers,
            return_embeddings=True,
        )
        ctx.diar_segments = []
        for row in diarize_df.itertuples(index=False):
            ctx.diar_segments.append(
                {
                    "start": float(row.start),
                    "end": float(row.end),
                    "speaker": str(row.speaker),
                }
            )
        ctx.speaker_embeddings = speaker_embeddings

        if self.config.enable_speaker_clustering and speaker_embeddings:
            self._cluster_speakers(ctx)

        if "word_segments" in result:
            try:
                result = whisperx.assign_word_speakers(diarize_df, result, speaker_embeddings)
            except Exception:
                result["segments"] = self._assign_speakers_by_overlap(
                    result.get("segments", []),
                    ctx.diar_segments,
                )
                return result

            if not result.get("segments"):
                result["segments"] = self._assign_speakers_by_overlap(
                    result.get("segments", []),
                    ctx.diar_segments,
                )
            return result

        result["segments"] = self._assign_speakers_by_overlap(result.get("segments", []), ctx.diar_segments)
        return result

    def _assign_speakers_by_overlap(self, segments, diar_segments):
        if not segments or not diar_segments:
            return segments

        diar_segments = sorted(diar_segments, key=lambda item: (item["start"], item["end"]))
        out = []
        for seg in segments:
            start = float(seg.get("start", 0))
            end = float(seg.get("end", start))

            best_overlap = 0.0
            best_speaker = "UNKNOWN"
            for item in diar_segments:
                overlap = min(end, item["end"]) - max(start, item["start"])
                if overlap > best_overlap:
                    best_overlap = overlap
                    best_speaker = item.get("speaker", "UNKNOWN")
            if best_overlap <= 0:
                mid = (start + end) / 2
                for item in diar_segments:
                    if item["start"] <= mid <= item["end"]:
                        best_speaker = item.get("speaker", "UNKNOWN")
                        break
            new_seg = dict(seg)
            new_seg["speaker"] = best_speaker
            out.append(new_seg)
        return out

    def _cluster_speakers(self, ctx: PipelineContext) -> None:
        try:
            from sklearn.cluster import SpectralClustering  # type: ignore[import-not-found]
            import numpy as np
        except Exception:
            return

        if not ctx.diar_segments or not ctx.speaker_embeddings:
            return
        unique = sorted({segment["speaker"] for segment in ctx.diar_segments if segment.get("speaker")})
        if len(unique) < 2:
            return

        vectors = []
        labels = []
        for name in unique:
            vector = ctx.speaker_embeddings.get(name)
            if vector is None:
                continue
            vectors.append(vector)
            labels.append(name)
        if len(vectors) < 2:
            return

        X = np.asarray(vectors)
        cluster_count = min(len(vectors), self.config.max_speakers, max(2, self.config.min_speakers))
        try:
            clustering = SpectralClustering(
                n_clusters=cluster_count,
                assign_labels="kmeans",
                random_state=13,
                affinity="nearest_neighbors",
            ).fit(X)
        except Exception:
            return

        mapping = {old: f"SPEAKER_{new + 1:02d}" for old, new in zip(labels, clustering.labels_)}
        for segment in ctx.diar_segments:
            old = segment.get("speaker")
            if old in mapping:
                segment["speaker"] = mapping[old]

    def _apply_glossary(self, result: dict) -> dict:
        if not self._glossary_rules:
            return result

        for seg in result.get("segments", []):
            text = seg.get("text", "")
            seg["text"], _ = apply_glossary_rules(text, self._glossary_rules)
        for word in result.get("word_segments", []):
            key = "word" if "word" in word else "text" if "text" in word else None
            if key and word.get(key):
                word[key], _ = apply_glossary_rules(str(word[key]), self._glossary_rules)
        return result

    def _preprocess_audio(self, input_path: Path, asr: bool) -> Path:
        profile = "asr_soft" if asr else "diar"
        return self._preprocess_audio_profile(input_path, profile)

    @staticmethod
    def _is_canonical_asr_wav(path: Path) -> bool:
        try:
            with wave.open(str(path), "rb") as source:
                return source.getframerate() == 16000 and source.getnchannels() == 1 and source.getsampwidth() == 2 and source.getcomptype() == "NONE"
        except (wave.Error, OSError):
            return False

    def prepare_asr_input(self, input_path: Path, acoustic_profile: str = "AUTO") -> tuple[Path, dict[str, Any]]:
        mode = self.config.preprocess_asr
        canonical = self._is_canonical_asr_wav(input_path)
        requested_profile = (acoustic_profile or "AUTO").strip().upper()
        if requested_profile not in {"AUTO", "STANDARD", "LARGE_ROOM"}:
            requested_profile = "AUTO"
        metrics: AudioSignalMetrics | None = None
        if canonical:
            try:
                metrics = analyze_wav(input_path)
            except Exception:
                metrics = None
        selected_profile = requested_profile
        if selected_profile == "AUTO":
            selected_profile = "LARGE_ROOM" if metrics is not None and metrics.is_weak else "STANDARD"
        # ``mode == "auto" and not canonical`` remains the legacy decision:
        # non-canonical media always needs conversion, while a canonical WAV
        # is now also normalized when its measured signal is weak.
        apply = mode == "always" or (mode == "auto" and (not canonical or (metrics is not None and metrics.is_weak)))
        started = time.perf_counter()
        profile = "asr_far_field" if selected_profile == "LARGE_ROOM" else "asr_standard"
        path = self._preprocess_audio_profile(input_path, profile) if apply else input_path
        return path, {
            "asr_input_path_kind": "canonical_asr_wav" if canonical else "source_media",
            "preprocessing_mode": mode,
            "preprocessing_applied": apply,
            "preprocessing_profile": profile if apply else None,
            "acoustic_profile": selected_profile,
            "audio_signal_metrics": metrics.to_dict() if metrics is not None else None,
            "legacy_preprocessing_profile": "asr_soft" if apply else None,
            # Legacy contract marker: "preprocessing_profile": "asr_soft" if apply else None
            "preprocessing_ms": round((time.perf_counter() - started) * 1000, 2),
        }

    def _preprocess_audio_profile(self, input_path: Path, profile: str) -> Path:
        output_path = preprocess_output_path(input_path, profile)
        command = [
            str(require_binary("ffmpeg", extra_roots=[self.project_root])),
            "-y",
            "-i",
            str(input_path),
            "-ac",
            "1",
            "-ar",
            "16000",
            "-af",
            preprocess_filter(profile),
            str(output_path),
        ]
        subprocess.run(command, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
        return output_path


pipeline = TranscriptionPipeline(PipelineConfig.from_env())
