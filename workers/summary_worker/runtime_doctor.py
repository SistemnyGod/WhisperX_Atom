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


def _decode_json_document(value: str) -> Any:
    """Decode a model JSON response without accepting arbitrary prose.

    llama.cpp may return a fenced JSON document or a short preamble even when
    ``response_format`` is requested.  The production clients already apply
    the same bounded extraction before schema validation; the doctor must
    measure that path rather than fail on harmless transport decoration.
    """

    text = value.strip()
    if text.startswith("```"):
        lines = text.splitlines()
        if lines and lines[0].lstrip().startswith("```"):
            lines = lines[1:]
        if lines and lines[-1].strip() == "```":
            lines = lines[:-1]
        text = "\n".join(lines).strip()
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        start = text.find("{")
        end = text.rfind("}")
        if start < 0 or end <= start:
            return None
        try:
            return json.loads(text[start : end + 1])
        except json.JSONDecodeError:
            return None


def _canonical_segment_id(value: object) -> str:
    text = str(value).strip()
    return text if text.upper().startswith("SEG-") else f"SEG-{text}"


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
    decoded = _decode_json_document("".join(content)) if content else None
    json_valid = isinstance(decoded, dict) and isinstance(decoded.get("answer"), str) and bool(decoded["answer"].strip())
    return {
        "status": "READY" if chunks > 0 and json_valid else "FAILED",
        "firstTokenMs": round(first_token_ms, 3) if first_token_ms is not None else None,
        "totalMs": round((time.perf_counter() - started) * 1000, 3),
        "chunks": chunks,
        "jsonValid": json_valid,
    }


def _contract_probe(base_url: str, model: str, kind: str) -> dict[str, Any]:
    """Run a secret-free production-shaped Assistant/Summary contract probe.

    Response text is consumed only in memory and is intentionally omitted from
    the returned doctor artifact.
    """
    if kind == "ASSISTANT_GROUNDING":
        prompt = (
            "Верни только JSON с answer, voice_answer, evidence_segment_ids и claims. "
            "Ответь кто отвечает за ремонт. Единственный evidence: SEG-1: "
            "Ответственным за ремонт назначен Иванов. Нельзя добавлять другие имена, даты или числа."
        )
        required = {"answer", "voice_answer", "evidence_segment_ids", "claims"}
        expected_ids = {"SEG-1"}
    else:
        prompt = (
            "Верни только JSON профиля MEETING_PROTOCOL_RU с questions_and_decisions и tasks. "
            "Используй только SEG-1..SEG-4. SEG-1: Обсудили ремонт второй печи. "
            "SEG-2: Решили перенести ремонт на 30 августа. "
            "SEG-3: Ответственным назначили Иванова. "
            "SEG-4: Иванову поручили подготовить ведомость до 28 августа."
        )
        required = {"questions_and_decisions", "tasks"}
        expected_ids = {"SEG-1", "SEG-2", "SEG-3", "SEG-4"}
    # Use the exact additive schemas consumed by the production Assistant and
    # MEETING_PROTOCOL_RU paths.  A shallow ``required`` list made the probe
    # green for malformed protocol rows that could never pass content gates.
    if kind == "ASSISTANT_GROUNDING":
        from .assistant import ASSISTANT_SCHEMA

        schema = ASSISTANT_SCHEMA
    else:
        from .contracts import MEETING_PROTOCOL_RU_SCHEMA

        schema = MEETING_PROTOCOL_RU_SCHEMA
    payload = json.dumps({
        "model": model,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": 400,
        "temperature": 0,
        "response_format": {"type": "json_object", "schema": schema},
        "stream": True,
    }).encode("utf-8")
    request = urllib.request.Request(base_url.rstrip("/") + "/chat/completions", data=payload, headers={"Content-Type": "application/json"}, method="POST")
    started = time.perf_counter()
    first_token_ms: float | None = None
    chunks = 0
    content: list[str] = []
    try:
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
                    token = delta.get("content") if isinstance(delta, dict) else None
                    if token:
                        chunks += 1
                        content.append(str(token))
                        if first_token_ms is None:
                            first_token_ms = (time.perf_counter() - started) * 1000
        decoded = _decode_json_document("".join(content)) if content else None
        valid = isinstance(decoded, dict) and required.issubset(decoded)
        ids: set[str] = set()
        if isinstance(decoded, dict):
            for key in ("evidence_segment_ids", "evidenceIds"):
                ids.update(_canonical_segment_id(item) for item in decoded.get(key, []) if item)
            for collection in ("claims", "tasks", "questions_and_decisions"):
                for item in decoded.get(collection, []) if isinstance(decoded.get(collection), list) else []:
                    if isinstance(item, dict):
                        ids.update(
                            _canonical_segment_id(value)
                            for value in item.get("evidence_segment_ids", item.get("evidenceIds", []))
                            if value
                        )
        if kind == "ASSISTANT_GROUNDING":
            valid = valid and ids and ids == {_canonical_segment_id(item) for item in expected_ids}
        else:
            valid = valid and ids and ids == {_canonical_segment_id(item) for item in expected_ids}
        return {"status": "READY" if valid and chunks else "FAILED", "firstTokenMs": round(first_token_ms, 3) if first_token_ms is not None else None, "totalMs": round((time.perf_counter() - started) * 1000, 3), "chunks": chunks}
    except Exception as exc:
        return {"status": "FAILED", "firstTokenMs": None, "totalMs": round((time.perf_counter() - started) * 1000, 3), "chunks": chunks, "error": type(exc).__name__}


def _resident_base_url() -> str | None:
    port = int(os.getenv("LLM_LOCAL_PORT", "18080"))
    base_url = f"http://127.0.0.1:{port}/v1"
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as response:
            return base_url if response.status == 200 else None
    except OSError:
        return None


def _failed_contract_probes(error_code: str) -> dict[str, dict[str, Any]]:
    """Keep the v2 probe surface stable even when model setup fails."""
    return {
        name: {"status": "FAILED", "error": error_code, "firstTokenMs": None, "totalMs": None, "chunks": 0}
        for name in ("LLM_RUNTIME", "ASSISTANT_JSON", "ASSISTANT_GROUNDING", "SUMMARY_JSON", "MEETING_PROTOCOL_RU")
    }


def main() -> int:
    attestation = attest_llm_model()
    result: dict[str, Any] = {
        "schemaVersion": 1,
        "doctorVersion": 2,
        "provider": "llama.cpp",
        "device": "CUDA",
        "model": attestation,
        "binaryVersion": _binary_version(),
        "checkedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    if not attestation.get("manifestValid"):
        result.update({"status": "MODEL_INVALID", "probe": None, "probes": _failed_contract_probes("MODEL_INVALID")})
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
        model_alias = os.getenv("LLM_MODEL_ALIAS", "qwen3-8b")
        result["probe"] = _probe(base_url, model_alias)
        result["probes"] = {
            "LLM_RUNTIME": {"status": result["probe"].get("status"), "timings": {"firstTokenMs": result["probe"].get("firstTokenMs"), "totalMs": result["probe"].get("totalMs")}},
            "ASSISTANT_JSON": _contract_probe(base_url, model_alias, "ASSISTANT_GROUNDING"),
            "ASSISTANT_GROUNDING": _contract_probe(base_url, model_alias, "ASSISTANT_GROUNDING"),
            "SUMMARY_JSON": _contract_probe(base_url, model_alias, "MEETING_PROTOCOL_RU"),
            "MEETING_PROTOCOL_RU": _contract_probe(base_url, model_alias, "MEETING_PROTOCOL_RU"),
        }
        result["status"] = "READY" if all(item.get("status") == "READY" for item in result["probes"].values()) else "GENERATION_FAILED"
    except FileNotFoundError:
        result.update({"status": "MODEL_LOAD_FAILED", "probe": None, "probes": _failed_contract_probes("MODEL_LOAD_FAILED")})
    except Exception as exc:  # doctor output is intentionally stable and secret-free
        result.update({"status": "LLM_UNAVAILABLE", "probe": None, "probes": _failed_contract_probes(type(exc).__name__), "error": type(exc).__name__})
    finally:
        if started_here:
            runtime.stop()
    print(json.dumps(result, ensure_ascii=False))
    return 0 if result["status"] == "READY" else 2


if __name__ == "__main__":
    sys.exit(main())
