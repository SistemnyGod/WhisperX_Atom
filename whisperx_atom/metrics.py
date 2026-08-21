"""Per-request performance diagnostics for the Core Pipeline.

Metrics are advisory metadata.  They contain timings and resource counters,
never transcript text, audio bytes, credentials or absolute host paths.
"""

from __future__ import annotations

import contextlib
import os
import subprocess
import threading
import time
from dataclasses import dataclass, field
from typing import Iterator


_STAGE_KEYS = {
    "model_load": "model_load_ms",
    "queue_wait": "queue_wait_ms",
    "normalize": "normalize_ms",
    "asr": "asr_ms",
    "alignment": "alignment_ms",
    "diarization": "diarization_ms",
    "postprocess": "postprocess_ms",
    # Media Worker measures this before the GPU job starts.  It is copied into
    # the request collector rather than re-measured in the GPU container.
    "media_prepare": "media_prepare_ms",
}


def _peak_vram_bytes() -> int | None:
    try:
        import torch

        if not torch.cuda.is_available():
            return None
        return int(torch.cuda.max_memory_allocated())
    except Exception:
        return None


@dataclass(frozen=True)
class GpuTelemetrySample:
    utilization: float | None = None
    sampled_at: float | None = None
    probe_ms: float | None = None
    source: str = "NONE"


class _GpuTelemetrySampler:
    """One low-frequency sampler shared by all request metrics.

    Calling nvidia-smi in the request completion path made telemetry itself
    add up to 750 ms to every job. The sampler keeps that cost off the worker's
    critical path and exposes the sample age for honest diagnostics.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._sample = GpuTelemetrySample()
        self._started = False
        self._interval = max(0.5, float(os.getenv("GPU_METRICS_SAMPLE_INTERVAL_SECONDS", "2")))

    def start(self) -> None:
        with self._lock:
            if self._started:
                return
            self._started = True
        thread = threading.Thread(target=self._run, name="whisperx-gpu-telemetry", daemon=True)
        thread.start()

    def snapshot(self) -> GpuTelemetrySample:
        self.start()
        with self._lock:
            return self._sample

    def _run(self) -> None:
        while True:
            started = time.perf_counter()
            utilization: float | None = None
            source = "NONE"
            try:
                import pynvml  # type: ignore[import-not-found]

                pynvml.nvmlInit()
                try:
                    handle = pynvml.nvmlDeviceGetHandleByIndex(0)
                    utilization = round(float(pynvml.nvmlDeviceGetUtilizationRates(handle).gpu), 3)
                    source = "NVML"
                finally:
                    try:
                        pynvml.nvmlShutdown()
                    except Exception:
                        pass
            except Exception:
                try:
                    completed = subprocess.run(
                        ["nvidia-smi", "--query-gpu=utilization.gpu", "--format=csv,noheader,nounits"],
                        check=True,
                        capture_output=True,
                        text=True,
                        timeout=float(os.getenv("GPU_METRICS_TIMEOUT_SECONDS", "0.75")),
                    )
                    value = float((completed.stdout or "").strip().splitlines()[0])
                    if 0 <= value <= 100:
                        utilization = round(value, 3)
                        source = "NVIDIA_SMI"
                except (OSError, ValueError, IndexError, subprocess.SubprocessError):
                    pass
            sample = GpuTelemetrySample(utilization, time.time(), round((time.perf_counter() - started) * 1000.0, 3), source)
            with self._lock:
                self._sample = sample
            time.sleep(self._interval)


_GPU_SAMPLER = _GpuTelemetrySampler()


def _gpu_utilization_snapshot() -> GpuTelemetrySample:
    return _GPU_SAMPLER.snapshot()


def _gpu_utilization_percent() -> float | None:
    """Compatibility accessor for older diagnostics callers."""
    return _gpu_utilization_snapshot().utilization


@dataclass
class PipelineMetrics:
    """Mutable collector scoped to one ASR or enrichment request."""

    queue_wait_ms: float = 0.0
    media_prepare_ms: float | None = None
    model_load_ms: float = 0.0
    values: dict[str, float] = field(default_factory=dict)
    checkpoint_lookups: int = 0
    checkpoint_hits: int = 0
    cuda_oom_count: int = 0
    started_at: float = field(default_factory=time.perf_counter)

    def __post_init__(self) -> None:
        for key in ("normalize_ms", "asr_ms", "alignment_ms", "diarization_ms", "postprocess_ms"):
            self.values.setdefault(key, 0.0)
        self.values["media_prepare_ms"] = max(0.0, float(self.media_prepare_ms or 0.0))
        try:
            import torch

            if torch.cuda.is_available():
                torch.cuda.reset_peak_memory_stats()
        except Exception:
            # Metrics must never prevent a transcript from being processed.
            pass

    @contextlib.contextmanager
    def measure(self, stage: str) -> Iterator[None]:
        started = time.perf_counter()
        try:
            yield
        except Exception as exc:
            self.record_exception(exc)
            raise
        finally:
            key = _STAGE_KEYS.get(stage, f"{stage}_ms")
            self.values[key] = round(self.values.get(key, 0.0) + (time.perf_counter() - started) * 1000.0, 3)

    def record_exception(self, exc: BaseException) -> None:
        text = f"{type(exc).__name__}: {exc}".lower()
        if "cuda" in text and ("out of memory" in text or "oom" in text):
            self.cuda_oom_count += 1

    def checkpoint(self, hit: bool) -> None:
        self.checkpoint_lookups += 1
        if hit:
            self.checkpoint_hits += 1

    def to_dict(self, duration_seconds: float | None = None) -> dict[str, object]:
        audio_duration_ms = round(float(duration_seconds) * 1000.0, 3) if duration_seconds and duration_seconds > 0 else None
        peak_bytes = _peak_vram_bytes()
        peak_mb = round(peak_bytes / (1024.0 * 1024.0), 3) if peak_bytes is not None else None
        gpu_sample = _gpu_utilization_snapshot()
        sample_age_ms = round((time.time() - gpu_sample.sampled_at) * 1000.0, 3) if gpu_sample.sampled_at else None
        # Measure after the resource probes so telemetry collection is
        # visible in total_processing_ms instead of being silently omitted.
        total_ms = (time.perf_counter() - self.started_at) * 1000.0
        metrics: dict[str, object] = {
            "model_load_ms": round(float(self.model_load_ms), 3),
            "queue_wait_ms": round(float(self.queue_wait_ms), 3),
            **{key: round(float(value), 3) for key, value in self.values.items() if key not in {"model_load_ms", "queue_wait_ms"}},
            # Keep total_ms for existing diagnostics and expose the explicit
            # name used by the performance contract.
            "total_processing_ms": round(total_ms, 3),
            "total_ms": round(total_ms, 3),
            "audio_duration_ms": audio_duration_ms,
            "peak_vram_bytes": peak_bytes,
            "gpu_peak_vram_mb": peak_mb,
            "gpu_utilization": gpu_sample.utilization,
            "gpu_sample_source": gpu_sample.source,
            "gpu_sample_age_ms": sample_age_ms,
            "gpu_probe_ms": gpu_sample.probe_ms,
            "cuda_oom_count": int(self.cuda_oom_count),
            "checkpoint_lookups": int(self.checkpoint_lookups),
            "checkpoint_hits": int(self.checkpoint_hits),
            "checkpoint_hit": bool(self.checkpoint_hits),
            "checkpoint_hit_rate": round(self.checkpoint_hits / self.checkpoint_lookups, 4) if self.checkpoint_lookups else None,
        }
        if audio_duration_ms:
            # RTF is wall-clock request time / media duration; values below 1
            # indicate faster-than-real-time processing.
            rtf = round(total_ms / audio_duration_ms, 4)
            metrics["rtf"] = rtf
            # Upper-case alias keeps exported metric names aligned with the
            # operator-facing performance table without breaking old clients.
            metrics["RTF"] = rtf
        else:
            metrics["rtf"] = None
            metrics["RTF"] = None
        return metrics
