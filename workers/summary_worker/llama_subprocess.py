from __future__ import annotations

import os
import logging
import subprocess
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path


LOGGER = logging.getLogger("whisperx.summary.llama")


class LocalLlamaServer:
    """Managed llama.cpp subprocess used under the worker GPU lease."""

    def __init__(self) -> None:
        self.model_path = Path(os.getenv("LLM_MODEL_PATH", "/models/qwen3-8b/Qwen3-8B-Q5_K_M.gguf"))
        self.alias = os.getenv("LLM_MODEL_ALIAS", "qwen3-8b")
        self.port = int(os.getenv("LLM_LOCAL_PORT", "18080"))
        self.start_timeout = max(30, int(os.getenv("LLM_START_TIMEOUT_SECONDS", "300")))
        self.gpu_layers = os.getenv("LLM_GPU_LAYERS", "99")
        self.require_gpu = os.getenv("LLM_REQUIRE_GPU", "true").lower() in {"1", "true", "yes"}
        self.context_size = os.getenv("LLM_CONTEXT_SIZE", "16384")
        self.process: subprocess.Popen[bytes] | None = None

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.port}/v1"

    def start(self) -> None:
        if not self.model_path.is_file():
            raise FileNotFoundError(f"llm_model_not_found:{self.model_path}")
        try:
            gpu_layers = int(self.gpu_layers)
        except ValueError as exc:
            raise RuntimeError(f"invalid_llm_gpu_layers:{self.gpu_layers}") from exc
        if self.require_gpu and gpu_layers <= 0:
            raise RuntimeError("llm_gpu_required_but_no_layers_configured")
        binary = os.getenv("LLM_SERVER_BINARY", "/opt/llama/llama-server")
        command = [
            binary,
            "--model", str(self.model_path),
            "--alias", self.alias,
            "--host", "127.0.0.1",
            "--port", str(self.port),
            "--ctx-size", str(self.context_size),
            "--n-gpu-layers", str(gpu_layers),
            "--flash-attn", "on",
            "--cache-type-k", "q8_0",
            "--cache-type-v", "q8_0",
            "--parallel", "1",
            "--jinja",
            "--reasoning", "off",
            "--chat-template-kwargs", '{"enable_thinking":false}',
        ]
        # Keep llama.cpp diagnostics in the worker/container log. Suppressing
        # both streams made model/flag failures indistinguishable from a
        # generic startup timeout.
        self.process = subprocess.Popen(command)
        deadline = time.monotonic() + self.start_timeout
        health_url = f"http://127.0.0.1:{self.port}/health"
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                code = self.process.returncode
                self.process = None
                raise RuntimeError(f"llm_server_exit:{code}")
            try:
                with urllib.request.urlopen(health_url, timeout=2) as response:
                    if response.status == 200:
                        return
            except (OSError, urllib.error.URLError) as exc:
                LOGGER.debug("llama_health_probe_failed: %s", exc)
            time.sleep(1)
        self.stop()
        raise TimeoutError("llm_server_start_timeout")

    def stop(self) -> None:
        process = self.process
        self.process = None
        if process is None:
            return
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)

    def __enter__(self) -> "LocalLlamaServer":
        self.start()
        return self

    def __exit__(self, *_: object) -> None:
        self.stop()


class LocalLlamaRuntime:
    """Process-wide resident llama-server with bounded idle unloading.

    Summary and Assistant workers live in the same GPU worker process. They
    share this runtime, while their PostgreSQL GPU leases still serialize
    requests. Keeping the process resident avoids reloading Qwen for every
    block/job and unloading after inactivity leaves the GPU available to ASR.
    """

    _gate = threading.RLock()
    _server: LocalLlamaServer | None = None
    _fingerprint: tuple[str, int, int, str, str, str] | None = None
    _last_used = 0.0

    def __init__(self, idle_seconds: int | None = None) -> None:
        self.idle_seconds = max(60, idle_seconds or int(os.getenv("LLM_IDLE_UNLOAD_SECONDS", "900")))
        # Keep the safe stop-after-job behavior until the deployment enables
        # cross-worker GPU coordination. The resident path is opt-in so a
        # standalone summary container cannot starve the ASR worker.
        self.enabled = os.getenv("LLM_RESIDENT_ENABLED", "false").lower() in {"1", "true", "yes"}

    @staticmethod
    def _model_fingerprint() -> tuple[str, int, int, str, str, str]:
        path = Path(os.getenv("LLM_MODEL_PATH", "/models/qwen3-8b/Qwen3-8B-Q5_K_M.gguf"))
        try:
            stat = path.stat()
            return (
                str(path.resolve()), int(stat.st_size), int(stat.st_mtime_ns),
                os.getenv("LLM_MODEL_ALIAS", "qwen3-8b"),
                os.getenv("LLM_GPU_LAYERS", "99"),
                os.getenv("LLM_CONTEXT_SIZE", "16384"),
            )
        except OSError:
            return (
                str(path), 0, 0,
                os.getenv("LLM_MODEL_ALIAS", "qwen3-8b"),
                os.getenv("LLM_GPU_LAYERS", "99"),
                os.getenv("LLM_CONTEXT_SIZE", "16384"),
            )

    def ensure_started(self) -> LocalLlamaServer:
        fingerprint = self._model_fingerprint()
        runtime = type(self)
        with runtime._gate:
            process = runtime._server.process if runtime._server is not None else None
            if runtime._server is None or process is None or process.poll() is not None or runtime._fingerprint != fingerprint:
                if runtime._server is not None:
                    runtime._server.stop()
                server = LocalLlamaServer()
                server.start()
                runtime._server = server
                runtime._fingerprint = fingerprint
            runtime._last_used = time.monotonic()
            return runtime._server

    def touch(self) -> None:
        runtime = type(self)
        with runtime._gate:
            runtime._last_used = time.monotonic()

    def release_after_job(self) -> None:
        if self.enabled:
            self.touch()
        else:
            self.stop()

    @property
    def state(self) -> str:
        runtime = type(self)
        with runtime._gate:
            if runtime._server is None:
                return "STOPPED"
            process = runtime._server.process
            if process is None or process.poll() is not None:
                return "FAILED"
            return "READY"

    def release_idle(self) -> None:
        runtime = type(self)
        with runtime._gate:
            if runtime._server is not None and runtime._last_used and time.monotonic() - runtime._last_used >= self.idle_seconds:
                runtime._server.stop()
                runtime._server = None
                runtime._fingerprint = None
                runtime._last_used = 0.0

    def stop(self) -> None:
        runtime = type(self)
        with runtime._gate:
            if runtime._server is not None:
                runtime._server.stop()
            runtime._server = None
            runtime._fingerprint = None
            runtime._last_used = 0.0
