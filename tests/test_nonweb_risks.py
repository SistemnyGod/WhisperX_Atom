from __future__ import annotations

import json
import os
import wave
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from app import WhisperXApp, WhisperXService
from auto_transcribe_watch import AutoTranscribeWatcher, WatchConfig
from diarization_quality import (
    choose_best_diarization_candidate,
    diarization_profiles_for_processing_profile,
    score_diarization_result,
    smooth_speaker_turns,
)
from glossary_utils import apply_glossary_rules, load_hotwords_text, parse_glossary_rules
from live_runtime import (
    LiveChunkStatus,
    LiveSessionStore,
    LiveStatus,
    SoundDeviceChunkRecorder,
    SpeakerRegistry,
    concatenate_wav_files,
)
from local_io import atomic_write_json, atomic_write_text
from media_binaries import media_has_audio_stream
from processing_runtime import (
    MEDIA_EXTENSIONS,
    PROFILES,
    RunJournal,
    RunStage,
    RunStatus,
    apply_speaker_mapping_to_history_item,
    apply_speaker_mapping_to_result,
    collect_speaker_stats,
    delete_history_item,
    export_history_day_texts,
    export_history_item_text,
    format_history_browser_entry,
    format_speaker_mapping_template,
    ensure_text_file,
    format_quality_report,
    format_run_history_details,
    format_run_history_entry,
    get_processing_profile,
    group_history_by_day,
    history_item_artifact_paths,
    history_item_day,
    history_item_display_name,
    history_item_export_text,
    parse_speaker_mapping_text,
    quality_from_result,
    retry_source_from_history_item,
    scan_run_history,
    summarize_result_quality,
    unique_result_paths,
)
from runtime_secrets import load_hf_token_from_env
from transcription_quality import preprocess_output_path


class NonWebRiskTests(unittest.TestCase):
    @staticmethod
    def _write_test_wav(path: Path, frames: int = 160) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        with wave.open(str(path), "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(16000)
            wav.writeframes(b"\0\0" * frames)

    def test_glossary_rules_apply_plain_and_regex_replacements(self) -> None:
        rules = parse_glossary_rules("whisper -> WhisperX\nre:\\btt\\b => TT")

        updated, replacements = apply_glossary_rules("whisper handles tt", rules)

        self.assertEqual(updated, "WhisperX handles TT")
        self.assertEqual(replacements, 2)

    def test_hotwords_loads_file_and_inline_text(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            (base / "hotwords.txt").write_text("alpha\n", encoding="utf-8")

            text = load_hotwords_text(base, "beta")

        self.assertEqual(text, "alpha\nbeta")

    def test_preprocess_output_path_uses_profile_suffix(self) -> None:
        output = preprocess_output_path(Path("meeting.wav"), "diar_soft")

        self.assertEqual(output.name, "meeting.diar_soft.wav")

    def test_atomic_write_text_and_json_replace_existing_content(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            text_path = Path(tmp) / "result.txt"
            json_path = Path(tmp) / "result.json"

            atomic_write_text(text_path, "first")
            atomic_write_text(text_path, "second")
            atomic_write_json(json_path, {"ok": True})

            self.assertEqual(text_path.read_text(encoding="utf-8"), "second")
            self.assertEqual(json.loads(json_path.read_text(encoding="utf-8")), {"ok": True})
            self.assertFalse((Path(tmp) / ".result.txt.tmp").exists())
            self.assertFalse(list(Path(tmp).glob(".result.txt.*.tmp")))

    def test_unique_result_paths_avoid_same_second_overwrite(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            first = unique_result_paths(base, Path("meeting.wav"), "20260101_010101")
            first["json"].write_text("{}", encoding="utf-8")
            first["txt"].write_text("", encoding="utf-8")
            first["journal"].write_text("{}", encoding="utf-8")

            second = unique_result_paths(base, Path("meeting.wav"), "20260101_010101")

        self.assertEqual(first["json"].name, "meeting_20260101_010101.json")
        self.assertEqual(second["json"].name, "meeting_20260101_010101_02.json")
        self.assertEqual(second["quality"].name, "meeting_20260101_010101_02_quality.txt")
        self.assertEqual(second["journal"].name, "meeting_20260101_010101_02_run.json")

    def test_live_session_store_persists_chunks_status_and_final_outputs(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            store = LiveSessionStore(Path(tmp))
            session = store.create_session(profile="meeting")
            chunk = store.create_chunk(session, global_offset_sec=20.0)

            store.update_chunk(
                session,
                chunk,
                status=LiveChunkStatus.DONE,
                text="Привет",
                segments=[{"start": 20.0, "end": 21.0, "text": "Привет", "speaker": "Спикер 1"}],
            )
            store.append_partial_transcript(session, "[00:20:00] Спикер 1: Привет")
            store.set_final_outputs(session, {"json": "final.json", "txt": "final.txt"})
            store.set_status(session, LiveStatus.DONE)

            data = json.loads((Path(session.root_dir) / "session.json").read_text(encoding="utf-8"))

        self.assertEqual(data["status"], LiveStatus.DONE)
        self.assertEqual(data["profile"], "meeting")
        self.assertEqual(data["chunks"][0]["status"], LiveChunkStatus.DONE)
        self.assertEqual(data["chunks"][0]["global_offset_sec"], 20.0)
        self.assertEqual(data["final_output_paths"]["json"], "final.json")
        self.assertIn("Спикер 1", data["partial_transcript"])

    def test_speaker_registry_keeps_stable_names_between_chunks(self) -> None:
        registry = SpeakerRegistry()

        first, first_map = registry.apply_to_segments(
            [{"start": 0.0, "end": 2.0, "speaker": "SPEAKER_00", "words": [{"word": "hello", "speaker": "SPEAKER_00"}]}]
        )
        second, second_map = registry.apply_to_segments(
            [{"start": 20.0, "end": 22.0, "speaker": "SPEAKER_00"}]
        )
        third, third_map = registry.apply_to_segments(
            [{"start": 23.0, "end": 25.0, "speaker": "SPEAKER_01"}]
        )

        self.assertEqual(first[0]["speaker"], "Спикер 1")
        self.assertEqual(first[0]["words"][0]["speaker"], "Спикер 1")
        self.assertEqual(second[0]["speaker"], "Спикер 1")
        self.assertEqual(third[0]["speaker"], "Спикер 2")
        self.assertEqual(first_map["SPEAKER_00"], second_map["SPEAKER_00"])
        self.assertEqual(third_map["SPEAKER_01"], "Спикер 2")

    def test_concatenate_wav_files_merges_compatible_chunks(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            first = base / "chunk_0001.wav"
            second = base / "chunk_0002.wav"
            output = base / "final" / "session.wav"
            self._write_test_wav(first, frames=160)
            self._write_test_wav(second, frames=320)

            concatenate_wav_files([first, second], output)

            with wave.open(str(output), "rb") as wav:
                frame_count = wav.getnframes()

        self.assertEqual(frame_count, 480)

    def test_sounddevice_recorder_dependency_is_optional_until_recording(self) -> None:
        with patch("importlib.util.find_spec", return_value=None):
            self.assertFalse(SoundDeviceChunkRecorder.is_available())
            with self.assertRaisesRegex(RuntimeError, "sounddevice"):
                SoundDeviceChunkRecorder().record_chunk(Path("unused.wav"), 1.0)

    def test_hf_token_comes_from_environment(self) -> None:
        with patch.dict(os.environ, {"HF_TOKEN": "env-token"}, clear=False):
            self.assertEqual(load_hf_token_from_env(), "env-token")

    def test_watcher_ignores_hf_token_in_settings(self) -> None:
        settings = {"hf_token": "settings-token", "hftoken": "legacy-settings-token", "device": "cpu"}

        with patch.dict(os.environ, {"HF_TOKEN": "env-token"}, clear=False):
            config = WatchConfig.from_settings(settings)

        self.assertEqual(config.hf_token, "env-token")

    def test_processing_profile_mapping_for_all_profiles(self) -> None:
        self.assertEqual(set(PROFILES), {"fast", "accurate", "meeting", "noisy"})
        self.assertEqual(get_processing_profile("Быстро").key, "fast")
        self.assertEqual(get_processing_profile("fast").max_auto_model, "medium")
        self.assertEqual(get_processing_profile("unknown").key, "meeting")

    def test_watcher_uses_profile_defaults_when_settings_are_missing(self) -> None:
        with patch.dict(os.environ, {"HF_TOKEN": "env-token"}, clear=False):
            config = WatchConfig.from_settings({"device": "cpu", "processing_profile": "noisy"})

        profile = get_processing_profile("noisy")
        self.assertEqual(config.processing_profile, "noisy")
        self.assertEqual(config.model_name, profile.model)
        self.assertEqual(config.batch_size, profile.batch_size)
        self.assertEqual(config.vad_onset, profile.vad_onset)

    def test_media_extensions_include_common_video_formats(self) -> None:
        self.assertIn(".mp4", MEDIA_EXTENSIONS)
        self.assertIn(".mov", MEDIA_EXTENSIONS)
        self.assertIn(".mkv", MEDIA_EXTENSIONS)

    def test_media_has_audio_stream_reads_ffprobe_audio_stream(self) -> None:
        with patch("media_binaries.require_binary", return_value=Path("ffprobe.exe")), patch(
            "media_binaries.subprocess.check_output",
            return_value=b"audio\n",
        ):
            self.assertTrue(media_has_audio_stream("meeting.mp4"))

    def test_media_has_audio_stream_returns_false_without_audio_stream(self) -> None:
        with patch("media_binaries.require_binary", return_value=Path("ffprobe.exe")), patch(
            "media_binaries.subprocess.check_output",
            return_value=b"",
        ):
            self.assertFalse(media_has_audio_stream("silent.mp4"))

    def test_gui_validation_accepts_video_files(self) -> None:
        gui = WhisperXApp.__new__(WhisperXApp)
        with tempfile.TemporaryDirectory() as tmp, patch("app.media_has_audio_stream", return_value=True):
            video = Path(tmp) / "meeting.mp4"
            video.write_bytes(b"not-a-real-video")

            ok, error = gui._validate_audio_file(str(video), "meeting")

        self.assertTrue(ok)
        self.assertEqual(error, "")

    def test_gui_validation_rejects_video_without_audio_stream(self) -> None:
        gui = WhisperXApp.__new__(WhisperXApp)
        with tempfile.TemporaryDirectory() as tmp, patch("app.media_has_audio_stream", return_value=False):
            video = Path(tmp) / "silent.mp4"
            video.write_bytes(b"not-a-real-video")

            ok, error = gui._validate_audio_file(str(video), "meeting")

        self.assertFalse(ok)
        self.assertIn("аудиодорож", error)

    def test_gui_preflight_requires_ffprobe_for_video_even_without_preprocess(self) -> None:
        service = WhisperXService(log_cb=lambda _: None, progress_cb=lambda _: None)
        request = {
            "selected_file": "meeting.mp4",
            "reference_file": "",
            "source_file": "",
            "preprocess": False,
            "diarize": False,
            "backend": "faster-whisper",
            "auto_model": False,
        }

        with patch("app.require_ffmpeg_tools", return_value=(Path("ffmpeg.exe"), Path("ffprobe.exe"))) as tools, patch(
            "app.media_has_audio_stream",
            return_value=True,
        ) as has_audio:
            service.ensure_audio_dependencies(request)

        self.assertTrue(tools.call_args.kwargs["require_ffprobe"])
        has_audio.assert_called_once()

    def test_gui_preflight_rejects_video_without_audio_stream(self) -> None:
        service = WhisperXService(log_cb=lambda _: None, progress_cb=lambda _: None)
        request = {
            "selected_file": "silent.mp4",
            "reference_file": "",
            "source_file": "",
            "preprocess": False,
            "diarize": False,
            "backend": "faster-whisper",
            "auto_model": False,
        }

        with patch("app.require_ffmpeg_tools", return_value=(Path("ffmpeg.exe"), Path("ffprobe.exe"))), patch(
            "app.media_has_audio_stream",
            return_value=False,
        ):
            with self.assertRaisesRegex(RuntimeError, "аудиодорож"):
                service.ensure_audio_dependencies(request)

    def test_watcher_accepts_video_files(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            watcher = AutoTranscribeWatcher(Path(tmp))
            video = watcher.input_dir / "meeting.mp4"
            ignored = watcher.input_dir / "notes.txt"
            temp_asr = watcher.input_dir / "meeting.asr_retry.mp4"
            video.write_bytes(b"")
            ignored.write_text("skip", encoding="utf-8")
            temp_asr.write_bytes(b"")

            pending = watcher.iter_pending_files()

        self.assertEqual([path.name for path in pending], ["meeting.mp4"])

    def test_watcher_rejects_video_without_audio_stream(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            watcher = AutoTranscribeWatcher(Path(tmp))
            video = watcher.input_dir / "silent.mp4"
            video.write_bytes(b"")

            with patch("auto_transcribe_watch.media_has_audio_stream", return_value=False):
                with self.assertRaisesRegex(RuntimeError, "аудиодорож"):
                    watcher.ensure_supported_media(video)

    def test_watcher_skips_asr_and_diar_temp_files(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            watcher = AutoTranscribeWatcher(Path(tmp))
            wanted = watcher.input_dir / "meeting.wav"
            temp_asr = watcher.input_dir / "meeting.asr_retry.wav"
            temp_diar = watcher.input_dir / "meeting.diar-soft.wav"
            wanted.write_bytes(b"")
            temp_asr.write_bytes(b"")
            temp_diar.write_bytes(b"")

            pending = watcher.iter_pending_files()

        self.assertEqual([path.name for path in pending], ["meeting.wav"])

    def test_run_journal_lifecycle_done_and_failed(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "sample_run.json"
            journal = RunJournal(
                path=path,
                input_path="sample.wav",
                profile="meeting",
                model="large-v3",
                backend="whisperx",
                device="cpu",
            )
            journal.write()
            journal.update(stage=RunStage.ASR, status=RunStatus.RUNNING)
            journal.finish(
                output_paths={"json": "sample.json", "txt": "sample.txt"},
                quality={"segment_count": 2, "largest_gap": 1.5, "coverage_end": 20.0, "speaker_count": 1},
            )

            done = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(done["status"], RunStatus.DONE)
            self.assertEqual(done["stage"], RunStage.DONE)
            self.assertEqual(done["segment_count"], 2)

            failed_path = Path(tmp) / "failed_run.json"
            failed = RunJournal(
                path=failed_path,
                input_path="broken.wav",
                profile="fast",
                model="medium",
                backend="whisperx",
                device="cpu",
            )
            failed.fail(error="ffmpeg failed", stage=RunStage.FFMPEG)
            failed_data = json.loads(failed_path.read_text(encoding="utf-8"))
            self.assertEqual(failed_data["status"], RunStatus.FAILED)
            self.assertEqual(failed_data["stage"], RunStage.FFMPEG)
            self.assertEqual(failed_data["error"], "ffmpeg failed")

    def test_history_scanner_sorts_recent_runs(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            first = base / "a_run.json"
            second = base / "b_run.json"
            atomic_write_json(first, {"input_path": "a.wav", "status": "done"})
            atomic_write_json(second, {"input_path": "b.wav", "status": "failed"})
            os.utime(first, (1, 1))
            os.utime(second, (2, 2))

            rows = scan_run_history(base)

        self.assertEqual([Path(row["input_path"]).name for row in rows], ["b.wav", "a.wav"])

    def test_history_scanner_finds_nested_live_session_runs(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            nested = base / "live_sessions" / "live_20260101_100000" / "final"
            nested.mkdir(parents=True)
            atomic_write_json(nested / "meeting_run.json", {"input_path": "C:/audio/meeting.wav", "status": "done"})

            rows = scan_run_history(base)

        self.assertEqual(len(rows), 1)
        self.assertEqual(Path(rows[0]["input_path"]).name, "meeting.wav")

    def test_history_day_grouping_and_browser_entry_use_audio_name(self) -> None:
        rows = [
            {
                "started_at": "2026-01-02T09:00:00",
                "status": "done",
                "profile": "meeting",
                "input_path": "C:/audio/Команда.wav",
                "speaker_count": 3,
                "output_paths": {"txt": "C:/out/Команда.txt"},
            },
            {
                "started_at": "2026-01-01T09:00:00",
                "status": "failed",
                "profile": "fast",
                "input_path": "C:/audio/Ошибка.wav",
                "output_paths": {},
            },
        ]

        grouped = group_history_by_day(rows)
        entry = format_history_browser_entry(rows[0])

        self.assertEqual(list(grouped), ["2026-01-02", "2026-01-01"])
        self.assertEqual(history_item_day(rows[0]), "2026-01-02")
        self.assertEqual(history_item_display_name(rows[0]), "Команда")
        self.assertIn("Команда", entry)
        self.assertIn("спикеров=3", entry)
        self.assertIn("TXT", entry)

    def test_history_txt_export_uses_existing_txt_and_audio_filename(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source_txt = base / "source.txt"
            source_txt.write_text("Готовая транскрипция\n", encoding="utf-8")
            item = {
                "finished_at": "2026-01-02T09:00:00",
                "input_path": "C:/audio/Планерка.wav",
                "output_paths": {"txt": str(source_txt)},
            }

            exported = export_history_item_text(item, base / "exports")
            exported_text = exported.read_text(encoding="utf-8")

        self.assertEqual(exported.name, "Планерка.txt")
        self.assertEqual(exported.parent.name, "2026-01-02")
        self.assertEqual(exported_text, "Готовая транскрипция\n")

    def test_history_txt_export_falls_back_to_json_segments_for_notepad(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            json_path = base / "result.json"
            atomic_write_json(
                json_path,
                {
                    "segments": [
                        {"speaker": "Спикер 1", "text": "Добрый день"},
                        {"speaker": "Спикер 2", "text": "Начинаем"},
                    ]
                },
            )
            item = {
                "started_at": "2026-01-03T10:00:00",
                "input_path": "C:/audio/Совещание.wav",
                "output_paths": {"json": str(json_path)},
            }

            text = history_item_export_text(item)
            exported = export_history_item_text(item, base / "exports")
            exported_text = exported.read_text(encoding="utf-8")

        self.assertIn("Спикер 1: Добрый день", text)
        self.assertEqual(exported.name, "Совещание.txt")
        self.assertIn("Спикер 2: Начинаем", exported_text)

    def test_history_day_export_exports_only_selected_day(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            first_txt = base / "first.txt"
            second_txt = base / "second.txt"
            first_txt.write_text("one\n", encoding="utf-8")
            second_txt.write_text("two\n", encoding="utf-8")
            rows = [
                {"finished_at": "2026-01-02T09:00:00", "input_path": "C:/a/one.wav", "output_paths": {"txt": str(first_txt)}},
                {"finished_at": "2026-01-03T09:00:00", "input_path": "C:/a/two.wav", "output_paths": {"txt": str(second_txt)}},
            ]

            exported = export_history_day_texts(rows, "2026-01-03", base / "exports")

        self.assertEqual([path.name for path in exported], ["two.txt"])
        self.assertEqual(exported[0].parent.name, "2026-01-03")

    def test_speaker_mapping_template_parse_and_apply_merges_speakers(self) -> None:
        result = {
            "segments": [
                {
                    "start": 0.0,
                    "end": 2.0,
                    "speaker": "Спикер 1",
                    "text": "Добрый день",
                    "words": [{"word": "Добрый", "speaker": "Спикер 1"}],
                },
                {"start": 2.0, "end": 4.0, "speaker": "Спикер 2", "text": "Начинаем"},
            ],
            "word_segments": [{"word": "Начинаем", "speaker": "Спикер 2"}],
            "_quality_summary": {"speaker_count": 2},
        }

        template = format_speaker_mapping_template(result)
        mapping = parse_speaker_mapping_text(
            "Спикер 1 = Иван Петров\nСпикер 2 = Иван Петров\nUNKNOWN = UNKNOWN\n"
        )
        updated = apply_speaker_mapping_to_result(result, mapping)
        stats = collect_speaker_stats(updated)

        self.assertIn("реплик=1", template)
        self.assertEqual(updated["segments"][0]["speaker"], "Иван Петров")
        self.assertEqual(updated["segments"][0]["speaker_original"], "Спикер 1")
        self.assertEqual(updated["segments"][0]["words"][0]["speaker"], "Иван Петров")
        self.assertEqual(updated["word_segments"][0]["speaker"], "Иван Петров")
        self.assertEqual(updated["_quality_summary"]["speaker_count"], 1)
        self.assertEqual([item["speaker"] for item in stats], ["Иван Петров"])
        self.assertEqual(stats[0]["segment_count"], 2)

    def test_apply_speaker_mapping_to_history_item_updates_json_and_txt(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            json_path = base / "meeting.json"
            txt_path = base / "meeting.txt"
            atomic_write_json(
                json_path,
                {
                    "segments": [
                        {"start": 0.0, "end": 1.0, "speaker": "Спикер 1", "text": "Привет"},
                        {"start": 1.0, "end": 2.0, "speaker": "Спикер 2", "text": "Ответ"},
                    ],
                    "_quality_summary": {"speaker_count": 2},
                },
            )
            txt_path.write_text("old\n", encoding="utf-8")
            item = {"output_paths": {"json": str(json_path), "txt": str(txt_path)}}

            paths = apply_speaker_mapping_to_history_item(
                item,
                {"Спикер 1": "Анна", "Спикер 2": "Анна"},
            )
            updated = json.loads(json_path.read_text(encoding="utf-8"))
            txt = txt_path.read_text(encoding="utf-8")

        self.assertEqual(paths["json"], json_path)
        self.assertEqual(paths["txt"], txt_path)
        self.assertEqual(updated["segments"][0]["speaker"], "Анна")
        self.assertEqual(updated["_quality_summary"]["speaker_count"], 1)
        self.assertIn("Анна: Привет", txt)
        self.assertIn("Анна: Ответ", txt)

    def test_history_formatter_includes_diarization_score(self) -> None:
        line = format_run_history_entry(
            {
                "finished_at": "2026-01-01T10:00:00",
                "status": "done",
                "profile": "meeting",
                "input_path": "C:/audio/meeting.wav",
                "diarization_profile": "diar_soft",
                "diarization_score": 88.25,
                "output_paths": {"txt": "C:/out/meeting.txt"},
            }
        )

        self.assertIn("diar=diar_soft:88.2", line)
        self.assertIn("meeting.wav", line)
        self.assertIn("C:/out/meeting.txt", line)

    def test_history_formatter_mentions_journal_folder_when_output_is_missing(self) -> None:
        line = format_run_history_entry(
            {
                "started_at": "2026-01-01T10:00:00",
                "status": "failed",
                "profile": "noisy",
                "input_path": "C:/audio/broken.wav",
                "_path": "C:/out/broken_run.json",
                "output_paths": {"json": "C:/missing/broken.json"},
            }
        )

        self.assertIn("broken.wav", line)
        self.assertIn("C:/missing/broken.json", line)
        self.assertIn("journal:", line)

    def test_history_details_include_timestamps_outputs_and_diarization_candidates(self) -> None:
        details = format_run_history_details(
            {
                "started_at": "2026-01-01T10:00:00",
                "finished_at": "2026-01-01T10:03:00",
                "status": "done",
                "stage": "done",
                "profile": "meeting",
                "model": "large-v3",
                "backend": "whisperx",
                "device": "cuda",
                "input_path": "C:/audio/meeting.wav",
                "_path": "C:/out/meeting_run.json",
                "output_paths": {"json": "C:/out/meeting.json", "quality": "C:/out/meeting_quality.txt"},
                "segment_count": 10,
                "speaker_count": 2,
                "diarization_profile": "diar_soft",
                "diarization_score": 91.5,
                "diarization_reasons": ["speaker_count_in_expected_range"],
                "diarization_candidates": [
                    {"profile": "diar_soft", "score": 91.5, "speaker_count": 2, "unknown_ratio": 0.01}
                ],
            }
        )

        self.assertIn("Started: 2026-01-01T10:00:00", details)
        self.assertIn("Finished: 2026-01-01T10:03:00", details)
        self.assertIn("quality: C:/out/meeting_quality.txt", details)
        self.assertIn("Profile: diar_soft", details)
        self.assertIn("- diar_soft: score=91.5", details)

    def test_delete_history_item_removes_outputs_and_journal_but_keeps_audio_by_default(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            json_path = base / "result.json"
            txt_path = base / "result.txt"
            quality_path = base / "result_quality.txt"
            journal_path = base / "result_run.json"
            processed_audio = base / "processed.wav"
            for path in (json_path, txt_path, quality_path, journal_path, processed_audio):
                path.write_text("x", encoding="utf-8")
            item = {
                "_path": str(journal_path),
                "output_paths": {
                    "json": str(json_path),
                    "txt": str(txt_path),
                    "quality": str(quality_path),
                    "processed_audio": str(processed_audio),
                },
            }

            artifact_paths = history_item_artifact_paths(item)
            deleted = delete_history_item(item)

            self.assertNotIn(processed_audio, artifact_paths)
            self.assertFalse(json_path.exists())
            self.assertFalse(txt_path.exists())
            self.assertFalse(quality_path.exists())
            self.assertFalse(journal_path.exists())
            self.assertTrue(processed_audio.exists())
            self.assertEqual(len(deleted), 4)

    def test_app_spec_includes_shared_modules_and_text_templates(self) -> None:
        spec_text = Path("app.spec").read_text(encoding="utf-8")

        for module in (
            "diarization_quality",
            "glossary_utils",
            "live_runtime",
            "local_io",
            "media_binaries",
            "processing_runtime",
            "runtime_secrets",
            "transcription_quality",
        ):
            self.assertIn(repr(module), spec_text)
        self.assertIn("('glossary.txt', '.')", spec_text)
        self.assertIn("('hotwords.txt', '.')", spec_text)

    def test_glossary_and_hotwords_are_readable_utf8_without_mojibake(self) -> None:
        glossary = Path("glossary.txt").read_text(encoding="utf-8")
        hotwords = Path("hotwords.txt").read_text(encoding="utf-8")

        self.assertIn("Глоссарий исправлений", glossary)
        self.assertIn("Важные ФИО", hotwords)
        self.assertNotIn("Р“", glossary + hotwords)
        self.assertNotIn("Р’", glossary + hotwords)

    def test_ensure_text_file_creates_empty_glossary_or_hotwords_file(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "glossary.txt"

            created = ensure_text_file(target)

            self.assertEqual(created, target)
            self.assertTrue(target.exists())
            self.assertEqual(target.read_text(encoding="utf-8"), "")

    def test_quality_summary_counts_segments_gaps_and_speakers(self) -> None:
        summary = summarize_result_quality(
            {
                "segments": [
                    {"start": 0.0, "end": 2.0, "speaker": "A"},
                    {"start": 5.0, "end": 7.0, "speaker": "B"},
                ]
            },
            audio_duration=10.0,
        )

        self.assertEqual(summary["segment_count"], 2)
        self.assertEqual(summary["largest_gap"], 3.0)
        self.assertEqual(summary["coverage_end"], 7.0)
        self.assertEqual(summary["speaker_count"], 2)

    def test_quality_from_result_merges_diarization_metadata(self) -> None:
        quality = quality_from_result(
            {
                "segments": [{"start": 0.0, "end": 1.0, "speaker": "A"}],
                "_diarization": {
                    "profile": "diar_soft",
                    "score": 91.5,
                    "score_scope": "pre_reference_filter",
                    "reasons": ["speaker_count_in_expected_range"],
                    "candidates": [{"profile": "diar_soft", "score": 91.5}],
                },
            },
            audio_duration=2.0,
        )

        self.assertEqual(quality["segment_count"], 1)
        self.assertEqual(quality["diarization_profile"], "diar_soft")
        self.assertEqual(quality["diarization_score"], 91.5)
        self.assertEqual(quality["diarization_score_scope"], "pre_reference_filter")
        self.assertEqual(quality["diarization_reasons"], ["speaker_count_in_expected_range"])

    def test_quality_report_includes_diarization_details_and_outputs(self) -> None:
        report = format_quality_report(
            {
                "segment_count": 3,
                "audio_duration": 120.0,
                "largest_gap": 4.5,
                "coverage_end": 110.0,
                "speaker_count": 2,
                "diarization_profile": "diar_soft",
                "diarization_score": 91.5,
                "diarization_score_scope": "pre_reference_filter",
                "diarization_reasons": ["speaker_count_in_expected_range"],
                "diarization_candidates": [
                    {"profile": "diar_soft", "score": 91.5, "speaker_count": 2, "unknown_ratio": 0.01}
                ],
            },
            input_path="meeting.wav",
            output_paths={"json": "meeting.json", "txt": "meeting.txt", "processed_audio": "processed/meeting.wav"},
        )

        self.assertIn("Input: meeting.wav", report)
        self.assertIn("Processed audio: processed/meeting.wav", report)
        self.assertIn("Segments: 3", report)
        self.assertIn("Diarization profile: diar_soft", report)
        self.assertIn("speaker_count_in_expected_range", report)
        self.assertIn("- diar_soft: score=91.5", report)

    def test_gui_worker_updates_output_paths_through_main_thread_callback(self) -> None:
        source = Path("app.py").read_text(encoding="utf-8")
        worker_body = source.split("    def _transcribe_worker(self, cancel: threading.Event):", 1)[1].split(
            "    def export_txt(self):", 1
        )[0]

        self.assertNotIn("self._remember_output_paths(output_paths", worker_body)
        self.assertIn("self.root.after(", worker_body)
        self.assertIn("self._show_completed_result", worker_body)

    def test_gui_completion_notification_is_shown_from_result_handler(self) -> None:
        source = Path("app.py").read_text(encoding="utf-8")
        result_handler = source.split("    def _show_completed_result(", 1)[1].split(
            "    def open_last_output", 1
        )[0]
        worker_body = source.split("    def _transcribe_worker(self, cancel: threading.Event):", 1)[1].split(
            "    def export_txt(self):", 1
        )[0]

        self.assertIn("self._show_completion_notification(output_paths)", result_handler)
        self.assertNotIn("_show_completion_notification", worker_body)
        self.assertIn("messagebox.showinfo", source.split("    def _show_completion_notification", 1)[1])

    def test_retry_source_from_history_item_uses_existing_failed_or_processed_audio(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            missing_input = base / "missing.wav"
            failed_audio = base / "failed.wav"
            failed_audio.write_bytes(b"RIFF")

            source = retry_source_from_history_item(
                {
                    "input_path": str(missing_input),
                    "output_paths": {"failed_audio": str(failed_audio)},
                },
                {".wav"},
            )

        self.assertEqual(source, failed_audio)

    def test_gui_history_retry_source_delegates_to_shared_helper(self) -> None:
        gui = WhisperXApp.__new__(WhisperXApp)
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            missing_input = base / "missing.wav"
            failed_audio = base / "failed.wav"
            failed_audio.write_bytes(b"RIFF")

            source = gui._history_retry_source(
                {
                    "input_path": str(missing_input),
                    "output_paths": {"failed_audio": str(failed_audio)},
                }
            )

        self.assertEqual(source, failed_audio)

    def test_gui_history_uses_single_actions_menu_instead_of_button_grid(self) -> None:
        source = Path("app.py").read_text(encoding="utf-8")
        history_section = source.split('history_tab = tabs.tab("История")', 1)[1].split(
            'self._build_live_tab(tabs.tab("Совещание"))', 1
        )[0]

        self.assertIn("self.history_actions_btn", history_section)
        self.assertIn("self._open_history_context_menu", source)
        self.assertIn("open_history_action_menu", source)
        self.assertNotIn("self.history_retry_tab_btn", history_section)
        self.assertNotIn("self.history_download_txt_btn", history_section)
        self.assertNotIn("self.history_delete_btn", history_section)

    def test_diarization_scorer_prefers_multi_speaker_over_collapsed_result(self) -> None:
        diar_segments = [
            {"start": 0.0, "end": 3.0, "speaker": "A"},
            {"start": 3.0, "end": 6.0, "speaker": "B"},
        ]
        collapsed = [
            {"start": 0.0, "end": 3.0, "speaker": "A", "text": "hello"},
            {"start": 3.0, "end": 6.0, "speaker": "A", "text": "world"},
        ]
        separated = [
            {"start": 0.0, "end": 3.0, "speaker": "A", "text": "hello"},
            {"start": 3.0, "end": 6.0, "speaker": "B", "text": "world"},
        ]

        collapsed_score = score_diarization_result(collapsed, diar_segments, "diar", 2, 4)
        separated_score = score_diarization_result(separated, diar_segments, "diar_soft", 2, 4)

        self.assertGreater(separated_score.score, collapsed_score.score)
        self.assertIn("collapsed_to_one_speaker", collapsed_score.reasons)

    def test_diarization_scorer_penalizes_short_speaker_switches(self) -> None:
        stable = [
            {"start": 0.0, "end": 4.0, "speaker": "A", "text": "long opening"},
            {"start": 4.0, "end": 8.0, "speaker": "B", "text": "long reply"},
            {"start": 8.0, "end": 12.0, "speaker": "A", "text": "long close"},
        ]
        noisy = [
            {"start": 0.0, "end": 4.0, "speaker": "A", "text": "long opening"},
            {"start": 4.0, "end": 4.4, "speaker": "B", "text": "yes"},
            {"start": 4.4, "end": 8.0, "speaker": "A", "text": "continues"},
        ]
        diar_segments = stable

        stable_score = score_diarization_result(stable, diar_segments, "diar", 1, 4)
        noisy_score = score_diarization_result(noisy, diar_segments, "diar", 1, 4)

        self.assertGreater(stable_score.score, noisy_score.score)
        self.assertIn("short_speaker_switches", noisy_score.reasons)

    def test_smooth_speaker_turns_repairs_short_insert_between_same_speaker(self) -> None:
        segments = [
            {"start": 0.0, "end": 5.0, "speaker": "A", "text": "first long turn"},
            {"start": 5.0, "end": 5.5, "speaker": "B", "text": "ok", "words": [{"word": "ok", "speaker": "B"}]},
            {"start": 5.5, "end": 9.0, "speaker": "A", "text": "same speaker continues"},
        ]

        smoothed = smooth_speaker_turns(segments)

        self.assertEqual(smoothed[1]["speaker"], "A")
        self.assertEqual(smoothed[1]["words"][0]["speaker"], "A")

    def test_diarization_profile_policy_keeps_fast_single_pass(self) -> None:
        self.assertEqual(diarization_profiles_for_processing_profile("fast", "diar"), ["diar"])
        self.assertEqual(diarization_profiles_for_processing_profile("accurate", "diar_soft"), ["diar_soft", "diar"])
        self.assertEqual(diarization_profiles_for_processing_profile("meeting", "diar"), ["diar", "diar_soft"])
        self.assertEqual(diarization_profiles_for_processing_profile("noisy", "diar_soft"), ["diar_soft", "diar"])

    def test_choose_best_diarization_candidate_uses_score_then_primary_tiebreaker(self) -> None:
        left_score = score_diarization_result(
            [{"start": 0, "end": 3, "speaker": "A", "text": "a"}],
            [{"start": 0, "end": 3, "speaker": "A"}],
            "diar",
            1,
            2,
        )
        right_score = score_diarization_result(
            [{"start": 0, "end": 3, "speaker": "A", "text": "a"}],
            [{"start": 0, "end": 3, "speaker": "A"}],
            "diar_soft",
            1,
            2,
        )

        chosen = choose_best_diarization_candidate(
            [
                {"profile": "diar_soft", "is_primary": False, "score": right_score},
                {"profile": "diar", "is_primary": True, "score": left_score},
            ]
        )

        self.assertEqual(chosen["profile"], "diar")

    def test_run_journal_finish_persists_diarization_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "sample_run.json"
            journal = RunJournal(path=path, input_path="sample.wav", profile="meeting", model="large-v3", backend="whisperx", device="cpu")
            journal.finish(
                output_paths={"json": "sample.json"},
                quality={
                    "diarization_profile": "diar_soft",
                    "diarization_score": 91.5,
                    "diarization_score_scope": "pre_reference_filter",
                    "diarization_reasons": ["speaker_count_in_expected_range"],
                    "diarization_candidates": [{"profile": "diar_soft", "score": 91.5}],
                },
            )

            data = json.loads(path.read_text(encoding="utf-8"))

        self.assertEqual(data["diarization_profile"], "diar_soft")
        self.assertEqual(data["diarization_score"], 91.5)
        self.assertEqual(data["diarization_score_scope"], "pre_reference_filter")
        self.assertEqual(data["diarization_reasons"], ["speaker_count_in_expected_range"])

    def test_web_pipeline_skips_diar_audio_when_diarization_disabled(self) -> None:
        source = Path("app/transcription_pipeline.py").read_text(encoding="utf-8")
        worker_audio = source.split("    async def worker_audio(self) -> None:", 1)[1].split(
            "    async def worker_asr(self) -> None:", 1
        )[0]

        self.assertIn("media_has_audio_stream", worker_audio)
        self.assertIn("if self.config.preprocess_asr or is_video:", worker_audio)
        guard_index = worker_audio.index("if self.config.enable_diarization:")
        diar_index = worker_audio.index("diar_path = await asyncio.to_thread(self._preprocess_audio, ctx.audio_path, asr=False)")
        self.assertLess(guard_index, diar_index)

    def test_gui_settings_source_does_not_persist_token_fields(self) -> None:
        source = Path("app.py").read_text(encoding="utf-8")
        save_settings = source.split("    def _save_settings(self):", 1)[1].split(
            "    def _speaker_display_name", 1
        )[0]

        self.assertNotIn('"hftoken"', save_settings)
        self.assertNotIn('self.hf_entry.get()', save_settings)

    def test_non_web_files_do_not_contain_common_mojibake_markers(self) -> None:
        checked_files = [
            Path(".gitignore"),
            Path("app.py"),
            Path("auto_transcribe_watch.py"),
            Path("glossary_utils.py"),
            Path("live_runtime.py"),
            Path("media_binaries.py"),
            Path("start_auto_transcribe_watch.bat"),
        ]
        markers = ["Рќ", "Рџ", "Р’", "Р°", "СЃ", "вљ", "вњ", "рџ"]

        offenders = []
        for path in checked_files:
            text = path.read_text(encoding="utf-8")
            if any(marker in text for marker in markers):
                offenders.append(str(path))

        self.assertEqual(offenders, [])


if __name__ == "__main__":
    unittest.main()
