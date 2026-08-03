from __future__ import annotations

import os
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path


class LocalLlamaServer:
    """Start llama.cpp only while the PostgreSQL GPU lease is held."""

    def __init__(self) -> None:
        self.model_path = Path(os.getenv("LLM_MODEL_PATH", "/models/qwen3-8b/Qwen3-8B-Q5_K_M.gguf"))
        self.alias = os.getenv("LLM_MODEL_ALIAS", "qwen3-8b")
        self.port = int(os.getenv("LLM_LOCAL_PORT", "18080"))
        self.start_timeout = max(30, int(os.getenv("LLM_START_TIMEOUT_SECONDS", "300")))
        self.gpu_layers = os.getenv("LLM_GPU_LAYERS", "99")
        self.context_size = os.getenv("LLM_CONTEXT_SIZE", "16384")
        self.process: subprocess.Popen[bytes] | None = None

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.port}/v1"

    def start(self) -> None:
        if not self.model_path.is_file():
            raise FileNotFoundError(f"llm_model_not_found:{self.model_path}")
        binary = os.getenv("LLM_SERVER_BINARY", "/opt/llama/llama-server")
        command = [
            binary,
            "--model", str(self.model_path),
            "--alias", self.alias,
            "--host", "127.0.0.1",
            "--port", str(self.port),
            "--ctx-size", str(self.context_size),
            "--n-gpu-layers", str(self.gpu_layers),
            "--flash-attn", "on",
            "--cache-type-k", "q8_0",
            "--cache-type-v", "q8_0",
            "--parallel", "1",
            "--jinja",
            "--reasoning", "off",
            "--chat-template-kwargs", '{"enable_thinking":false}',
        ]
        self.process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
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
            except (OSError, urllib.error.URLError):
                pass
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