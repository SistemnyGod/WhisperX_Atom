"""One-shot, secret-free llama.cpp inference probe for Server Doctor Release."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import urllib.request
from typing import Any

from .llama_subprocess import LocalLlamaRuntime, attest_llm_model


def _binary_version() -> str | None:
    binary = os.getenv("LLM_SERVER_BINARY", os.getenv("LLAMA_RUNTIME_BINARY", "/opt/llama/llama-server"))
    try:
        result = subprocess.run([binary, "--version"], capture_output=True, text=True, timeout=5, check=False)
    except (OSError, subprocess.SubprocessError):
        return None
    line = next((line.strip() for line in (result.stdout + "\n" + result.stderr).splitlines() if line.strip()), None)
    return line[:200] if line else None


def _probe(base_url: str, model: str) -> dict[str, Any]:
    # Exercise the same JSON-response contract used by Summary/Assistant,
    # rather than treating an arbitrary text completion as proof that the
    # production client can decode Qwen output.
    response_schema = {
        "type": "object",
        "required": ["answer"],
        "properties": {"answer": {"type": "string"}},
        "additionalProperties": False,
    }
    payload = json.dumps({
        "model": model,
        "messages": [{"role": "user", "content": "Верни JSON с полем answer и значением готов."}],
        "max_tokens": 8,
        "temperature": 0,
        "response_format": {"type": "json_object", "schema": response_schema},
        "stream": True,
    }).encode("utf-8")
    request = urllib.request.Request(
        base_url.rstrip("/") + "/chat/completions",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    started = time.perf_counter()
    first_token_ms: float | None = None
    chunks = 0
    content: list[str] = []
    with urllib.request.urlopen(request, timeout=120) as response:
        for raw in response:
            line = raw.decode("utf-8", errors="replace").strip()
            if not line.startswith("data:"):
                continue
            data = line[5:].strip()
            if data == "[DONE]":
                continue
            try:
                value = json.loads(data)
            except json.JSONDecodeError:
                continue
            choices = value.get("choices") if isinstance(value, dict) else None
            if isinstance(choices, list) and choices:
                delta = choices[0].get("delta") if isinstance(choices[0], dict) else None
                if isinstance(delta, dict) and (delta.get("content") or delta.get("reasoning_content")):
                    chunks += 1
                    token = delta.get("content")
                    if isinstance(token, str):
                        content.append(token)
                    if first_token_ms is None:
                        first_token_ms = (time.perf_counter() - started) * 1000
    decoded = None
    if content:
        try:
            decoded = json.loads("".join(content))
        except json.JSONDecodeError:
            decoded = None
    json_valid = isinstance(decoded, dict) and isinstance(decoded.get("answer"), str) and bool(decoded["answer"].strip())
    return {
        "status": "READY" if chunks > 0 and json_valid else "FAILED",
        "firstTokenMs": round(first_token_ms, 3) if first_token_ms is not None else None,
        "totalMs": round((time.perf_counter() - started) * 1000, 3),
        "chunks": chunks,
        "jsonValid": json_valid,
    }


def _resident_base_url() -> str | None:
    port = int(os.getenv("LLM_LOCAL_PORT", "18080"))
    base_url = f"http://127.0.0.1:{port}/v1"
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as response:
            return base_url if response.status == 200 else None
    except OSError:
        return None


def main() -> int:
    attestation = attest_llm_model()
    result: dict[str, Any] = {
        "schemaVersion": 1,
        "provider": "llama.cpp",
        "device": "CUDA",
        "model": attestation,
        "binaryVersion": _binary_version(),
        "checkedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    if not attestation.get("manifestValid"):
        result.update({"status": "MODEL_INVALID", "probe": None})
        print(json.dumps(result, ensure_ascii=False))
        return 2
    runtime = LocalLlamaRuntime()
    started_here = False
    try:
        started = time.perf_counter()
        # Reuse a resident worker-owned server when present. This keeps the
        # release probe non-disruptive and avoids starting a second GPU model.
        base_url = _resident_base_url()
        if base_url is None and getattr(runtime, "_server", None) is not None:
            base_url = runtime._server.base_url
        if base_url is None:
            server = runtime.ensure_started()
            base_url = server.base_url
            started_here = True
        result["modelLoadMs"] = round((time.perf_counter() - started) * 1000, 3)
        result["probe"] = _probe(base_url, os.getenv("LLM_MODEL_ALIAS", "qwen3-8b"))
        result["status"] = "READY" if result["probe"].get("status") == "READY" else "GENERATION_FAILED"
    except FileNotFoundError:
        result.update({"status": "MODEL_LOAD_FAILED", "probe": None})
    except Exception as exc:  # doctor output is intentionally stable and secret-free
        result.update({"status": "LLM_UNAVAILABLE", "probe": None, "error": type(exc).__name__})
    finally:
        if started_here:
            runtime.stop()
    print(json.dumps(result, ensure_ascii=False))
    return 0 if result["status"] == "READY" else 2


if __name__ == "__main__":
    sys.exit(main())
