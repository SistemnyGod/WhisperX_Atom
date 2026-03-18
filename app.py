from __future__ import annotations

import json
import re
import subprocess
import threading
import time
from pathlib import Path
from queue import Empty, Queue
from typing import Any

import customtkinter as ctk
import torch
import whisperx
from tkinter import filedialog, messagebox
from whisperx.diarize import DiarizationPipeline as WhisperXDiarizationPipeline

SETTINGS_PATH = Path("whisperx_gui_settings.json")
RESULTS_DIR = Path("whisperx_results")


class TaskManager:
    def __init__(self, on_idle=None):
        self._queue: Queue[Any] = Queue()
        self._on_idle = on_idle
        self._current_id: int | None = None
        self._current_cancel: threading.Event | None = None
        self._next_id = 1
        self._running = True
        self._worker = threading.Thread(target=self._loop, daemon=True)
        self._worker.start()

    def submit(self, fn):
        job_id = self._next_id
        self._next_id += 1
        cancel = threading.Event()
        self._queue.put((job_id, fn, cancel))
        return job_id

    def cancel(self, job_id: int) -> bool:
        if self._current_id == job_id and self._current_cancel is not None:
            self._current_cancel.set()
            return True
        return False

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
            self._current_id = job_id
            self._current_cancel = cancel
            try:
                fn(cancel)
            except Exception:
                pass
            finally:
                self._current_id = None
                self._current_cancel = None
                self._queue.task_done()
                if self._on_idle:
                    self._on_idle(job_id)


class WhisperXApp:
    def __init__(self):
        ctk.set_appearance_mode("Dark")
        ctk.set_default_color_theme("blue")
        self.root = ctk.CTk()
        self.root.title("WhisperX GUI")
        self.root.geometry("1240x860")

        self.selected_file: str | None = None
        self.reference_file: str | None = None
        self.last_result: dict[str, Any] | None = None
        self.model_cache: dict[str, Any] = {}
        self.diarizer_cache: dict[str, Any] = {}
        self.temp_files: list[Path] = []
        self._active_job: int | None = None
        self.tm = TaskManager(on_idle=self._on_idle)

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

        left = ctk.CTkScrollableFrame(main, width=430)
        left.grid(row=0, column=0, sticky="nsew", padx=(0, 10))
        left.grid_columnconfigure(1, weight=1)

        right = ctk.CTkFrame(main)
        right.grid(row=0, column=1, sticky="nsew")
        right.grid_columnconfigure(0, weight=1)
        right.grid_rowconfigure(2, weight=1)

        r = 0
        self.file_label = ctk.CTkLabel(left, text="Файл совещания: не выбран", anchor="w")
        self.file_label.grid(row=r, column=0, sticky="ew", padx=8, pady=6)
        ctk.CTkButton(left, text="Обзор", command=self.select_file, width=100).grid(row=r, column=1, sticky="e", padx=8, pady=6)
        r += 1
        self.reference_file_label = ctk.CTkLabel(left, text="Файл представления: не выбран", anchor="w")
        self.reference_file_label.grid(row=r, column=0, sticky="ew", padx=8, pady=6)
        ctk.CTkButton(left, text="Обзор", command=self.select_reference_file, width=100).grid(
            row=r, column=1, sticky="e", padx=8, pady=6
        )
        r += 1

        self.model_var = ctk.StringVar(value="large-v3")
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

        def combo(lbl, vals, var):
            nonlocal r
            ctk.CTkLabel(left, text=lbl).grid(row=r, column=0, sticky="w", padx=8, pady=4)
            ctk.CTkComboBox(left, values=vals, variable=var, width=170).grid(row=r, column=1, sticky="e", padx=8, pady=4)
            r += 1

        combo("Model", ["tiny", "base", "small", "medium", "large-v2", "large-v3"], self.model_var)
        combo("Language", ["auto", "ru", "en", "de", "fr", "es", "it", "zh", "ja", "ko"], self.lang_var)
        combo("Device", ["auto", "cpu", "cuda"], self.device_var)
        combo("Compute", ["float32", "float16", "int8"], self.compute_var)
        combo("Batch", ["2", "4", "8", "16", "32"], self.batch_var)
        combo("Beam", ["5", "7", "10", "12"], self.beam_var)
        combo("VAD onset", ["0.35", "0.40", "0.45", "0.50"], self.vad_var)
        combo("Chunk", ["15", "20", "30"], self.chunk_var)
        combo("Min speakers", [str(i) for i in range(1, 31)], self.min_spk_var)
        combo("Max speakers", [str(i) for i in range(1, 31)], self.max_spk_var)

        ctk.CTkCheckBox(left, text="ASR preprocessing", variable=self.preprocess_var).grid(row=r, column=0, columnspan=2, sticky="w", padx=8, pady=4)
        r += 1
        ctk.CTkCheckBox(left, text="Enable diarization", variable=self.diarize_var).grid(row=r, column=0, columnspan=2, sticky="w", padx=8, pady=4)
        r += 1

        ctk.CTkLabel(left, text="HF token").grid(row=r, column=0, sticky="w", padx=8, pady=4)
        self.hf_entry = ctk.CTkEntry(left, show="*")
        self.hf_entry.grid(row=r, column=1, sticky="ew", padx=8, pady=4)
        r += 1

        ctk.CTkLabel(left, text="Порог совпадения голосов").grid(row=r, column=0, sticky="w", padx=8, pady=4)
        self.voice_match_threshold_var = ctk.StringVar(value="0.60")
        ctk.CTkComboBox(
            left,
            values=["0.45", "0.50", "0.55", "0.60", "0.65", "0.70"],
            variable=self.voice_match_threshold_var,
            width=170,
        ).grid(row=r, column=1, sticky="e", padx=8, pady=4)
        r += 1
        self.only_matched_var = ctk.BooleanVar(value=True)
        ctk.CTkCheckBox(left, text="Только совпавшие голоса из файла представления", variable=self.only_matched_var).grid(
            row=r, column=0, columnspan=2, sticky="w", padx=8, pady=4
        )
        r += 1
        ctk.CTkLabel(left, text="ФИО из файла представления (по одному на строке)").grid(
            row=r, column=0, columnspan=2, sticky="w", padx=8, pady=(8, 4)
        )
        r += 1
        self.reference_names = ctk.CTkTextbox(left, height=80, font=("Consolas", 10))
        self.reference_names.grid(row=r, column=0, columnspan=2, sticky="ew", padx=8, pady=4)
        r += 1

        ctk.CTkLabel(left, text="Initial prompt").grid(row=r, column=0, sticky="w", padx=8, pady=4)
        self.prompt_entry = ctk.CTkEntry(left)
        self.prompt_entry.grid(row=r, column=1, sticky="ew", padx=8, pady=4)
        r += 1

        ctk.CTkLabel(left, text="Hotwords").grid(row=r, column=0, sticky="w", padx=8, pady=4)
        self.hotwords_entry = ctk.CTkEntry(left)
        self.hotwords_entry.grid(row=r, column=1, sticky="ew", padx=8, pady=4)
        r += 1

        ctk.CTkLabel(left, text="Glossary replacements").grid(row=r, column=0, columnspan=2, sticky="w", padx=8, pady=(8, 4))
        r += 1
        self.glossary = ctk.CTkTextbox(left, height=120, font=("Consolas", 10))
        self.glossary.grid(row=r, column=0, columnspan=2, sticky="ew", padx=8, pady=4)

        bar = ctk.CTkFrame(right)
        bar.grid(row=0, column=0, sticky="ew", padx=10, pady=10)
        bar.grid_columnconfigure((0, 1, 2, 3), weight=1)
        self.install_btn = ctk.CTkButton(bar, text="Загрузить модель", command=self.start_install)
        self.run_btn = ctk.CTkButton(bar, text="Транскрибировать", command=self.start_transcribe)
        self.cancel_btn = ctk.CTkButton(bar, text="Отмена", command=self.cancel_job, state="disabled")
        self.export_btn = ctk.CTkButton(bar, text="Экспорт TXT", command=self.export_txt)
        self.install_btn.grid(row=0, column=0, padx=4, pady=8, sticky="ew")
        self.run_btn.grid(row=0, column=1, padx=4, pady=8, sticky="ew")
        self.cancel_btn.grid(row=0, column=2, padx=4, pady=8, sticky="ew")
        self.export_btn.grid(row=0, column=3, padx=4, pady=8, sticky="ew")

        self.progress = ctk.CTkProgressBar(right)
        self.progress.grid(row=1, column=0, sticky="ew", padx=10, pady=(0, 8))
        self.progress.set(0)

        tabs = ctk.CTkTabview(right)
        tabs.grid(row=2, column=0, sticky="nsew", padx=10, pady=(0, 10))
        tabs.add("Result")
        tabs.add("Logs")
        self.result = ctk.CTkTextbox(tabs.tab("Result"), font=("Consolas", 10))
        self.result.pack(fill="both", expand=True, padx=8, pady=8)
        self.logs = ctk.CTkTextbox(tabs.tab("Logs"), font=("Consolas", 10))
        self.logs.pack(fill="both", expand=True, padx=8, pady=8)

    def _on_idle(self, _job):
        self._active_job = None
        self.root.after(0, lambda: self._set_busy(False))

    def _on_close(self):
        self.tm.stop()
        self._save_settings()
        self.root.destroy()

    def _set_busy(self, busy: bool):
        st = "disabled" if busy else "normal"
        self.install_btn.configure(state=st)
        self.run_btn.configure(state=st)
        self.cancel_btn.configure(state="normal" if busy else "disabled")

    def log(self, msg: str):
        ts = time.strftime("%H:%M:%S")
        self.root.after(0, lambda: (self.logs.insert("end", f"[{ts}] {msg}\n"), self.logs.see("end")))

    def _cfg(self):
        dev = self.device_var.get()
        if dev == "auto":
            dev = "cuda" if torch.cuda.is_available() else "cpu"
        compute = self.compute_var.get()
        if dev == "cpu" and compute == "float16":
            compute = "float32"
        lang = None if self.lang_var.get() == "auto" else self.lang_var.get()
        mn, mx = int(self.min_spk_var.get()), int(self.max_spk_var.get())
        if mn > mx:
            mn, mx = mx, mn
        return {
            "device": dev,
            "compute": compute,
            "lang": lang,
            "mn": mn,
            "mx": mx,
            "match_threshold": float(self.voice_match_threshold_var.get()),
            "only_matched": bool(self.only_matched_var.get()),
        }

    def select_file(self):
        fp = filedialog.askopenfilename(filetypes=[("Audio", "*.wav *.mp3 *.m4a *.ogg *.flac"), ("All", "*.*")])
        if fp:
            self.selected_file = fp
            self.file_label.configure(text=Path(fp).name)

    def select_reference_file(self):
        fp = filedialog.askopenfilename(filetypes=[("Audio", "*.wav *.mp3 *.m4a *.ogg *.flac"), ("All", "*.*")])
        if fp:
            self.reference_file = fp
            self.reference_file_label.configure(text=Path(fp).name)

    def _parse_reference_names(self) -> list[str]:
        raw = self.reference_names.get("1.0", "end").strip()
        if not raw:
            return []
        return [ln.strip() for ln in raw.splitlines() if ln.strip()]

    def _cosine_similarity(self, left, right) -> float:
        import numpy as np

        a = np.asarray(left, dtype=np.float32)
        b = np.asarray(right, dtype=np.float32)
        denom = float(np.linalg.norm(a) * np.linalg.norm(b))
        if denom == 0.0:
            return -1.0
        return float(np.dot(a, b) / denom)

    def _build_reference_profiles(self, cancel: threading.Event, model, c: dict[str, Any]) -> dict[str, dict[str, Any]]:
        if not self.reference_file:
            return {}
        if not Path(self.reference_file).exists():
            self.log("Reference file not found, skipping voice matching")
            return {}
        if cancel.is_set():
            return {}

        self.log(f"Reference processing: {self.reference_file}")
        src = self.reference_file
        if self.preprocess_var.get():
            src = self._preprocess(src, diar=False)

        audio = whisperx.load_audio(src)
        try:
            ref_res = model.transcribe(audio, batch_size=int(self.batch_var.get()), word_timestamps=True)
        except TypeError:
            ref_res = model.transcribe(audio, batch_size=int(self.batch_var.get()))

        if ref_res.get("language"):
            try:
                am, md = whisperx.load_align_model(language_code=ref_res["language"], device=c["device"])
                al = whisperx.align(ref_res.get("segments", []), am, md, audio, c["device"], return_char_alignments=False)
                if isinstance(al, dict):
                    ref_res["segments"] = al.get("segments", ref_res.get("segments", []))
                    if "word_segments" in al:
                        ref_res["word_segments"] = al["word_segments"]
            except Exception as e:
                self.log(f"Reference alignment skipped: {e}")

        diar_audio = self._preprocess(self.reference_file, diar=True)
        diar_df, emb = self._diarizer()(diar_audio, min_speakers=c["mn"], max_speakers=c["mx"], return_embeddings=True)
        try:
            ref_res = whisperx.assign_word_speakers(diar_df, ref_res, emb)
        except Exception:
            pass

        ordered: list[str] = []
        for seg in ref_res.get("segments", []):
            sp = str(seg.get("speaker", "UNKNOWN"))
            if sp not in ordered:
                ordered.append(sp)
        if not ordered and emb:
            ordered = list(emb.keys())

        names = self._parse_reference_names()
        profiles: dict[str, dict[str, Any]] = {}
        for idx, sp in enumerate(ordered, start=1):
            if not emb or sp not in emb:
                continue
            name = names[idx - 1] if idx - 1 < len(names) else f"Спикер {idx}"
            profiles[sp] = {"name": name, "embedding": emb[sp]}
        return profiles

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
            mapping[main_sp] = {
                "name": reference_profiles[ref_sp]["name"],
                "score": score,
                "reference_speaker": ref_sp,
            }
            used_ref.add(ref_sp)
        return mapping

    def _model(self):
        c = self._cfg()
        key = json.dumps({"m": self.model_var.get(), **c, "beam": self.beam_var.get(), "vad": self.vad_var.get(), "chunk": self.chunk_var.get()})
        if key not in self.model_cache:
            self.model_cache[key] = whisperx.load_model(
                self.model_var.get(),
                device=c["device"],
                compute_type=c["compute"],
                language=c["lang"],
                asr_options={"beam_size": int(self.beam_var.get()), "initial_prompt": self.prompt_entry.get().strip() or None, "hotwords": self.hotwords_entry.get().strip() or None},
                vad_options={"vad_onset": float(self.vad_var.get()), "chunk_size": int(self.chunk_var.get())},
            )
        return self.model_cache[key]

    def _diarizer(self):
        c = self._cfg()
        token = self.hf_entry.get().strip()
        key = f"{c['device']}:{token}"
        if key not in self.diarizer_cache:
            self.diarizer_cache[key] = WhisperXDiarizationPipeline(use_auth_token=token, device=c["device"])
        return self.diarizer_cache[key]

    def _preprocess(self, path: str, diar=False):
        inp = Path(path)
        out = inp.parent / f"{inp.stem}{'.diar.wav' if diar else '.asr.wav'}"
        flt = "highpass=f=120,lowpass=f=7500,afftdn=nf=-25,acompressor=threshold=-28dB:ratio=4:attack=5:release=80" if diar else "highpass=f=70,lowpass=f=7600,afftdn=nf=-20,acompressor=threshold=-22dB:ratio=2:attack=5:release=60"
        cmd = ["ffmpeg", "-y", "-i", str(inp), "-ac", "1", "-ar", "16000", "-af", flt, str(out)]
        try:
            subprocess.run(cmd, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=300)
            self.temp_files.append(out)
            return str(out)
        except Exception:
            return path

    def start_install(self):
        if self.tm.busy():
            return
        self._active_job = self.tm.submit(self._install_worker)
        self._set_busy(True)

    def _install_worker(self, cancel: threading.Event):
        _ = cancel
        self.log("Loading model...")
        self._model()
        self.log("Model loaded")

    def start_transcribe(self):
        if not self.selected_file:
            messagebox.showerror("Error", "Select audio file first")
            return
        if self.tm.busy():
            return
        self._save_settings()
        self._active_job = self.tm.submit(self._transcribe_worker)
        self._set_busy(True)
        self.progress.set(0.05)

    def cancel_job(self):
        if self._active_job is not None:
            self.tm.cancel(self._active_job)

    def _transcribe_worker(self, cancel: threading.Event):
        c = self._cfg()
        model = self._model()
        src = self.selected_file or ""
        if self.preprocess_var.get():
            src = self._preprocess(src, diar=False)
        audio = whisperx.load_audio(src)
        self.progress.set(0.2)
        if cancel.is_set():
            return
        try:
            res = model.transcribe(audio, batch_size=int(self.batch_var.get()), word_timestamps=True)
        except TypeError:
            res = model.transcribe(audio, batch_size=int(self.batch_var.get()))
        self.progress.set(0.45)
        if cancel.is_set():
            return

        if res.get("language"):
            try:
                am, md = whisperx.load_align_model(language_code=res["language"], device=c["device"])
                al = whisperx.align(res.get("segments", []), am, md, audio, c["device"], return_char_alignments=False)
                if isinstance(al, dict):
                    res["segments"] = al.get("segments", res.get("segments", []))
                    if "word_segments" in al:
                        res["word_segments"] = al["word_segments"]
            except Exception as e:
                self.log(f"Alignment skipped: {e}")

        speaker_mapping: dict[str, dict[str, Any]] = {}
        if self.diarize_var.get():
            token = self.hf_entry.get().strip()
            if not token:
                raise RuntimeError("HF token is required for diarization")
            diar_audio = self._preprocess(self.selected_file or "", diar=True)
            diar_df, emb = self._diarizer()(diar_audio, min_speakers=c["mn"], max_speakers=c["mx"], return_embeddings=True)
            try:
                res = whisperx.assign_word_speakers(diar_df, res, emb)
            except Exception as e:
                self.log(f"assign_word_speakers failed: {e}")

            if self.reference_file:
                reference_profiles = self._build_reference_profiles(cancel, model, c)
                speaker_mapping = self._match_speakers(emb or {}, reference_profiles, c["match_threshold"])
                if speaker_mapping:
                    self.log(
                        "Reference matches: "
                        + ", ".join(
                            f"{main} -> {m['name']} ({m['score']:.2f})"
                            for main, m in speaker_mapping.items()
                        )
                    )
                else:
                    self.log("No speaker matches with reference file")
        self.progress.set(0.8)

        rules = []
        for ln in self.glossary.get("1.0", "end").splitlines():
            if "=>" in ln:
                a, b = [x.strip() for x in ln.split("=>", 1)]
                if a:
                    pt = a[3:].strip() if a.startswith("re:") else re.escape(a)
                    try:
                        rules.append((re.compile(pt, flags=re.IGNORECASE), b))
                    except re.error:
                        pass
        for seg in res.get("segments", []):
            txt = str(seg.get("text", ""))
            for pt, repl in rules:
                txt = pt.sub(repl, txt)
            seg["text"] = re.sub(r"\s{2,}", " ", txt).strip()

        if speaker_mapping:
            for seg in res.get("segments", []):
                sp = str(seg.get("speaker", "UNKNOWN"))
                if sp in speaker_mapping:
                    seg["speaker_original"] = sp
                    seg["speaker"] = speaker_mapping[sp]["name"]
                    seg["speaker_similarity"] = round(float(speaker_mapping[sp]["score"]), 4)
            for word in res.get("word_segments", []):
                sp = str(word.get("speaker", "UNKNOWN"))
                if sp in speaker_mapping:
                    word["speaker_original"] = sp
                    word["speaker"] = speaker_mapping[sp]["name"]
                    word["speaker_similarity"] = round(float(speaker_mapping[sp]["score"]), 4)
            res["reference_speaker_matches"] = {
                sp: {
                    "name": match["name"],
                    "score": round(float(match["score"]), 4),
                    "reference_speaker": match["reference_speaker"],
                }
                for sp, match in speaker_mapping.items()
            }

        if self.reference_file and c["only_matched"]:
            matched_names = {m["name"] for m in speaker_mapping.values()}
            res["segments"] = [seg for seg in res.get("segments", []) if str(seg.get("speaker", "")) in matched_names]
            if "word_segments" in res:
                res["word_segments"] = [
                    w for w in res.get("word_segments", []) if str(w.get("speaker", "")) in matched_names
                ]

        res["text"] = " ".join(seg.get("text", "").strip() for seg in res.get("segments", []) if seg.get("text"))

        RESULTS_DIR.mkdir(exist_ok=True)
        out = RESULTS_DIR / f"{Path(self.selected_file or 'result').stem}_{time.strftime('%Y%m%d_%H%M%S')}.json"
        out.write_text(json.dumps(res, ensure_ascii=False, indent=2), encoding="utf-8")
        self.last_result = res

        lines = [f"Language: {res.get('language', 'unknown')} | Segments: {len(res.get('segments', []))}", "=" * 60]
        for s in res.get("segments", []):
            lines.append(f"[{float(s.get('start',0)):07.2f}-{float(s.get('end',0)):07.2f}] {s.get('speaker','UNKNOWN')}: {s.get('text','').strip()}")
        txt = "\n".join(lines) + "\n"
        self.root.after(0, lambda: (self.result.delete("1.0", "end"), self.result.insert("end", txt)))
        self.log(f"Saved: {out}")
        self.progress.set(1.0)

    def export_txt(self):
        if not self.last_result:
            messagebox.showerror("Error", "No result")
            return
        fp = filedialog.asksaveasfilename(defaultextension=".txt", filetypes=[("Text", "*.txt")])
        if not fp:
            return
        Path(fp).write_text(self.result.get("1.0", "end"), encoding="utf-8")
        self.log(f"TXT exported: {fp}")

    def _load_settings(self):
        if not SETTINGS_PATH.exists():
            return
        try:
            s = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
        except Exception:
            return
        self.model_var.set(str(s.get("last_model") or s.get("model") or self.model_var.get()))
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
        self.only_matched_var.set(bool(s.get("only_matched_reference", self.only_matched_var.get())))
        self.voice_match_threshold_var.set(str(s.get("voice_match_threshold") or self.voice_match_threshold_var.get()))
        self.hf_entry.insert(0, str(s.get("hf_token") or s.get("hftoken") or ""))
        self.prompt_entry.insert(0, str(s.get("initial_prompt") or ""))
        self.hotwords_entry.insert(0, str(s.get("hotwords") or ""))
        self.glossary.insert("1.0", str(s.get("glossary_replacements") or ""))
        self.reference_names.insert("1.0", str(s.get("reference_names") or ""))
        self.reference_file = str(s.get("reference_file") or "").strip() or None
        if self.reference_file:
            self.reference_file_label.configure(text=Path(self.reference_file).name)

    def _save_settings(self):
        p = {
            "last_model": self.model_var.get(),
            "model": self.model_var.get(),
            "last_language": self.lang_var.get(),
            "lang": self.lang_var.get(),
            "device": self.device_var.get(),
            "compute_type": self.compute_var.get(),
            "compute": self.compute_var.get(),
            "batch_size": int(self.batch_var.get()),
            "batch": int(self.batch_var.get()),
            "beam_size": int(self.beam_var.get()),
            "vad_onset": float(self.vad_var.get()),
            "chunk_size": int(self.chunk_var.get()),
            "min_speakers": int(self.min_spk_var.get()),
            "mins": int(self.min_spk_var.get()),
            "max_speakers": int(self.max_spk_var.get()),
            "maxs": int(self.max_spk_var.get()),
            "preprocess_asr": bool(self.preprocess_var.get()),
            "only_matched_reference": bool(self.only_matched_var.get()),
            "voice_match_threshold": float(self.voice_match_threshold_var.get()),
            "hf_token": self.hf_entry.get(),
            "hftoken": self.hf_entry.get(),
            "initial_prompt": self.prompt_entry.get(),
            "hotwords": self.hotwords_entry.get(),
            "glossary_replacements": self.glossary.get("1.0", "end").strip(),
            "reference_names": self.reference_names.get("1.0", "end").strip(),
            "reference_file": self.reference_file or "",
        }
        SETTINGS_PATH.write_text(json.dumps(p, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    WhisperXApp().root.mainloop()
