from __future__ import annotations

import os
import time
import wave
from pathlib import Path
from typing import Any


class SileroRuntime:
    def __init__(self, model_path: Path, temp_root: Path, cpu_threads: int = 4) -> None:
        self.model_path = model_path.resolve()
        self.temp_root = temp_root.resolve()
        self.cpu_threads = max(1, min(cpu_threads, 32))
        self.model: Any | None = None
        self.torch: Any | None = None
        self.model_load_ms = 0

    def load(self) -> None:
        os.environ["CUDA_VISIBLE_DEVICES"] = ""
        started = time.perf_counter()
        import torch

        torch.set_num_threads(self.cpu_threads)
        importer = torch.package.PackageImporter(str(self.model_path))
        model = importer.load_pickle("tts_models", "model")
        # Silero's packaged TTSModelMultiAcc_v3 mutates itself and returns
        # None from ``to`` (unlike a regular torch.nn.Module). Preserve the
        # original object while still accepting conventional return values.
        moved = model.to(torch.device("cpu"))
        if moved is not None:
            model = moved
        if hasattr(model, "eval"):
            model.eval()
        self.torch = torch
        self.model = model
        # Warm-up is deliberately short and deterministic; it avoids first
        # response latency without writing any user text to logs.
        model.apply_tts(text="Готово", speaker="aidar", sample_rate=24000)
        self.model_load_ms = int((time.perf_counter() - started) * 1000)
        self.temp_root.mkdir(parents=True, exist_ok=True)

    def synthesize(self, text: str, speaker: str, sample_rate: int, request_id: str) -> dict[str, object]:
        if self.model is None:
            self.load()
        started = time.perf_counter()
        audio = self.model.apply_tts(text=text, speaker=speaker, sample_rate=sample_rate)
        target = self.temp_root / f"tts-{request_id}.wav"
        # apply_tts returns a CPU tensor/list. Write PCM16 WAV ourselves so
        # the parent process controls playback and can delete the file.
        values = audio.detach().cpu().numpy() if hasattr(audio, "detach") else audio
        import numpy as np

        samples = np.asarray(values, dtype=np.float32)
        samples = np.clip(samples, -1.0, 1.0)
        pcm = (samples * 32767.0).astype(np.int16).tobytes()
        with wave.open(str(target), "wb") as output:
            output.setnchannels(1)
            output.setsampwidth(2)
            output.setframerate(sample_rate)
            output.writeframes(pcm)
        duration_ms = int(len(samples) * 1000 / sample_rate)
        return {
            "audioPath": str(target),
            "durationMs": duration_ms,
            "synthesisMs": int((time.perf_counter() - started) * 1000),
            "modelLoadMs": self.model_load_ms,
        }
