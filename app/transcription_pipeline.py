from __future__ import annotations

import asyncio
import os
import re
import subprocess
import traceback
from dataclasses import dataclass
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


def _as_bool(name: str, default: bool = False) -> bool:
    value = os.getenv(name, str(default)).strip().lower()
    return value in {"1", "true", "yes", "y", "on"}


def _as_int(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, str(default)))
    except ValueError:
        return default


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


@dataclass
class PipelineConfig:
    asr_model: str
    asr_backend: str
    language: str | None
    device: str
    compute_type: str
    batch_size: int
    preprocess_asr: bool
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
            batch_size=_as_int("BATCH_SIZE", 8),
            preprocess_asr=_as_bool("PREPROCESS_ASR", True),
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
        self._align = {}
        self._diarizer = {}

    def _asr_key(self, model: str, device: str, compute_type: str, backend: str, options: tuple) -> tuple:
        return (backend, model, device, compute_type, options)

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
        asr_options = tuple(
            sorted(
                {
                    "beam_size": beam_size,
                    "language": language,
                    "vad_onset": vad_onset,
                    "chunk_size": chunk_size,
                    "initial_prompt": initial_prompt or "",
                    "hotwords": hotwords or "",
                }.items()
            )
        )
        key = self._asr_key(model, device, compute_type, backend, asr_options)
        if key not in self._asr:
            if backend == "faster-whisper":
                from faster_whisper import WhisperModel  # type: ignore[import-not-found]

                self._asr[key] = WhisperModel(model, device=device, compute_type=compute_type)
            else:
                self._asr[key] = whisperx.load_model(
                    model,
                    device=device,
                    compute_type=compute_type,
                    language=language,
                    asr_options={
                        "beam_size": beam_size,
                        "initial_prompt": initial_prompt or None,
                        "hotwords": hotwords or None,
                    },
                    vad_options={
                        "vad_onset": float(vad_onset),
                        "chunk_size": int(chunk_size),
                    },
                )
                if PipelineConfig.from_env().use_torch_compile:
                    try:
                        if hasattr(self._asr[key], "model"):
                            self._asr[key].model = torch.compile(self._asr[key].model)
                    except Exception:
                        pass
        return self._asr[key]

    def get_align_model(self, language_code: str, device: str):
        key = (language_code, device)
        if key not in self._align:
            self._align[key] = whisperx.load_align_model(language_code=language_code, device=device)
        return self._align[key]

    def get_diarizer(self, device: str, hf_token: str):
        key = (device, hf_token)
        if key not in self._diarizer:
            self._diarizer[key] = WhisperXDiarizationPipeline(use_auth_token=hf_token, device=device)
        return self._diarizer[key]

    def clear(self) -> None:
        """Release cached model references at the end of a GPU job."""
        self._asr.clear()
        self._align.clear()
        self._diarizer.clear()


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

                asr_path = ctx.audio_path
                if self.config.preprocess_asr or is_video:
                    asr_path = await asyncio.to_thread(self._preprocess_audio, ctx.audio_path, asr=True)
                ctx.asr_audio_path = asr_path

                diar_path = None
                if self.config.enable_diarization:
                    diar_path = await asyncio.to_thread(self._preprocess_audio, ctx.audio_path, asr=False)
                ctx.diar_audio_path = diar_path
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
        for path in (ctx.asr_audio_path, ctx.diar_audio_path):
            if not path or not path.exists():
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

    def _run_asr(self, ctx: PipelineContext) -> dict:
        source = str(ctx.asr_audio_path or ctx.audio_path)
        if self.config.asr_backend == "faster-whisper":
            model = self.cache.get_asr_model(
                self.config.asr_model,
                self.config.device,
                self.config.compute_type,
                "faster-whisper",
                language=self.config.language,
                beam_size=self.config.asr_beam_size,
                vad_onset=self.config.vad_onset,
                chunk_size=self.config.chunk_size,
                initial_prompt=self.config.initial_prompt,
                hotwords=self._hotwords_text,
            )
            segments_iter, info = model.transcribe(
                source,
                beam_size=self.config.asr_beam_size,
                word_timestamps=True,
                language=self.config.language,
                vad_filter=True,
                vad_parameters={"threshold": self.config.vad_onset},
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
            beam_size=self.config.asr_beam_size,
            vad_onset=self.config.vad_onset,
            chunk_size=self.config.chunk_size,
            initial_prompt=self.config.initial_prompt,
            hotwords=self._hotwords_text,
        )
        result = model.transcribe(source, batch_size=self.config.batch_size)
        if not isinstance(result, dict):
            raise RuntimeError("Unexpected ASR result format from whisperx")
        return result

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
            audio_path = str(self._preprocess_audio_profile(ctx.audio_path, profile))
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
