from __future__ import annotations

import copy
import json
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import torch
import whisperx
from whisperx.diarize import DiarizationPipeline as WhisperXDiarizationPipeline

from diarization_quality import (
    choose_best_diarization_candidate,
    diarization_profiles_for_processing_profile,
    score_diarization_result,
    smooth_speaker_turns,
)
from glossary_utils import apply_glossary_rules, load_glossary_text, load_hotwords_text, parse_glossary_rules
from local_io import atomic_write_json, atomic_write_text
from media_binaries import require_binary
from processing_runtime import (
    RunJournal,
    RunStage,
    RunStatus,
    format_quality_report,
    get_processing_profile,
    quality_from_result,
    unique_result_paths,
)
from runtime_secrets import load_hf_token_from_env
from transcription_quality import (
    is_retry_result_better,
    merge_asr_results,
    preprocess_filter,
    preprocess_output_path,
    should_retry_transcription,
    transcript_health,
)


AUDIO_EXTENSIONS = {".wav", ".mp3", ".m4a", ".ogg", ".flac"}
INPUT_DIR_NAME = "ЗДЕСЬ СКИДЫВАЕМ ФАЙЛ ДЛЯ ТРАНСКРИБАЦИИ"
OUTPUT_DIR_NAME = "ЗДЕСЬ ПОЛУЧАЕМ РЕЗУЛЬТАТ ТРАНСКРИБАЦИИ"

# Reduce mojibake in Windows terminals with non-UTF defaults.
for stream_name in ("stdout", "stderr"):
    stream = getattr(sys, stream_name, None)
    if stream is not None and hasattr(stream, "reconfigure"):
        try:
            stream.reconfigure(encoding="utf-8")
        except Exception:
            pass


@dataclass
class WatchConfig:
    processing_profile: str
    model_name: str
    language: str | None
    device: str
    compute_type: str
    batch_size: int
    min_speakers: int
    max_speakers: int
    beam_size: int
    vad_onset: float
    chunk_size: int
    preprocess_asr: bool
    initial_prompt: str | None
    hotwords: str | None
    hf_token: str
    use_glossary: bool
    glossary_replacements: str
    poll_interval_sec: int = 5
    stable_checks: int = 3
    stable_interval_sec: int = 2

    @classmethod
    def from_settings(cls, settings: dict) -> "WatchConfig":
        profile = get_processing_profile(settings.get("processing_profile") or settings.get("profile"))
        device = settings.get("device", "auto")
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"

        compute_type = settings.get("compute_type") or settings.get("compute") or "float16"
        if device == "cpu" and compute_type == "float16":
            compute_type = "float32"

        language = settings.get("last_language") or settings.get("lang") or "ru"
        if language == "auto":
            language = None

        min_speakers = int(settings.get("min_speakers") or settings.get("mins") or 2)
        max_speakers = int(settings.get("max_speakers") or settings.get("maxs") or 12)
        if min_speakers > max_speakers:
            min_speakers, max_speakers = max_speakers, min_speakers

        return cls(
            processing_profile=profile.key,
            model_name=settings.get("last_model") or settings.get("model") or profile.model,
            language=language,
            device=device,
            compute_type=compute_type,
            batch_size=int(settings.get("batch_size") or settings.get("batch") or profile.batch_size),
            min_speakers=min_speakers,
            max_speakers=max_speakers,
            beam_size=int(settings.get("beam_size") or profile.beam_size),
            vad_onset=float(settings.get("vad_onset") or profile.vad_onset),
            chunk_size=int(settings.get("chunk_size") or profile.chunk_size),
            preprocess_asr=bool(settings.get("preprocess_asr", profile.preprocess_asr)),
            initial_prompt=(settings.get("initial_prompt") or "").strip() or None,
            hotwords=(settings.get("hotwords") or "").strip() or None,
            hf_token=load_hf_token_from_env(),
            use_glossary=bool(settings.get("use_glossary", False)),
            glossary_replacements=(settings.get("glossary_replacements") or "").strip(),
        )

    @property
    def asr_options(self) -> dict:
        return {
            "beam_size": self.beam_size,
            "initial_prompt": self.initial_prompt,
            "hotwords": self.hotwords,
        }

    @property
    def vad_options(self) -> dict:
        return {
            "vad_onset": self.vad_onset,
            "chunk_size": self.chunk_size,
        }


class AutoTranscribeWatcher:
    def __init__(self, root_dir: Path):
        self.root_dir = root_dir
        self.input_dir = root_dir / INPUT_DIR_NAME
        self.output_dir = root_dir / OUTPUT_DIR_NAME
        self.processed_dir = self.output_dir / "processed_audio"
        self.failed_dir = self.output_dir / "failed_audio"
        self.settings_path = root_dir / "whisperx_gui_settings.json"

        self.input_dir.mkdir(exist_ok=True)
        self.output_dir.mkdir(exist_ok=True)
        self.processed_dir.mkdir(exist_ok=True)
        self.failed_dir.mkdir(exist_ok=True)

        self.temp_files: list[Path] = []
        self.settings = self._load_settings_file()
        self.config = WatchConfig.from_settings(self.settings)
        self.config.hotwords = load_hotwords_text(self.root_dir, self.config.hotwords or "") or None
        self.model_cache: dict[str, Any] = {}
        self.model = None
        self.diarizer = None
        self.ffmpeg_path: Path | None = None

    def log(self, message: str):
        timestamp = time.strftime("%H:%M:%S")
        print(f"[{timestamp}] {message}", flush=True)

    def ensure_ffmpeg(self) -> Path:
        if self.ffmpeg_path is None:
            self.ffmpeg_path = require_binary("ffmpeg", extra_roots=[self.root_dir])
        return self.ffmpeg_path

    def _preprocess_with_profile(self, input_path: Path, profile: str, log_message: str) -> Path:
        output_path = preprocess_output_path(input_path, profile)
        command = [
            str(self.ensure_ffmpeg()),
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
        try:
            self.log(log_message)
            subprocess.run(command, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
            self.temp_files.append(output_path)
            return output_path
        except subprocess.CalledProcessError as e:
            details = (e.stderr or b"").decode("utf-8", errors="ignore").strip()
            self.log(f"⚠️ FFmpeg profile '{profile}' failed: {details or e}")
            return input_path
        except Exception as e:
            self.log(f"⚠️ FFmpeg profile '{profile}' skipped: {e}")
            return input_path

    def _load_settings_file(self) -> dict:
        if not self.settings_path.exists():
            return {}
        try:
            return json.loads(self.settings_path.read_text(encoding="utf-8"))
        except Exception as e:
            self.log(f"⚠️ Не удалось прочитать настройки: {e}")
            return {}

    def load_model(self):
        if self.model is not None:
            return self.model

        self.log(
            f"📥 Загрузка модели {self.config.model_name} на {self.config.device} "
            f"(compute_type={self.config.compute_type})..."
        )
        self.model = whisperx.load_model(
            self.config.model_name,
            device=self.config.device,
            compute_type=self.config.compute_type,
            language=self.config.language,
            asr_options=self.config.asr_options,
            vad_options=self.config.vad_options,
        )
        self.log(f"✅ Модель {self.config.model_name} загружена")
        return self.model

    def load_model_variant(
        self,
        *,
        vad_onset: float | None = None,
        chunk_size: int | None = None,
        initial_prompt: str | None = None,
        hotwords: str | None = None,
    ):
        actual_vad_onset = float(self.config.vad_onset if vad_onset is None else vad_onset)
        actual_chunk_size = int(self.config.chunk_size if chunk_size is None else chunk_size)
        actual_initial_prompt = self.config.initial_prompt if initial_prompt is None else initial_prompt
        actual_hotwords = self.config.hotwords if hotwords is None else hotwords
        cache_key = json.dumps(
            {
                "model": self.config.model_name,
                "device": self.config.device,
                "compute_type": self.config.compute_type,
                "language": self.config.language,
                "beam_size": self.config.beam_size,
                "vad_onset": actual_vad_onset,
                "chunk_size": actual_chunk_size,
                "initial_prompt": actual_initial_prompt or "",
                "hotwords": actual_hotwords or "",
            },
            ensure_ascii=False,
            sort_keys=True,
        )
        if cache_key in self.model_cache:
            return self.model_cache[cache_key]

        self.log(
            f"Loading ASR model {self.config.model_name} on {self.config.device} "
            f"(compute_type={self.config.compute_type}, vad_onset={actual_vad_onset:.2f}, chunk_size={actual_chunk_size})..."
        )
        model = whisperx.load_model(
            self.config.model_name,
            device=self.config.device,
            compute_type=self.config.compute_type,
            language=self.config.language,
            asr_options={
                "beam_size": self.config.beam_size,
                "initial_prompt": actual_initial_prompt,
                "hotwords": actual_hotwords,
            },
            vad_options={
                "vad_onset": actual_vad_onset,
                "chunk_size": actual_chunk_size,
            },
        )
        self.model_cache[cache_key] = model
        return model

    def get_diarizer(self):
        if self.diarizer is not None:
            return self.diarizer
        if not self.config.hf_token:
            raise RuntimeError("Для диаризации требуется переменная окружения HF_TOKEN")

        self.log("📥 Загрузка diarization pipeline...")
        self.diarizer = WhisperXDiarizationPipeline(
            use_auth_token=self.config.hf_token,
            device=self.config.device,
        )
        self.log("✅ Diarization pipeline загружен")
        return self.diarizer

    def preprocess_audio_for_asr_soft(self, input_path: Path) -> Path:
        return self._preprocess_with_profile(
            input_path,
            "asr_soft",
            "FFmpeg: мягкая предобработка аудио для ASR...",
        )

    def preprocess_audio_for_diarization(self, input_path: Path) -> Path:
        return self._preprocess_with_profile(
            input_path,
            "diar",
            "FFmpeg: предварительная обработка аудио для диаризации...",
        )

    def preprocess_audio_for_diarization_soft(self, input_path: Path) -> Path:
        return self._preprocess_with_profile(
            input_path,
            "diar_soft",
            "FFmpeg: мягкая очистка аудио для диаризации...",
        )

    def prepare_diarization_audio(self, input_path: Path) -> Path:
        processed = self.preprocess_audio_for_diarization(input_path)
        if processed == input_path:
            processed = self.preprocess_audio_for_diarization_soft(input_path)
        return processed

    def parse_glossary_rules(self):
        if not self.config.use_glossary:
            return []
        raw_text = load_glossary_text(self.root_dir, self.config.glossary_replacements)
        return parse_glossary_rules(raw_text)

    def normalize_transcript_text(self, text: str) -> str:
        text = re.sub(r"\s+([,.:;!?])", r"\1", text)
        text = re.sub(r"\s{2,}", " ", text)
        return text.strip()

    def apply_glossary_to_text(self, text: str, rules):
        updated, replacements = apply_glossary_rules(text, rules)
        return self.normalize_transcript_text(updated), replacements

    def get_word_text(self, word: dict) -> str:
        return str(word.get("text") or word.get("word") or "").strip()

    def join_words(self, words) -> str:
        parts = []
        for word in words:
            token = self.get_word_text(word)
            if not token:
                continue
            if not parts:
                parts.append(token)
                continue
            if re.fullmatch(r"[.,!?;:%)\]»]+", token):
                parts[-1] += token
            elif token in {"-", "—", "–"}:
                parts.append(token)
            elif re.fullmatch(r"[(\[«]+", token):
                parts.append(token)
            else:
                parts.append(" " + token)
        return "".join(parts).strip()

    def smooth_word_speakers(self, words):
        if len(words) < 3:
            return words
        smoothed = [dict(word) for word in words]
        for idx in range(1, len(smoothed) - 1):
            prev_spk = smoothed[idx - 1].get("speaker")
            cur_spk = smoothed[idx].get("speaker")
            next_spk = smoothed[idx + 1].get("speaker")
            if prev_spk and prev_spk == next_spk and cur_spk and cur_spk != prev_spk:
                smoothed[idx]["speaker"] = prev_spk
        return smoothed

    def collapse_short_speaker_runs(self, words, max_run_words: int = 2):
        if len(words) < 3:
            return words
        runs = []
        for word in words:
            speaker = word.get("speaker", "UNKNOWN")
            if runs and runs[-1]["speaker"] == speaker:
                runs[-1]["words"].append(word)
            else:
                runs.append({"speaker": speaker, "words": [word]})
        if len(runs) < 3:
            return words
        for idx in range(1, len(runs) - 1):
            prev_run = runs[idx - 1]
            cur_run = runs[idx]
            next_run = runs[idx + 1]
            if (
                prev_run["speaker"]
                and prev_run["speaker"] == next_run["speaker"]
                and cur_run["speaker"] != prev_run["speaker"]
                and len(cur_run["words"]) <= max_run_words
            ):
                for word in cur_run["words"]:
                    word["speaker"] = prev_run["speaker"]
        collapsed = []
        for run in runs:
            collapsed.extend(run["words"])
        return collapsed

    def segment_word_runs(self, words, default_speaker: str):
        runs = []
        cur_words = []
        cur_speaker = None
        cur_start = None
        cur_end = None

        def flush():
            nonlocal cur_words, cur_speaker, cur_start, cur_end
            if not cur_words:
                return
            text = self.join_words(cur_words)
            if text:
                runs.append(
                    {
                        "speaker": cur_speaker or default_speaker or "UNKNOWN",
                        "start": float(cur_start if cur_start is not None else 0),
                        "end": float(cur_end if cur_end is not None else 0),
                        "words": list(cur_words),
                        "text": text,
                    }
                )
            cur_words = []
            cur_speaker = None
            cur_start = None
            cur_end = None

        for word in words:
            token = self.get_word_text(word)
            if not token:
                continue
            speaker = word.get("speaker") or default_speaker or "UNKNOWN"
            start = float(word.get("start", 0))
            end = float(word.get("end", start))

            if cur_speaker is None:
                cur_speaker = speaker
                cur_start = start
                cur_end = end
                cur_words = [word]
                continue

            if speaker == cur_speaker:
                cur_words.append(word)
                cur_end = max(cur_end, end)
                continue

            flush()
            cur_speaker = speaker
            cur_start = start
            cur_end = end
            cur_words = [word]

        flush()
        return [run for run in runs if run["text"]]

    def should_keep_segment_whole(self, segment: dict, runs) -> tuple[bool, str | None]:
        if not runs:
            return False, None
        if len(runs) == 1:
            return False, None
        total_words = sum(len(run["words"]) for run in runs)
        if total_words <= 0:
            return False, None

        counts = {}
        for run in runs:
            counts[run["speaker"]] = counts.get(run["speaker"], 0) + len(run["words"])
        majority_speaker, majority_count = max(counts.items(), key=lambda item: item[1])
        majority_ratio = majority_count / total_words
        minority_runs = [run for run in runs if run["speaker"] != majority_speaker]

        segment_text = self.normalize_transcript_text((segment.get("text") or "").strip())
        reconstructed_text = self.normalize_transcript_text(" ".join(run["text"] for run in runs if run.get("text")))
        coverage_ratio = len(reconstructed_text) / max(len(segment_text), 1) if segment_text else 1.0

        if majority_ratio >= 0.7 and all(len(run["words"]) <= 2 for run in minority_runs):
            return True, majority_speaker

        if segment_text and len(segment_text.split()) <= 4 and majority_ratio >= 0.6:
            return True, majority_speaker

        if segment_text and coverage_ratio < 0.75 and majority_ratio >= 0.55:
            return True, majority_speaker

        if len(runs) >= 3 and majority_ratio >= 0.65:
            return True, majority_speaker

        return False, None

    def smooth_segment_speaker_turns(self, segments, max_duration: float = 1.2, max_words: int = 3):
        if len(segments) < 3:
            return segments

        smoothed = [dict(seg) for seg in segments]
        for idx in range(1, len(smoothed) - 1):
            prev_seg = smoothed[idx - 1]
            cur_seg = smoothed[idx]
            next_seg = smoothed[idx + 1]
            prev_spk = prev_seg.get("speaker")
            cur_spk = cur_seg.get("speaker")
            next_spk = next_seg.get("speaker")
            duration = float(cur_seg.get("end", 0)) - float(cur_seg.get("start", 0))
            word_count = len((cur_seg.get("text") or "").split())

            if (
                prev_spk
                and prev_spk == next_spk
                and cur_spk != prev_spk
                and duration <= max_duration
                and word_count <= max_words
            ):
                cur_seg["speaker"] = prev_spk

        return smoothed

    def merge_adjacent_segments(self, segments, max_gap: float = 0.35):
        if not segments:
            return []
        merged = [dict(segments[0])]
        for seg in segments[1:]:
            prev = merged[-1]
            same_speaker = prev.get("speaker") == seg.get("speaker")
            gap = float(seg.get("start", 0)) - float(prev.get("end", 0))
            if same_speaker and 0 <= gap <= max_gap:
                prev["end"] = max(float(prev.get("end", 0)), float(seg.get("end", 0)))
                prev_text = (prev.get("text") or "").strip()
                seg_text = (seg.get("text") or "").strip()
                prev["text"] = self.normalize_transcript_text(f"{prev_text} {seg_text}".strip())
            else:
                merged.append(dict(seg))
        return merged

    def finalize_result_segments(self, result: dict):
        segments = []
        for seg in result.get("segments", []):
            text = self.normalize_transcript_text((seg.get("text") or "").strip())
            if not text:
                continue
            normalized = dict(seg)
            normalized["text"] = text
            segments.append(normalized)
        segments = self.smooth_segment_speaker_turns(segments)
        segments = self.merge_adjacent_segments(segments)
        result["segments"] = segments
        result["text"] = " ".join(seg["text"] for seg in segments if seg.get("text"))
        return result

    def extract_word_segments(self, result: dict):
        if result.get("word_segments"):
            return result["word_segments"]
        words = []
        for seg in result.get("segments", []):
            for word in seg.get("words", []):
                if "start" in word and "end" in word:
                    words.append(word)
        return words

    def has_word_timestamps(self, result: dict) -> bool:
        return bool(self.extract_word_segments(result))

    def resegment_by_speaker(self, segments, word_segments):
        if not segments:
            return []
        if not word_segments:
            return segments

        out = []
        global_words = sorted(word_segments, key=lambda w: (w.get("start", 0), w.get("end", 0)))

        for seg in segments:
            seg_start = float(seg.get("start", 0))
            seg_end = float(seg.get("end", seg_start))
            seg_speaker = seg.get("speaker", "UNKNOWN")

            local_words = []
            for word in seg.get("words", []):
                if "start" in word and "end" in word:
                    local_words.append(dict(word))

            if not local_words:
                for word in global_words:
                    w_start = float(word.get("start", seg_start))
                    w_end = float(word.get("end", w_start))
                    mid = (w_start + w_end) / 2
                    if seg_start - 0.05 <= mid <= seg_end + 0.05:
                        local_words.append(dict(word))

            if not local_words:
                text = (seg.get("text") or "").strip()
                if text:
                    out.append(
                        {
                            "start": seg_start,
                            "end": seg_end,
                            "speaker": seg_speaker,
                            "text": text,
                        }
                    )
                continue

            local_words.sort(key=lambda w: (w.get("start", seg_start), w.get("end", seg_start)))
            local_words = self.smooth_word_speakers(local_words)
            local_words = self.collapse_short_speaker_runs(local_words)
            runs = self.segment_word_runs(local_words, seg_speaker)

            keep_whole, majority_speaker = self.should_keep_segment_whole(seg, runs)
            if keep_whole:
                text = self.normalize_transcript_text((seg.get("text") or self.join_words(local_words)).strip())
                if text:
                    out.append(
                        {
                            "start": seg_start,
                            "end": seg_end,
                            "speaker": majority_speaker or seg_speaker or "UNKNOWN",
                            "text": text,
                        }
                    )
                continue

            for run in runs:
                out.append(
                    {
                        "start": float(run["start"] if run["start"] else seg_start),
                        "end": float(run["end"] if run["end"] else seg_end),
                        "speaker": run["speaker"] or seg_speaker or "UNKNOWN",
                        "text": self.normalize_transcript_text(run["text"]),
                    }
                )

        out.sort(key=lambda item: (item.get("start", 0), item.get("end", 0)))
        return self.merge_adjacent_segments(out)

    def assign_speakers_by_overlap(self, segments, diar_segments):
        if not segments or not diar_segments:
            return segments

        diar = sorted(diar_segments, key=lambda d: (d["start"], d["end"]))

        def pick_speaker(start, end):
            best_spk = None
            best_overlap = 0.0
            for item in diar:
                overlap = min(end, item["end"]) - max(start, item["start"])
                if overlap > best_overlap:
                    best_overlap = overlap
                    best_spk = item.get("speaker", "UNKNOWN")
            if best_spk is None:
                mid = (start + end) / 2
                for item in diar:
                    if item["start"] <= mid <= item["end"]:
                        return item.get("speaker", "UNKNOWN")
            return best_spk or "UNKNOWN"

        assigned = []
        for seg in segments:
            new_seg = dict(seg)
            if not new_seg.get("speaker") or new_seg.get("speaker") == "UNKNOWN":
                new_seg["speaker"] = pick_speaker(float(seg.get("start", 0)), float(seg.get("end", 0)))
            assigned.append(new_seg)
        return assigned

    def apply_glossary_to_result(self, result: dict):
        rules = self.parse_glossary_rules()
        if not rules:
            return self.finalize_result_segments(result)

        total_replacements = 0
        for seg in result.get("segments", []):
            text = seg.get("text")
            if text:
                seg["text"], count = self.apply_glossary_to_text(text, rules)
                total_replacements += count

        for word in result.get("word_segments", []):
            key = "word" if "word" in word else "text" if "text" in word else None
            if key and word.get(key):
                word[key], count = self.apply_glossary_to_text(str(word[key]), rules)
                total_replacements += count

        if total_replacements:
            self.log(f"✅ Замен по словарю: {total_replacements}")
        return self.finalize_result_segments(result)

    def build_speaker_map(self, segments):
        speakers = []
        for seg in segments:
            speaker = seg.get("speaker", "UNKNOWN")
            if speaker not in speakers:
                speakers.append(speaker)
        speakers_sorted = sorted(speakers)
        mapped = {}
        for idx, speaker in enumerate(speakers_sorted, start=1):
            if re.fullmatch(r"SPEAKER_\d+|UNKNOWN", str(speaker)):
                mapped[speaker] = f"Спикер {idx}"
            else:
                mapped[speaker] = str(speaker)
        return mapped, speakers_sorted

    def render_result_text(self, result: dict) -> str:
        segments = result.get("segments", [])
        language = result.get("language", "unknown")
        speaker_map, speakers_sorted = self.build_speaker_map(segments)
        all_text = " ".join(seg["text"].strip() for seg in segments if seg.get("text"))

        lines = [f"Язык: {language} | Сегментов: {len(segments)} | Спикеров: {len(speakers_sorted)}"]
        if all_text:
            lines.extend(["=" * 70, "ВСЕ ВЫСКАЗЫВАНИЯ", "=" * 70, "", all_text, ""])

        lines.extend(["=" * 70, "СЕГМЕНТЫ ПО ВРЕМЕНИ", "=" * 70, ""])
        for seg in segments:
            start = float(seg.get("start", 0))
            end = float(seg.get("end", start))
            speaker = seg.get("speaker", "UNKNOWN")
            text = seg.get("text", "").strip() or "[без текста]"
            lines.append(f"[{start:07.2f} - {end:07.2f}] {speaker_map.get(speaker, speaker)}: {text}")

        lines.extend(["", "=" * 70, "РЕПЛИКИ ПО СПИКЕРАМ", "=" * 70, ""])
        for raw_speaker in speakers_sorted:
            speaker_name = speaker_map.get(raw_speaker, raw_speaker)
            speaker_segments = [seg for seg in segments if seg.get("speaker", "UNKNOWN") == raw_speaker]
            speaker_text = " ".join(seg["text"].strip() for seg in speaker_segments if seg.get("text"))
            lines.append(f"{speaker_name} ({raw_speaker})")
            lines.append("-" * 65)
            lines.append(f"Реплик: {len(speaker_segments)}")
            if speaker_text:
                lines.append(f"Текст: {speaker_text}")
            lines.append("")

        return "\n".join(lines).rstrip() + "\n"

    def wait_for_stable_file(self, path: Path) -> bool:
        previous_size = -1
        stable_count = 0
        while stable_count < self.config.stable_checks:
            if not path.exists():
                return False
            current_size = path.stat().st_size
            if current_size > 0 and current_size == previous_size:
                stable_count += 1
            else:
                stable_count = 0
                previous_size = current_size
            time.sleep(self.config.stable_interval_sec)
        return True

    def unique_destination(self, directory: Path, filename: str) -> Path:
        candidate = directory / filename
        if not candidate.exists():
            return candidate
        stem = candidate.stem
        suffix = candidate.suffix
        index = 1
        while True:
            next_candidate = directory / f"{stem}_{index}{suffix}"
            if not next_candidate.exists():
                return next_candidate
            index += 1

    def get_audio_duration_sec(self, path: Path) -> float:
        ffprobe_path = require_binary("ffprobe", extra_roots=[self.root_dir])
        cmd = [
            str(ffprobe_path),
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "default=noprint_wrappers=1:nokey=1",
            str(path),
        ]
        try:
            out = subprocess.check_output(cmd, stderr=subprocess.STDOUT, timeout=30)
            return float(out.decode("utf-8", errors="ignore").strip())
        except Exception:
            return 0.0

    def create_audio_excerpt(self, path: Path, *, start_sec: float, duration_sec: float, suffix: str) -> Path:
        clip_path = path.parent / f"{path.stem}{suffix}.wav"
        ffmpeg_path = self.ensure_ffmpeg()
        cmd = [
            str(ffmpeg_path),
            "-y",
            "-ss",
            f"{max(0.0, start_sec):.3f}",
            "-t",
            f"{max(1.0, duration_sec):.3f}",
            "-i",
            str(path),
            "-ac",
            "1",
            "-ar",
            "16000",
            str(clip_path),
        ]
        subprocess.run(cmd, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
        self.temp_files.append(clip_path)
        return clip_path

    def cleanup_temp_files(self):
        for temp_file in self.temp_files:
            try:
                if temp_file.exists():
                    temp_file.unlink()
            except Exception:
                pass
        self.temp_files.clear()

    def transcribe_audio(self, model, audio, batch_size: int) -> dict:
        try:
            return model.transcribe(audio, batch_size=batch_size, word_timestamps=True)
        except TypeError:
            self.log("⚠️ word_timestamps не поддерживается этой версией whisperx; продолжаем без него")
            return model.transcribe(audio, batch_size=batch_size)

    def run_asr_pass(
        self,
        audio_path: Path,
        profile: str = "asr_soft",
        *,
        vad_onset: float | None = None,
        chunk_size: int | None = None,
        initial_prompt: str | None = None,
        hotwords: str | None = None,
    ) -> dict:
        model = self.load_model_variant(
            vad_onset=vad_onset,
            chunk_size=chunk_size,
            initial_prompt=initial_prompt,
            hotwords=hotwords,
        )
        source_path = audio_path
        if self.config.preprocess_asr:
            source_path = self._preprocess_with_profile(
                audio_path,
                profile,
                f"FFmpeg profile for ASR: {profile}",
            )
        audio = whisperx.load_audio(str(source_path))
        result = self.transcribe_audio(model, audio, self.config.batch_size)
        return self.align_result(result, audio)

    def align_result(self, result: dict, audio) -> dict:
        language = result.get("language")
        if not language:
            return result

        self.log(f"🗣️ Язык: {language}")
        try:
            align_model, metadata = whisperx.load_align_model(
                language_code=language,
                device=self.config.device,
            )
            aligned = whisperx.align(
                result["segments"],
                align_model,
                metadata,
                audio,
                self.config.device,
                return_char_alignments=False,
            )
            if isinstance(aligned, dict) and aligned.get("segments"):
                result["segments"] = aligned["segments"]
                if "word_segments" in aligned:
                    result["word_segments"] = aligned["word_segments"]
            self.log(f"Word-level timestamps: {len(self.extract_word_segments(result))}")
            self.log("✅ Выравнивание выполнено")
        except Exception as e:
            self.log(f"⚠️ Выравнивание не удалось: {e}")
        return result

    def run_diarization(self, input_path: Path, profile: str | None = None):
        diarizer = self.get_diarizer()
        if profile:
            processed_audio_path = self._preprocess_with_profile(
                input_path,
                profile,
                f"FFmpeg profile for diarization: {profile}",
            )
        else:
            processed_audio_path = self.prepare_diarization_audio(input_path)
        diarize_df, speaker_embeddings = diarizer(
            str(processed_audio_path),
            min_speakers=self.config.min_speakers,
            max_speakers=self.config.max_speakers,
            return_embeddings=True,
        )
        segments = []
        for row in diarize_df.itertuples(index=False):
            segments.append(
                {
                    "start": float(row.start),
                    "end": float(row.end),
                    "speaker": str(row.speaker),
                }
            )
        segments.sort(key=lambda item: (item["start"], item["end"]))
        self.log(
            f"🔍 Сегментов диаризации: {len(segments)}, "
            f"уникальных спикеров: {len({item['speaker'] for item in segments})}"
        )
        return segments, speaker_embeddings or {}, diarize_df

    def apply_diarization(self, result: dict, diar_segments, diarize_df, speaker_embeddings: dict):
        diar_spk_count = len({item.get("speaker") for item in diar_segments if item.get("speaker")})
        if self.has_word_timestamps(result):
            if not diar_segments:
                self.log("WARN: diar_segments empty; пропускаем назначение спикеров")
                return result

            assigned_segments = result.get("segments", [])
            try:
                result = whisperx.assign_word_speakers(diarize_df, result, speaker_embeddings)
                assigned_segments = self.resegment_by_speaker(
                    result.get("segments", []),
                    self.extract_word_segments(result),
                )
                result["segments"] = assigned_segments
                unique = len({seg.get("speaker", "UNKNOWN") for seg in assigned_segments if seg.get("speaker")})
            except Exception as e:
                self.log(f"⚠️ assign_word_speakers не сработал: {e}")
                assigned_segments = self.assign_speakers_by_overlap(assigned_segments, diar_segments)
                result["segments"] = assigned_segments
                unique = len({seg.get("speaker", "UNKNOWN") for seg in assigned_segments if seg.get("speaker")})

            if unique <= 1 and diar_spk_count > 1:
                self.log("⚠️ После assign_word_speakers остался 1 спикер, пробуем фолбэк по перекрытию")
                result["segments"] = self.assign_speakers_by_overlap(assigned_segments, diar_segments)
        else:
            result["segments"] = self.assign_speakers_by_overlap(result.get("segments", []), diar_segments)
        return self.finalize_result_segments(result)

    def build_diarization_candidate(
        self,
        result: dict,
        audio_path: Path,
        *,
        profile_name: str,
        is_primary: bool,
    ) -> dict:
        diar_segments, speaker_embeddings, diarize_df = self.run_diarization(audio_path, profile=profile_name)
        candidate_result = self.apply_diarization(
            copy.deepcopy(result),
            diar_segments,
            diarize_df,
            speaker_embeddings,
        )
        candidate_result["segments"] = smooth_speaker_turns(candidate_result.get("segments", []))
        candidate_result = self.finalize_result_segments(candidate_result)
        score = score_diarization_result(
            candidate_result.get("segments", []),
            diar_segments,
            profile_name,
            self.config.min_speakers,
            self.config.max_speakers,
        )
        self.log(
            f"Diarization candidate scored: profile={profile_name}, score={score.score:.1f}, "
            f"speakers={score.assigned_speaker_count}, reasons={', '.join(score.reasons) or 'ok'}"
        )
        return {
            "profile": profile_name,
            "is_primary": is_primary,
            "result": candidate_result,
            "diar_segments": diar_segments,
            "speaker_embeddings": speaker_embeddings,
            "diarize_df": diarize_df,
            "score": score,
        }

    def process_file(self, audio_path: Path):
        timestamp = time.strftime("%Y%m%d_%H%M%S")
        result_paths = unique_result_paths(self.output_dir, audio_path, timestamp)
        json_path = result_paths["json"]
        txt_path = result_paths["txt"]
        quality_path = result_paths["quality"]
        journal = RunJournal(
            path=result_paths["journal"],
            input_path=str(audio_path),
            profile=self.config.processing_profile,
            model=self.config.model_name,
            backend="whisperx",
            device=self.config.device,
        )
        journal.write()
        current_stage = RunStage.QUEUED
        output_paths: dict[str, str] = {}
        profile = get_processing_profile(self.config.processing_profile)

        self.temp_files.clear()
        self.log(f"Queued: {audio_path.name} (profile={profile.key})")
        try:
            current_stage = RunStage.FFMPEG
            journal.update(stage=current_stage, status=RunStatus.RUNNING)
            audio_duration = self.get_audio_duration_sec(audio_path)
            journal.update(audio_duration=audio_duration)
            if audio_duration > 0:
                self.log(f"Audio duration: {audio_duration / 60.0:.1f} min")
            if self.config.hotwords:
                hotword_count = len([line for line in self.config.hotwords.splitlines() if line.strip()])
                self.log(f"Hotwords loaded: {hotword_count} item(s)")

            current_stage = RunStage.ASR
            journal.update(stage=current_stage)
            result = self.run_asr_pass(audio_path, profile=profile.asr_profile)
            primary_stats = transcript_health(result, audio_duration)
            self.log(
                "Primary ASR coverage: "
                f"segments={int(primary_stats['segment_count'])}, "
                f"start_gap={primary_stats['leading_gap'] / 60.0:.1f} min, "
                f"last_end={primary_stats['coverage_end'] / 60.0:.1f} min, "
                f"largest_gap={primary_stats['largest_gap'] / 60.0:.1f} min"
            )

            if should_retry_transcription(primary_stats):
                self.log("Large timing gap detected; retrying ASR with a softer VAD profile")
                retry_result = self.run_asr_pass(
                    audio_path,
                    profile=profile.asr_profile,
                    vad_onset=min(self.config.vad_onset, 0.28),
                    chunk_size=max(self.config.chunk_size, 30),
                )
                retry_stats = transcript_health(retry_result, audio_duration)
                self.log(
                    "Fallback ASR coverage: "
                    f"segments={int(retry_stats['segment_count'])}, "
                    f"start_gap={retry_stats['leading_gap'] / 60.0:.1f} min, "
                    f"last_end={retry_stats['coverage_end'] / 60.0:.1f} min, "
                    f"largest_gap={retry_stats['largest_gap'] / 60.0:.1f} min"
                )
                if is_retry_result_better(primary_stats, retry_stats):
                    self.log("Using fallback ASR result because it covers the recording better")
                    result = retry_result
                    primary_stats = retry_stats
                else:
                    self.log("Keeping primary ASR result; fallback did not improve coverage")

            if primary_stats.get("leading_gap", 0.0) >= 45.0:
                rescue_path = self.create_audio_excerpt(
                    audio_path,
                    start_sec=0.0,
                    duration_sec=min(max(primary_stats["leading_gap"] + 90.0, 180.0), 8 * 60.0),
                    suffix=".head_rescue",
                )
                self.log("Attempting dedicated head-rescue pass for the beginning of the recording")
                rescue_result = self.run_asr_pass(
                    rescue_path,
                    profile=profile.asr_profile,
                    vad_onset=min(self.config.vad_onset, 0.18),
                    chunk_size=max(self.config.chunk_size, 30),
                )
                merged_result = merge_asr_results(rescue_result, result)
                merged_stats = transcript_health(merged_result, audio_duration)
                self.log(
                    "Head-rescue ASR coverage: "
                    f"segments={int(merged_stats['segment_count'])}, "
                    f"start_gap={merged_stats['leading_gap'] / 60.0:.1f} min, "
                    f"last_end={merged_stats['coverage_end'] / 60.0:.1f} min, "
                    f"largest_gap={merged_stats['largest_gap'] / 60.0:.1f} min"
                )
                if is_retry_result_better(primary_stats, merged_stats):
                    self.log("Using head-rescue merge because it recovered more of the beginning")
                    result = merged_result
                    primary_stats = merged_stats
                else:
                    self.log("Keeping previous ASR result; head-rescue merge did not improve coverage")

            current_stage = RunStage.DIARIZATION
            journal.update(stage=current_stage)
            diarization_candidates: list[dict] = []
            failed_diarization_candidates: list[dict] = []
            for candidate_profile in diarization_profiles_for_processing_profile(
                self.config.processing_profile,
                profile.diarization_profile,
            ):
                try:
                    diarization_candidates.append(
                        self.build_diarization_candidate(
                            result,
                            audio_path,
                            profile_name=candidate_profile,
                            is_primary=candidate_profile == profile.diarization_profile,
                        )
                    )
                except Exception as exc:
                    failed_diarization_candidates.append({"profile": candidate_profile, "error": str(exc)})
                    self.log(f"Diarization candidate failed: profile={candidate_profile}, error={exc}")

            if not diarization_candidates:
                errors = "; ".join(
                    f"{item['profile']}: {item['error']}" for item in failed_diarization_candidates
                )
                raise RuntimeError(f"All diarization candidates failed: {errors}")

            best_diarization = choose_best_diarization_candidate(diarization_candidates)
            best_diarization_score = best_diarization["score"]
            result = best_diarization["result"]
            result["_diarization"] = {
                "profile": best_diarization_score.profile,
                "score": best_diarization_score.score,
                "score_scope": "pre_reference_filter",
                "reasons": best_diarization_score.reasons,
                "candidates": [candidate["score"].to_dict() for candidate in diarization_candidates]
                + [
                    {"profile": item["profile"], "error": item["error"], "failed": True}
                    for item in failed_diarization_candidates
                ],
            }
            self.log(
                f"Selected diarization profile: {best_diarization_score.profile} "
                f"(score={best_diarization_score.score:.1f}, reasons={', '.join(best_diarization_score.reasons) or 'ok'})"
            )

            current_stage = RunStage.POSTPROCESS
            journal.update(stage=current_stage)
            result = self.apply_glossary_to_result(result)

            current_stage = RunStage.SAVE
            journal.update(stage=current_stage)
            atomic_write_json(json_path, result)
            atomic_write_text(txt_path, self.render_result_text(result))
            output_paths.update({"json": str(json_path), "txt": str(txt_path), "quality": str(quality_path)})

            processed_target = self.unique_destination(self.processed_dir, audio_path.name)
            shutil.move(str(audio_path), str(processed_target))
            output_paths["processed_audio"] = str(processed_target)

            quality = quality_from_result(result, audio_duration)
            atomic_write_text(
                quality_path,
                format_quality_report(quality, input_path=str(audio_path), output_paths=output_paths),
            )
            journal.finish(
                output_paths=output_paths,
                quality=quality,
            )

            self.log(f"Done JSON: {json_path.name}")
            self.log(f"Done TXT: {txt_path.name}")
            self.log(f"Done quality report: {quality_path.name}")
            self.log(f"Source file moved to: {processed_target}")
        except Exception as e:
            self.log(f"Processing failed for {audio_path.name}: {e}")
            if audio_path.exists():
                failed_target = self.unique_destination(self.failed_dir, audio_path.name)
                shutil.move(str(audio_path), str(failed_target))
                output_paths["failed_audio"] = str(failed_target)
                self.log(f"File moved to failed_audio: {failed_target}")
            journal.fail(error=str(e), stage=current_stage, output_paths=output_paths)
        finally:
            self.cleanup_temp_files()

    def iter_pending_files(self):
        files = []
        for path in self.input_dir.iterdir():
            if not path.is_file():
                continue
            if path.suffix.lower() not in AUDIO_EXTENSIONS:
                continue
            lower_name = path.name.lower()
            lower_stem = path.stem.lower()
            if re.search(r"\.(?:asr|diar|diarized)(?:[._-]|$)", lower_stem) or lower_name.endswith(
                (".asr.wav", ".asr_soft.wav", ".diar.wav", ".diarized.wav", ".diar_soft.wav")
            ):
                continue
            files.append(path)
        return sorted(files, key=lambda item: item.stat().st_mtime)

    def run_forever(self):
        ffmpeg_path = self.ensure_ffmpeg()
        self.log(f"FFmpeg: {ffmpeg_path}")
        self.log(f"📂 Папка входа: {self.input_dir}")
        self.log(f"📁 Папка результата: {self.output_dir}")
        self.log("👀 Ожидание новых аудиофайлов...")
        try:
            while True:
                pending_files = self.iter_pending_files()
                if not pending_files:
                    time.sleep(self.config.poll_interval_sec)
                    continue

                for audio_path in pending_files:
                    if not self.wait_for_stable_file(audio_path):
                        continue
                    self.process_file(audio_path)
        except KeyboardInterrupt:
            self.log("⏹️ Остановлено пользователем")


def main():
    root_dir = Path(__file__).resolve().parent
    watcher = AutoTranscribeWatcher(root_dir)
    watcher.run_forever()


if __name__ == "__main__":
    main()
