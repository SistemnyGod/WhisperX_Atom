from __future__ import annotations

import json
import re
import shutil
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import torch
import whisperx
from whisperx.diarize import DiarizationPipeline as WhisperXDiarizationPipeline


AUDIO_EXTENSIONS = {".wav", ".mp3", ".m4a", ".ogg", ".flac"}
INPUT_DIR_NAME = "ЗДЕСЬ СКИДЫВАЕМ ФАЙЛ ДЛЯ ТРАНСКРИБАЦИИ"
OUTPUT_DIR_NAME = "ЗДЕСЬ ПОЛУЧАЕМ РЕЗУЛЬТАТ ТРАНСКРИБАЦИИ"


@dataclass
class WatchConfig:
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
    glossary_replacements: str
    poll_interval_sec: int = 5
    stable_checks: int = 3
    stable_interval_sec: int = 2

    @classmethod
    def from_settings(cls, settings: dict) -> "WatchConfig":
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
            model_name=settings.get("last_model") or settings.get("model") or "large-v3",
            language=language,
            device=device,
            compute_type=compute_type,
            batch_size=int(settings.get("batch_size") or settings.get("batch") or 8),
            min_speakers=min_speakers,
            max_speakers=max_speakers,
            beam_size=int(settings.get("beam_size") or 7),
            vad_onset=float(settings.get("vad_onset") or 0.40),
            chunk_size=int(settings.get("chunk_size") or 20),
            preprocess_asr=bool(settings.get("preprocess_asr", True)),
            initial_prompt=(settings.get("initial_prompt") or "").strip() or None,
            hotwords=(settings.get("hotwords") or "").strip() or None,
            hf_token=(settings.get("hf_token") or settings.get("hftoken") or "").strip(),
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
        self.model = None
        self.diarizer = None

    def log(self, message: str):
        timestamp = time.strftime("%H:%M:%S")
        print(f"[{timestamp}] {message}", flush=True)

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

    def get_diarizer(self):
        if self.diarizer is not None:
            return self.diarizer
        if not self.config.hf_token:
            raise RuntimeError("В настройках отсутствует HF token для диаризации")

        self.log("📥 Загрузка diarization pipeline...")
        self.diarizer = WhisperXDiarizationPipeline(
            use_auth_token=self.config.hf_token,
            device=self.config.device,
        )
        self.log("✅ Diarization pipeline загружен")
        return self.diarizer

    def preprocess_audio_for_asr_soft(self, input_path: Path) -> Path:
        output_path = input_path.parent / f"{input_path.stem}.asr_soft.wav"
        command = [
            "ffmpeg",
            "-y",
            "-i",
            str(input_path),
            "-ac",
            "1",
            "-ar",
            "16000",
            "-af",
            (
                "highpass=f=70,"
                "lowpass=f=7600,"
                "afftdn=nf=-20,"
                "acompressor=threshold=-22dB:ratio=2:attack=5:release=60"
            ),
            str(output_path),
        ]
        try:
            self.log("🎙️ FFmpeg: мягкая предобработка аудио для ASR...")
            subprocess.run(command, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
            self.temp_files.append(output_path)
            return output_path
        except subprocess.CalledProcessError as e:
            details = (e.stderr or b"").decode("utf-8", errors="ignore").strip()
            self.log(f"⚠️ ASR FFmpeg не сработал: {details or e}")
            return input_path
        except Exception as e:
            self.log(f"⚠️ ASR предобработка пропущена: {e}")
            return input_path

    def preprocess_audio_for_diarization(self, input_path: Path) -> Path:
        output_path = input_path.parent / f"{input_path.stem}.diarized.wav"
        command = [
            "ffmpeg",
            "-y",
            "-i",
            str(input_path),
            "-ac",
            "1",
            "-ar",
            "16000",
            "-af",
            (
                "highpass=f=120,"
                "lowpass=f=7500,"
                "afftdn=nf=-25,"
                "acompressor=threshold=-28dB:ratio=4:attack=5:release=80,"
                "alimiter=limit=-1dB"
            ),
            str(output_path),
        ]
        try:
            self.log("🎧 FFmpeg: предварительная обработка аудио для диаризации...")
            subprocess.run(command, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
            self.temp_files.append(output_path)
            return output_path
        except Exception as e:
            self.log(f"⚠️ Диаризационная предобработка не сработала: {e}")
            return input_path

    def preprocess_audio_for_diarization_soft(self, input_path: Path) -> Path:
        output_path = input_path.parent / f"{input_path.stem}.diar_soft.wav"
        command = [
            "ffmpeg",
            "-y",
            "-i",
            str(input_path),
            "-ac",
            "1",
            "-ar",
            "16000",
            "-af",
            "highpass=f=90,lowpass=f=8000,afftdn=nf=-20",
            str(output_path),
        ]
        try:
            self.log("🎧 FFmpeg: мягкая очистка аудио для диаризации...")
            subprocess.run(command, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
            self.temp_files.append(output_path)
            return output_path
        except Exception as e:
            self.log(f"⚠️ diar_soft FFmpeg не сработал: {e}")
            return input_path

    def prepare_diarization_audio(self, input_path: Path) -> Path:
        processed = self.preprocess_audio_for_diarization(input_path)
        if processed == input_path:
            processed = self.preprocess_audio_for_diarization_soft(input_path)
        return processed

    def parse_glossary_rules(self):
        rules = []
        raw_text = self.config.glossary_replacements
        if not raw_text:
            return rules
        for idx, raw_line in enumerate(raw_text.splitlines(), start=1):
            line = raw_line.strip()
            if not line or line.startswith("#") or "=>" not in line:
                continue
            source, target = [part.strip() for part in line.split("=>", 1)]
            if not source:
                continue
            pattern = source[3:].strip() if source.startswith("re:") else re.escape(source)
            try:
                rules.append((re.compile(pattern, flags=re.IGNORECASE), target))
            except re.error as e:
                self.log(f"⚠️ Ошибка regex в glossary строке {idx}: {e}")
        return rules

    def normalize_transcript_text(self, text: str) -> str:
        text = re.sub(r"\s+([,.:;!?])", r"\1", text)
        text = re.sub(r"\s{2,}", " ", text)
        return text.strip()

    def apply_glossary_to_text(self, text: str, rules):
        updated = text
        replacements = 0
        for pattern, target in rules:
            updated, count = pattern.subn(target, updated)
            replacements += count
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

    def run_diarization(self, input_path: Path):
        diarizer = self.get_diarizer()
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

    def process_file(self, audio_path: Path):
        self.temp_files.clear()
        self.log(f"🎯 Обработка файла: {audio_path.name}")
        try:
            model = self.load_model()

            asr_source = audio_path
            if self.config.preprocess_asr:
                asr_source = self.preprocess_audio_for_asr_soft(audio_path)

            audio = whisperx.load_audio(str(asr_source))
            result = self.transcribe_audio(model, audio, self.config.batch_size)
            result = self.align_result(result, audio)

            diar_segments, speaker_embeddings, diarize_df = self.run_diarization(audio_path)
            result = self.apply_diarization(result, diar_segments, diarize_df, speaker_embeddings)
            result = self.apply_glossary_to_result(result)

            timestamp = time.strftime("%Y%m%d_%H%M%S")
            base_name = audio_path.stem
            json_path = self.output_dir / f"{base_name}_{timestamp}.json"
            txt_path = self.output_dir / f"{base_name}_{timestamp}.txt"
            json_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
            txt_path.write_text(self.render_result_text(result), encoding="utf-8")

            processed_target = self.unique_destination(self.processed_dir, audio_path.name)
            shutil.move(str(audio_path), str(processed_target))

            self.log(f"✅ JSON: {json_path.name}")
            self.log(f"✅ TXT: {txt_path.name}")
            self.log(f"📦 Исходный файл перемещён в: {processed_target}")
        except Exception as e:
            self.log(f"❌ Ошибка обработки {audio_path.name}: {e}")
            if audio_path.exists():
                failed_target = self.unique_destination(self.failed_dir, audio_path.name)
                shutil.move(str(audio_path), str(failed_target))
                self.log(f"⚠️ Файл перемещён в failed_audio: {failed_target}")
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
            if lower_name.endswith(".asr_soft.wav") or lower_name.endswith(".diarized.wav") or lower_name.endswith(".diar_soft.wav"):
                continue
            files.append(path)
        return sorted(files, key=lambda item: item.stat().st_mtime)

    def run_forever(self):
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
