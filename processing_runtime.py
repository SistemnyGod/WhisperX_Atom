from __future__ import annotations

import json
import re
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

from local_io import atomic_write_json, atomic_write_text


AUDIO_EXTENSIONS = {".wav", ".mp3", ".m4a", ".ogg", ".flac"}
VIDEO_EXTENSIONS = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".mpeg", ".mpg", ".3gp"}
MEDIA_EXTENSIONS = AUDIO_EXTENSIONS | VIDEO_EXTENSIONS


class RunStage:
    QUEUED = "queued"
    FFMPEG = "ffmpeg"
    ASR = "asr"
    ALIGNMENT = "alignment"
    DIARIZATION = "diarization"
    POSTPROCESS = "postprocess"
    SAVE = "save"
    DONE = "done"
    ERROR = "error"


class RunStatus:
    QUEUED = "queued"
    RUNNING = "running"
    DONE = "done"
    FAILED = "failed"


STAGE_LABELS_RU = {
    RunStage.QUEUED: "Ожидает",
    RunStage.FFMPEG: "FFmpeg",
    RunStage.ASR: "ASR",
    RunStage.ALIGNMENT: "Alignment",
    RunStage.DIARIZATION: "Diarization",
    RunStage.POSTPROCESS: "Postprocess",
    RunStage.SAVE: "Save",
    RunStage.DONE: "Done",
    RunStage.ERROR: "Error",
}


@dataclass(frozen=True)
class ProcessingProfile:
    key: str
    label: str
    model: str
    vad_onset: float
    chunk_size: int
    batch_size: int
    beam_size: int
    preprocess_asr: bool
    asr_profile: str
    diarization_profile: str
    retry_policy: str
    max_auto_model: str


PROFILES: dict[str, ProcessingProfile] = {
    "fast": ProcessingProfile(
        key="fast",
        label="Быстро",
        model="medium",
        vad_onset=0.40,
        chunk_size=20,
        batch_size=16,
        beam_size=5,
        preprocess_asr=True,
        asr_profile="asr_soft",
        diarization_profile="diar",
        retry_policy="basic",
        max_auto_model="medium",
    ),
    "accurate": ProcessingProfile(
        key="accurate",
        label="Точно",
        model="large-v3",
        vad_onset=0.35,
        chunk_size=30,
        batch_size=8,
        beam_size=10,
        preprocess_asr=True,
        asr_profile="asr_soft",
        diarization_profile="diar_soft",
        retry_policy="coverage",
        max_auto_model="large-v3",
    ),
    "meeting": ProcessingProfile(
        key="meeting",
        label="Сложное совещание",
        model="large-v3",
        vad_onset=0.40,
        chunk_size=20,
        batch_size=8,
        beam_size=7,
        preprocess_asr=True,
        asr_profile="asr_soft",
        diarization_profile="diar",
        retry_policy="coverage",
        max_auto_model="large-v3",
    ),
    "noisy": ProcessingProfile(
        key="noisy",
        label="Плохой звук",
        model="large-v3",
        vad_onset=0.28,
        chunk_size=30,
        batch_size=4,
        beam_size=7,
        preprocess_asr=True,
        asr_profile="asr_soft",
        diarization_profile="diar_soft",
        retry_policy="coverage",
        max_auto_model="large-v3",
    ),
}

PROFILE_LABELS = [profile.label for profile in PROFILES.values()]
_PROFILE_BY_LABEL = {profile.label: profile for profile in PROFILES.values()}


def now_iso() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S%z")


def get_processing_profile(value: str | None) -> ProcessingProfile:
    if not value:
        return PROFILES["meeting"]
    text = str(value).strip()
    return PROFILES.get(text) or _PROFILE_BY_LABEL.get(text) or PROFILES["meeting"]


def profile_to_settings(profile: ProcessingProfile) -> dict[str, Any]:
    return {
        "model": profile.model,
        "vad_onset": profile.vad_onset,
        "chunk_size": profile.chunk_size,
        "batch_size": profile.batch_size,
        "beam_size": profile.beam_size,
        "preprocess_asr": profile.preprocess_asr,
        "asr_profile": profile.asr_profile,
        "diarization_profile": profile.diarization_profile,
        "retry_policy": profile.retry_policy,
        "max_auto_model": profile.max_auto_model,
    }


def bounded_auto_model(model_name: str, profile: ProcessingProfile) -> str:
    if profile.max_auto_model == "medium" and model_name in {"large-v2", "large-v3"}:
        return "medium"
    return model_name


def run_journal_path(output_dir: Path, input_path: Path, timestamp: str) -> Path:
    return output_dir / f"{input_path.stem}_{timestamp}_run.json"


def journal_path_for_result(result_path: Path) -> Path:
    return result_path.with_name(f"{result_path.stem}_run.json")


def unique_result_paths(output_dir: Path, input_path: Path | str, timestamp: str | None = None) -> dict[str, Path]:
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    stem = Path(input_path).stem if not isinstance(input_path, Path) else input_path.stem
    timestamp = timestamp or time.strftime("%Y%m%d_%H%M%S")

    for index in range(1, 10000):
        suffix = f"{stem}_{timestamp}" if index == 1 else f"{stem}_{timestamp}_{index:02d}"
        json_path = output_dir / f"{suffix}.json"
        txt_path = output_dir / f"{suffix}.txt"
        quality_path = output_dir / f"{suffix}_quality.txt"
        journal_path = output_dir / f"{suffix}_run.json"
        if (
            not json_path.exists()
            and not txt_path.exists()
            and not quality_path.exists()
            and not journal_path.exists()
        ):
            return {"json": json_path, "txt": txt_path, "quality": quality_path, "journal": journal_path}

    raise RuntimeError(f"Could not allocate result path for {stem}_{timestamp}")


def ensure_text_file(path: Path) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        atomic_write_text(path, "")
    return path


def summarize_result_quality(result: dict[str, Any], audio_duration: float = 0.0) -> dict[str, Any]:
    segments = result.get("segments") or []
    segment_count = len(segments)
    coverage_end = 0.0
    largest_gap = 0.0
    previous_end = 0.0
    speakers = set()

    for segment in segments:
        try:
            start = float(segment.get("start") or 0.0)
            end = float(segment.get("end") or start)
        except (TypeError, ValueError):
            continue
        if start > previous_end:
            largest_gap = max(largest_gap, start - previous_end)
        previous_end = max(previous_end, end)
        coverage_end = max(coverage_end, end)
        speaker = segment.get("speaker")
        if speaker:
            speakers.add(str(speaker))

    return {
        "audio_duration": float(audio_duration or 0.0),
        "segment_count": segment_count,
        "largest_gap": largest_gap,
        "coverage_end": coverage_end,
        "speaker_count": len(speakers),
    }


def quality_from_result(result: dict[str, Any], audio_duration: float = 0.0) -> dict[str, Any]:
    quality = summarize_result_quality(result, audio_duration)
    diarization_meta = result.get("_diarization") or {}
    if diarization_meta:
        quality.update(
            {
                "diarization_profile": diarization_meta.get("profile") or "",
                "diarization_score": diarization_meta.get("score") or 0.0,
                "diarization_score_scope": diarization_meta.get("score_scope") or "",
                "diarization_reasons": diarization_meta.get("reasons") or [],
                "diarization_candidates": diarization_meta.get("candidates") or [],
            }
        )
    return quality


def format_quality_report(quality: dict[str, Any], *, input_path: str = "", output_paths: dict[str, str] | None = None) -> str:
    output_paths = output_paths or {}
    duration = float(quality.get("audio_duration") or 0.0)
    largest_gap = float(quality.get("largest_gap") or 0.0)
    coverage_end = float(quality.get("coverage_end") or 0.0)
    reasons = quality.get("diarization_reasons") or []
    candidates = quality.get("diarization_candidates") or []

    lines = [
        "Quality report",
        "=" * 70,
        f"Input: {input_path or '-'}",
        f"Processed audio: {output_paths.get('processed_audio') or '-'}",
        f"Failed audio: {output_paths.get('failed_audio') or '-'}",
        f"JSON: {output_paths.get('json') or '-'}",
        f"TXT: {output_paths.get('txt') or '-'}",
        "",
        f"Segments: {int(quality.get('segment_count') or 0)}",
        f"Audio duration: {duration:.2f} sec ({duration / 60.0:.2f} min)",
        f"Largest gap: {largest_gap:.2f} sec",
        f"Last covered point: {coverage_end:.2f} sec",
        f"Speakers: {int(quality.get('speaker_count') or 0)}",
        "",
        f"Diarization profile: {quality.get('diarization_profile') or '-'}",
        f"Diarization score: {float(quality.get('diarization_score') or 0.0):.1f}",
        f"Diarization score scope: {quality.get('diarization_score_scope') or '-'}",
        f"Diarization reasons: {', '.join(map(str, reasons)) if reasons else '-'}",
    ]
    if candidates:
        lines.extend(["", "Diarization candidates:"])
        for candidate in candidates:
            if not isinstance(candidate, dict):
                continue
            profile = candidate.get("profile") or "-"
            if candidate.get("failed"):
                lines.append(f"- {profile}: failed, error={candidate.get('error') or '-'}")
            else:
                score = candidate.get("score")
                speaker_count = candidate.get("speaker_count")
                unknown_ratio = candidate.get("unknown_ratio")
                lines.append(
                    f"- {profile}: score={float(score or 0.0):.1f}, "
                    f"speakers={speaker_count if speaker_count is not None else '-'}, "
                    f"unknown_ratio={float(unknown_ratio or 0.0):.3f}"
                )
    return "\n".join(lines).rstrip() + "\n"


def retry_source_from_history_item(item: dict[str, Any], audio_extensions: set[str] | tuple[str, ...]) -> Path | None:
    output_paths = item.get("output_paths") or {}
    candidates = [
        item.get("input_path"),
        output_paths.get("failed_audio"),
        output_paths.get("processed_audio"),
    ]
    allowed = {suffix.lower() for suffix in audio_extensions}
    for candidate in candidates:
        if not candidate:
            continue
        path = Path(str(candidate))
        if path.exists() and path.is_file() and path.suffix.lower() in allowed:
            return path
    return None


def history_item_artifact_paths(item: dict[str, Any], *, include_audio: bool = False) -> list[Path]:
    output_paths = item.get("output_paths") or {}
    keys = ["json", "txt", "quality"]
    if include_audio:
        keys.extend(["processed_audio", "failed_audio"])

    paths: list[Path] = []
    for key in keys:
        value = output_paths.get(key)
        if value:
            paths.append(Path(str(value)))
    journal_path = item.get("_path") or item.get("path")
    if journal_path:
        paths.append(Path(str(journal_path)))

    seen: set[str] = set()
    unique_paths: list[Path] = []
    for path in paths:
        key = str(path)
        if key not in seen:
            seen.add(key)
            unique_paths.append(path)
    return unique_paths


def delete_history_item(item: dict[str, Any], *, include_audio: bool = False) -> list[str]:
    deleted: list[str] = []
    for path in history_item_artifact_paths(item, include_audio=include_audio):
        try:
            if path.exists() and path.is_file():
                path.unlink()
                deleted.append(str(path))
        except OSError:
            continue
    return deleted


def history_item_day(item: dict[str, Any]) -> str:
    timestamp = str(item.get("finished_at") or item.get("started_at") or "").strip()
    if len(timestamp) >= 10 and timestamp[4:5] == "-" and timestamp[7:8] == "-":
        return timestamp[:10]
    journal_path = item.get("_path") or item.get("path")
    if journal_path:
        try:
            return time.strftime("%Y-%m-%d", time.localtime(Path(str(journal_path)).stat().st_mtime))
        except OSError:
            pass
    return "unknown-date"


def history_item_display_name(item: dict[str, Any]) -> str:
    input_path = item.get("input_path")
    if input_path:
        stem = Path(str(input_path)).stem
        if stem:
            return stem
    output_paths = item.get("output_paths") or {}
    for key in ("txt", "json", "failed_audio", "processed_audio"):
        value = output_paths.get(key)
        if value:
            stem = Path(str(value)).stem
            if stem:
                return stem
    journal_path = item.get("_path") or item.get("path")
    if journal_path:
        return Path(str(journal_path)).stem.replace("_run", "")
    return "transcript"


def group_history_by_day(rows: list[dict[str, Any]]) -> dict[str, list[dict[str, Any]]]:
    groups: dict[str, list[dict[str, Any]]] = {}
    for item in rows:
        groups.setdefault(history_item_day(item), []).append(item)
    return dict(sorted(groups.items(), key=lambda pair: pair[0], reverse=True))


def format_history_browser_entry(item: dict[str, Any]) -> str:
    output_paths = item.get("output_paths") or {}
    txt_state = "TXT" if output_paths.get("txt") else "TXT из JSON/журнала"
    finished = str(item.get("finished_at") or item.get("started_at") or "-")
    time_part = finished[11:19] if len(finished) >= 19 else finished
    return (
        f"{history_item_display_name(item)} | "
        f"{time_part} | "
        f"{item.get('status') or '-'} | "
        f"{item.get('profile') or '-'} | "
        f"спикеров={int(item.get('speaker_count') or 0)} | "
        f"{txt_state}"
    )


def _safe_export_stem(name: str) -> str:
    cleaned = re.sub(r'[<>:"/\\|?*\x00-\x1f]+', "_", name).strip(" ._")
    return cleaned or "transcript"


def _segments_to_text(result: dict[str, Any]) -> str:
    lines: list[str] = []
    for segment in result.get("segments") or []:
        if not isinstance(segment, dict):
            continue
        text = str(segment.get("text") or "").strip()
        if not text:
            continue
        speaker = str(segment.get("speaker") or "").strip()
        if speaker:
            lines.append(f"{speaker}: {text}")
        else:
            lines.append(text)
    if lines:
        return "\n".join(lines).rstrip() + "\n"
    text = str(result.get("text") or "").strip()
    return (text + "\n") if text else ""


def collect_speaker_stats(result: dict[str, Any]) -> list[dict[str, Any]]:
    stats: dict[str, dict[str, Any]] = {}
    order: list[str] = []
    for segment in result.get("segments") or []:
        if not isinstance(segment, dict):
            continue
        speaker = str(segment.get("speaker") or "UNKNOWN")
        if speaker not in stats:
            stats[speaker] = {"speaker": speaker, "segment_count": 0, "duration": 0.0, "word_count": 0}
            order.append(speaker)
        item = stats[speaker]
        item["segment_count"] += 1
        try:
            start = float(segment.get("start") or 0.0)
            end = float(segment.get("end") or start)
            item["duration"] += max(0.0, end - start)
        except (TypeError, ValueError):
            pass
        text = str(segment.get("text") or "")
        item["word_count"] += len([part for part in text.split() if part.strip()])
    return [stats[speaker] for speaker in order]


def format_speaker_mapping_template(result: dict[str, Any]) -> str:
    lines = [
        "# Формат: старое имя = новое имя",
        "# Чтобы объединить спикеров, укажите одинаковое новое имя.",
        "",
    ]
    stats = collect_speaker_stats(result)
    if not stats:
        lines.append("UNKNOWN = UNKNOWN")
    for item in stats:
        speaker = item["speaker"]
        lines.append(
            f"{speaker} = {speaker}    # реплик={item['segment_count']}, "
            f"длительность={float(item['duration']):.1f} сек"
        )
    return "\n".join(lines).rstrip() + "\n"


def parse_speaker_mapping_text(text: str) -> dict[str, str]:
    mapping: dict[str, str] = {}
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        line = line.split("#", 1)[0].strip()
        if "=>" in line:
            left, right = line.split("=>", 1)
        elif "->" in line:
            left, right = line.split("->", 1)
        elif "=" in line:
            left, right = line.split("=", 1)
        else:
            continue
        source = left.strip()
        target = right.strip()
        if source and target and source != target:
            mapping[source] = target
    return mapping


def apply_speaker_mapping_to_result(result: dict[str, Any], mapping: dict[str, str]) -> dict[str, Any]:
    updated = dict(result)
    cleaned_mapping = {str(source): str(target) for source, target in mapping.items() if str(source) and str(target)}
    if not cleaned_mapping:
        return updated

    def mapped(value: Any) -> str:
        speaker = str(value or "UNKNOWN")
        return cleaned_mapping.get(speaker, speaker)

    new_segments: list[dict[str, Any]] = []
    for segment in result.get("segments") or []:
        if not isinstance(segment, dict):
            continue
        item = dict(segment)
        original_speaker = str(item.get("speaker") or "UNKNOWN")
        new_speaker = mapped(original_speaker)
        if new_speaker != original_speaker:
            item["speaker_original"] = item.get("speaker_original") or original_speaker
            item["speaker"] = new_speaker
        words = []
        for word in item.get("words") or []:
            if not isinstance(word, dict):
                continue
            word_item = dict(word)
            if "speaker" in word_item:
                word_item["speaker"] = mapped(word_item.get("speaker"))
            words.append(word_item)
        if words:
            item["words"] = words
        new_segments.append(item)
    updated["segments"] = new_segments

    if result.get("word_segments"):
        new_words = []
        for word in result.get("word_segments") or []:
            if not isinstance(word, dict):
                continue
            item = dict(word)
            if "speaker" in item:
                item["speaker"] = mapped(item.get("speaker"))
            new_words.append(item)
        updated["word_segments"] = new_words

    updated["_speaker_mapping"] = cleaned_mapping
    if isinstance(updated.get("_quality_summary"), dict):
        quality = dict(updated["_quality_summary"])
        quality["speaker_count"] = len({seg.get("speaker") for seg in new_segments if seg.get("speaker")})
        updated["_quality_summary"] = quality
    return updated


def render_result_text_for_export(result: dict[str, Any]) -> str:
    segments = result.get("segments") or []
    if not segments:
        return _segments_to_text(result)
    lines: list[str] = []
    for segment in segments:
        if not isinstance(segment, dict):
            continue
        text = str(segment.get("text") or "").strip()
        if not text:
            continue
        speaker = str(segment.get("speaker") or "UNKNOWN")
        try:
            start = float(segment.get("start") or 0.0)
            end = float(segment.get("end") or start)
            prefix = f"[{start:07.2f} - {end:07.2f}]"
        except (TypeError, ValueError):
            prefix = "[-]"
        lines.append(f"{prefix} {speaker}: {text}")
    return "\n".join(lines).rstrip() + "\n"


def apply_speaker_mapping_to_history_item(item: dict[str, Any], mapping: dict[str, str]) -> dict[str, Path]:
    output_paths = item.get("output_paths") or {}
    json_value = output_paths.get("json")
    txt_value = output_paths.get("txt")
    if not json_value:
        raise FileNotFoundError("Для выбранной записи нет JSON результата")
    json_path = Path(str(json_value))
    if not json_path.exists() or not json_path.is_file():
        raise FileNotFoundError(f"JSON результата недоступен: {json_path}")
    txt_path = Path(str(txt_value)) if txt_value else json_path.with_suffix(".txt")
    result = json.loads(json_path.read_text(encoding="utf-8"))
    if not isinstance(result, dict):
        raise ValueError("JSON результата имеет неподдерживаемый формат")
    updated = apply_speaker_mapping_to_result(result, mapping)
    atomic_write_json(json_path, updated)
    atomic_write_text(txt_path, render_result_text_for_export(updated))
    output_paths["txt"] = str(txt_path)
    return {"json": json_path, "txt": txt_path}


def history_item_export_text(item: dict[str, Any]) -> str:
    output_paths = item.get("output_paths") or {}
    txt_path = output_paths.get("txt")
    if txt_path:
        path = Path(str(txt_path))
        if path.exists() and path.is_file():
            return path.read_text(encoding="utf-8")

    json_path = output_paths.get("json")
    if json_path:
        path = Path(str(json_path))
        if path.exists() and path.is_file():
            try:
                result = json.loads(path.read_text(encoding="utf-8"))
            except Exception:
                result = {}
            if isinstance(result, dict):
                text = _segments_to_text(result)
                if text:
                    return text

    details = format_run_history_details(item)
    error = str(item.get("error") or "").strip()
    if error:
        return f"Транскрипция недоступна.\n\nОшибка:\n{error}\n\n{details}"
    return f"Транскрипция недоступна.\n\n{details}"


def export_history_item_text(item: dict[str, Any], export_root: Path) -> Path:
    day = history_item_day(item)
    export_dir = Path(export_root) / day
    export_dir.mkdir(parents=True, exist_ok=True)
    stem = _safe_export_stem(history_item_display_name(item))
    target = export_dir / f"{stem}.txt"
    suffix = 2
    while target.exists():
        target = export_dir / f"{stem}_{suffix:02d}.txt"
        suffix += 1
    atomic_write_text(target, history_item_export_text(item))
    return target


def export_history_day_texts(rows: list[dict[str, Any]], day: str, export_root: Path) -> list[Path]:
    selected = [item for item in rows if day in {"Все дни", "all"} or history_item_day(item) == day]
    return [export_history_item_text(item, export_root) for item in selected]


def format_run_history_details(item: dict[str, Any]) -> str:
    output_paths = item.get("output_paths") or {}
    lines = [
        "Run details",
        "=" * 70,
        f"Started: {item.get('started_at') or '-'}",
        f"Finished: {item.get('finished_at') or '-'}",
        f"Status: {item.get('status') or '-'}",
        f"Stage: {item.get('stage') or '-'}",
        f"Profile: {item.get('profile') or '-'}",
        f"Model: {item.get('model') or '-'}",
        f"Backend: {item.get('backend') or '-'}",
        f"Device: {item.get('device') or '-'}",
        f"Input: {item.get('input_path') or '-'}",
        f"Journal: {item.get('_path') or item.get('path') or '-'}",
        "",
        "Outputs",
        "-" * 70,
    ]
    if output_paths:
        for key in sorted(output_paths):
            lines.append(f"{key}: {output_paths.get(key) or '-'}")
    else:
        lines.append("-")

    lines.extend(
        [
            "",
            "Quality",
            "-" * 70,
            f"Audio duration: {float(item.get('audio_duration') or 0.0):.2f} sec",
            f"Segments: {int(item.get('segment_count') or 0)}",
            f"Largest gap: {float(item.get('largest_gap') or 0.0):.2f} sec",
            f"Last covered point: {float(item.get('coverage_end') or 0.0):.2f} sec",
            f"Speakers: {int(item.get('speaker_count') or 0)}",
            "",
            "Diarization",
            "-" * 70,
            f"Profile: {item.get('diarization_profile') or '-'}",
            f"Score: {float(item.get('diarization_score') or 0.0):.1f}",
            f"Score scope: {item.get('diarization_score_scope') or '-'}",
        ]
    )
    reasons = item.get("diarization_reasons") or []
    lines.append(f"Reasons: {', '.join(map(str, reasons)) if reasons else '-'}")

    candidates = item.get("diarization_candidates") or []
    if candidates:
        lines.extend(["", "Diarization candidates:"])
        for candidate in candidates:
            if not isinstance(candidate, dict):
                continue
            profile = candidate.get("profile") or "-"
            if candidate.get("failed"):
                lines.append(f"- {profile}: failed, error={candidate.get('error') or '-'}")
            else:
                lines.append(
                    f"- {profile}: score={float(candidate.get('score') or 0.0):.1f}, "
                    f"speakers={candidate.get('speaker_count') if candidate.get('speaker_count') is not None else '-'}, "
                    f"unknown_ratio={float(candidate.get('unknown_ratio') or 0.0):.3f}, "
                    f"short_turns={int(candidate.get('short_turn_count') or 0)}"
                )

    error = item.get("error")
    if error:
        lines.extend(["", "Error", "-" * 70, str(error)])
    return "\n".join(lines).rstrip() + "\n"


@dataclass
class RunJournal:
    path: Path
    input_path: str
    profile: str
    model: str
    backend: str
    device: str
    status: str = RunStatus.QUEUED
    stage: str = RunStage.QUEUED
    started_at: str = ""
    finished_at: str = ""
    error: str = ""
    output_paths: dict[str, str] | None = None
    audio_duration: float = 0.0
    segment_count: int = 0
    largest_gap: float = 0.0
    coverage_end: float = 0.0
    speaker_count: int = 0
    diarization_profile: str = ""
    diarization_score: float = 0.0
    diarization_score_scope: str = ""
    diarization_reasons: list[str] | None = None
    diarization_candidates: list[dict[str, Any]] | None = None

    def __post_init__(self) -> None:
        if not self.started_at:
            self.started_at = now_iso()
        if self.output_paths is None:
            self.output_paths = {}
        if self.diarization_reasons is None:
            self.diarization_reasons = []
        if self.diarization_candidates is None:
            self.diarization_candidates = []

    def write(self) -> None:
        payload = asdict(self)
        payload["path"] = str(self.path)
        atomic_write_json(self.path, payload)

    def update(self, *, stage: str | None = None, status: str | None = None, **fields: Any) -> None:
        if stage is not None:
            self.stage = stage
        if status is not None:
            self.status = status
        for key, value in fields.items():
            if hasattr(self, key):
                setattr(self, key, value)
        self.write()

    def finish(self, *, output_paths: dict[str, str], quality: dict[str, Any]) -> None:
        self.output_paths = output_paths
        self.audio_duration = float(quality.get("audio_duration") or 0.0)
        self.segment_count = int(quality.get("segment_count") or 0)
        self.largest_gap = float(quality.get("largest_gap") or 0.0)
        self.coverage_end = float(quality.get("coverage_end") or 0.0)
        self.speaker_count = int(quality.get("speaker_count") or 0)
        self.diarization_profile = str(quality.get("diarization_profile") or self.diarization_profile or "")
        self.diarization_score = float(quality.get("diarization_score") or self.diarization_score or 0.0)
        self.diarization_score_scope = str(
            quality.get("diarization_score_scope") or self.diarization_score_scope or ""
        )
        self.diarization_reasons = list(quality.get("diarization_reasons") or self.diarization_reasons or [])
        self.diarization_candidates = list(quality.get("diarization_candidates") or self.diarization_candidates or [])
        self.status = RunStatus.DONE
        self.stage = RunStage.DONE
        self.finished_at = now_iso()
        self.write()

    def fail(self, *, error: str, stage: str | None = None, output_paths: dict[str, str] | None = None) -> None:
        self.error = error
        if stage is not None:
            self.stage = stage
        self.status = RunStatus.FAILED
        self.finished_at = now_iso()
        if output_paths:
            self.output_paths = output_paths
        self.write()


def scan_run_history(directory: Path, limit: int = 50) -> list[dict[str, Any]]:
    if not directory.exists():
        return []

    entries: list[tuple[float, dict[str, Any]]] = []
    for path in directory.rglob("*_run.json"):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            continue
        try:
            mtime = path.stat().st_mtime
        except OSError:
            mtime = 0.0
        data["_path"] = str(path)
        entries.append((mtime, data))

    entries.sort(key=lambda item: item[0], reverse=True)
    return [item[1] for item in entries[:limit]]


def format_run_history_entry(item: dict[str, Any]) -> str:
    output_paths = item.get("output_paths") or {}
    result_path = output_paths.get("txt") or output_paths.get("json") or output_paths.get("failed_audio") or ""
    journal_path = item.get("_path")
    if result_path and journal_path:
        result_candidate = Path(str(result_path))
        if result_candidate.is_absolute() and not result_candidate.exists():
            result_path = f"{result_path} (missing; journal: {Path(str(journal_path)).parent})"
    elif journal_path:
        result_path = f"journal: {Path(str(journal_path)).parent}"
    diar_profile = str(item.get("diarization_profile") or "-")
    diar_score = item.get("diarization_score")
    diar_part = diar_profile
    if diar_score not in (None, ""):
        try:
            diar_part = f"{diar_profile}:{float(diar_score):.1f}"
        except (TypeError, ValueError):
            diar_part = f"{diar_profile}:{diar_score}"
    return (
        f"{item.get('finished_at') or item.get('started_at') or '-'} | "
        f"{item.get('status') or '-'} | "
        f"{item.get('profile') or '-'} | "
        f"diar={diar_part} | "
        f"{Path(str(item.get('input_path') or '')).name} | "
        f"{result_path}"
    )
