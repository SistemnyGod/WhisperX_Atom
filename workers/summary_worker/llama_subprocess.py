from __future__ import annotations

import os
import hashlib
import json
import logging
import subprocess
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path


LOGGER = logging.getLogger("whisperx.summary.llama")
_ATTESTATION_LOCK = threading.RLock()
_ATTESTATION_KEY: tuple[str, int, int, str, int, str] | None = None
_ATTESTATION_VALUE: dict[str, object] | None = None


def resolve_llm_model_path() -> Path:
    """Resolve the one canonical model path used by worker and doctor.

    ``LLM_MODEL_PATH`` remains a backwards-compatible explicit override. New
    deployments may provide ``LLM_MODEL_ROOT``, ``LLM_MODEL_DIR`` and
    ``LLM_MODEL_FILE`` so Compose and Python cannot silently select different
    GGUF files.
    """
    explicit = os.getenv("LLM_MODEL_PATH", "").strip()
    if explicit:
        return Path(explicit)
    root = Path(os.getenv("LLM_MODEL_ROOT", "/models").strip() or "/models")
    directory = os.getenv("LLM_MODEL_DIR", "qwen3-8b").strip().strip("/\\") or "qwen3-8b"
    filename = os.getenv("LLM_MODEL_FILE", "Qwen3-8B-Q5_K_M.gguf").strip()
    return root / directory / filename


def resolve_llm_manifest_path(model_path: Path | None = None) -> Path:
    configured = os.getenv("LLM_MODEL_MANIFEST", "").strip()
    return Path(configured) if configured else Path(model_path or resolve_llm_model_path()).with_suffix(".gguf.manifest.json")


def attest_llm_model() -> dict[str, object]:
    """Return a secret-free model attestation for readiness and Doctor."""
    global _ATTESTATION_KEY, _ATTESTATION_VALUE
    path = resolve_llm_model_path()
    manifest_path = resolve_llm_manifest_path(path)
    expected = os.getenv("LLM_MODEL_SHA256", "").strip().lower()
    try:
        key = (
            str(path),
            int(path.stat().st_size),
            int(path.stat().st_mtime_ns),
            str(manifest_path),
            int(manifest_path.stat().st_mtime_ns),
            expected,
        )
    except OSError:
        key = (str(path), 0, 0, str(manifest_path), 0, expected)
    with _ATTESTATION_LOCK:
        if _ATTESTATION_KEY == key and _ATTESTATION_VALUE is not None:
            return dict(_ATTESTATION_VALUE)
    actual = ""
    size = 0
    try:
        size = path.stat().st_size
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        actual = digest.hexdigest().lower()
    except OSError:
        pass
    manifest: dict[str, object] = {}
    try:
        with manifest_path.open("r", encoding="utf-8") as stream:
            value = json.load(stream)
            if isinstance(value, dict):
                manifest = value
    except (OSError, ValueError, TypeError, json.JSONDecodeError):
        pass
    manifest_sha = str(manifest.get("sha256", "")).lower()
    expected_revision = os.getenv("LLM_MODEL_REVISION", "").strip()
    manifest_revision = str(manifest.get("revision", ""))
    hash_matches = bool(actual) and (not expected or actual == expected) and (not manifest_sha or actual == manifest_sha)
    revision_matches = not expected_revision or expected_revision.startswith("replace-with-") or manifest_revision == expected_revision
    try:
        manifest_schema = int(manifest.get("schemaVersion", 0) or 0)
        manifest_size = int(manifest.get("size", 0) or 0)
    except (TypeError, ValueError):
        manifest_schema = -1
        manifest_size = -1
    valid = bool(actual) and bool(manifest) and manifest_schema == 1 and hash_matches and revision_matches and manifest_size == size
    value = {
        "modelPath": str(path),
        "manifestPath": str(manifest_path),
        "modelAvailable": bool(actual),
        "modelSize": size,
        "expectedSha256Configured": bool(expected),
        "sha256": actual or None,
        "manifestSha256": manifest_sha or None,
        "revision": manifest_revision or None,
        "expectedRevision": expected_revision or None,
        "revisionMatches": revision_matches,
        "manifestValid": valid,
        "reason": "ready" if valid else ("model_missing" if not actual else "model_manifest_mismatch"),
    }
    with _ATTESTATION_LOCK:
        _ATTESTATION_KEY = key
        _ATTESTATION_VALUE = dict(value)
    return value


class LocalLlamaServer:
    """Managed llama.cpp subprocess used under the worker GPU lease."""

    def __init__(self) -> None:
        self.model_path = resolve_llm_model_path()
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

    def start(self, cancellation: threading.Event | None = None) -> None:
        if not self.model_path.is_file():
            raise FileNotFoundError(f"llm_model_not_found:{self.model_path}")
        if cancellation is not None and cancellation.is_set():
            raise RuntimeError("llm_warmup_cancelled")
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
            if cancellation is not None and cancellation.is_set():
                self.stop()
                raise RuntimeError("llm_warmup_cancelled")
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
    _last_probe: dict[str, object] = {"status": "NOT_RUN"}

    def __init__(self, idle_seconds: int | None = None) -> None:
        self.idle_seconds = max(60, idle_seconds or int(os.getenv("LLM_IDLE_UNLOAD_SECONDS", "900")))
        # Keep the safe stop-after-job behavior until the deployment enables
        # cross-worker GPU coordination. The resident path is opt-in so a
        # standalone summary container cannot starve the ASR worker.
        self.enabled = os.getenv("LLM_RESIDENT_ENABLED", "true").lower() in {"1", "true", "yes"}

    @staticmethod
    def _model_fingerprint() -> tuple[str, int, int, str, str, str]:
        path = resolve_llm_model_path()
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

    def ensure_started(self, cancellation: threading.Event | None = None) -> LocalLlamaServer:
        attestation = attest_llm_model()
        if not bool(attestation.get("manifestValid")):
            reason = str(attestation.get("reason") or "model_manifest_mismatch")
            raise RuntimeError(f"model_manifest_invalid:{reason}")
        fingerprint = self._model_fingerprint()
        runtime = type(self)
        with runtime._gate:
            process = runtime._server.process if runtime._server is not None else None
            if runtime._server is None or process is None or process.poll() is not None or runtime._fingerprint != fingerprint:
                if runtime._server is not None:
                    runtime._server.stop()
                server = LocalLlamaServer()
                server.start(cancellation)
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

    @property
    def last_probe(self) -> dict[str, object]:
        runtime = type(self)
        with runtime._gate:
            return dict(runtime._last_probe)

    def record_probe(self, status: str, *, total_ms: float | None = None, first_token_ms: float | None = None, error: str | None = None) -> None:
        runtime = type(self)
        with runtime._gate:
            value: dict[str, object] = {"status": status}
            if total_ms is not None and total_ms >= 0:
                value["totalMs"] = round(float(total_ms), 3)
            if first_token_ms is not None and first_token_ms >= 0:
                value["firstTokenMs"] = round(float(first_token_ms), 3)
            if error:
                value["error"] = error[:120]
            runtime._last_probe = value

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
