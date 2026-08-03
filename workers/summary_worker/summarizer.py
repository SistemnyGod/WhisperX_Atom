from __future__ import annotations

import hashlib
import json
import os
from dataclasses import dataclass
from typing import Any, Awaitable, Callable, Iterable


SUMMARY_SCHEMA: dict[str, Any] = {
    "type": "object",
    "required": ["summary", "topics", "decisions", "action_items", "risks", "open_questions"],
    "properties": {
        "summary": {"type": "string", "maxLength": 600},
        "topics": {"type": "array", "maxItems": 4, "items": {"type": "string", "maxLength": 220}},
        "decisions": {
            "type": "array",
            "maxItems": 4,
            "items": {
                "type": "object",
                "required": ["text", "evidence_segment_ids"],
                "properties": {
                    "text": {"type": "string", "maxLength": 260},
                    "evidence_segment_ids": {"type": "array", "maxItems": 3, "items": {"type": "string"}},
                },
            },
        },
        "action_items": {
            "type": "array",
            "maxItems": 4,
            "items": {
                "type": "object",
                "required": ["task", "responsible", "deadline", "evidence_segment_ids"],
                "properties": {
                    "task": {"type": "string", "maxLength": 260},
                    "responsible": {"type": ["string", "null"], "maxLength": 100},
                    "deadline": {"type": ["string", "null"], "maxLength": 60},
                    "evidence_segment_ids": {"type": "array", "maxItems": 3, "items": {"type": "string"}},
                },
            },
        },
        "risks": {
            "type": "array",
            "maxItems": 4,
            "items": {
                "type": "object",
                "required": ["text", "evidence_segment_ids"],
                "properties": {
                    "text": {"type": "string", "maxLength": 260},
                    "evidence_segment_ids": {"type": "array", "maxItems": 3, "items": {"type": "string"}},
                },
            },
        },
        "open_questions": {
            "type": "array",
            "maxItems": 4,
            "items": {
                "type": "object",
                "required": ["text", "evidence_segment_ids"],
                "properties": {
                    "text": {"type": "string", "maxLength": 260},
                    "evidence_segment_ids": {"type": "array", "maxItems": 3, "items": {"type": "string"}},
                },
            },
        },
    },
}


@dataclass(frozen=True)
class TranscriptSegment:
    id: str
    start_ms: int
    end_ms: int
    speaker: str
    text: str

    def prompt_line(self) -> str:
        start = self.start_ms // 1000
        timecode = f"{start // 3600:02d}:{start // 60 % 60:02d}:{start % 60:02d}"
        return f"[SEG-{self.id} | {timecode} | {self.speaker}]\n{self.text.strip()}"


JsonInvoker = Callable[[list[dict[str, str]], dict[str, Any]], Awaitable[dict[str, Any]]]


def build_blocks(segments: Iterable[TranscriptSegment], max_chars: int = 24_000) -> list[str]:
    blocks: list[str] = []
    current: list[str] = []
    size = 0
    for segment in segments:
        if not segment.text.strip():
            continue
        line = segment.prompt_line()
        if current and size + len(line) + 2 > max_chars:
            blocks.append("\n\n".join(current))
            current = []
            size = 0
        current.append(line)
        size += len(line) + 2
    if current:
        blocks.append("\n\n".join(current))
    return blocks


def transcript_source_hash(segments: Iterable[TranscriptSegment]) -> str:
    canonical = "\n".join(segment.prompt_line() for segment in segments)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def validate_evidence(payload: dict[str, Any], valid_ids: set[str]) -> dict[str, Any]:
    for collection in ("decisions", "action_items", "risks", "open_questions"):
        for item in payload.get(collection, []):
            if collection == "action_items":
                for field in ("responsible", "deadline"):
                    value = item.get(field)
                    if isinstance(value, str) and value.strip().lower() in {"", "null", "none", "не указан", "не указано"}:
                        item[field] = None
            evidence = [str(value).removeprefix("SEG-") for value in item.get("evidence_segment_ids", [])]
            item["evidence_segment_ids"] = [value for value in evidence if value in valid_ids]
            item["needs_review"] = not bool(item["evidence_segment_ids"])
    return payload


class SummaryOrchestrator:
    def __init__(self, invoke_json: JsonInvoker, max_chars: int | None = None) -> None:
        self._invoke_json = invoke_json
        configured = os.getenv("LLM_BLOCK_MAX_CHARS", "16000") if max_chars is None else str(max_chars)
        self._max_chars = max(4000, int(configured))

    async def summarize(self, segments: list[TranscriptSegment]) -> dict[str, Any]:
        if not segments:
            raise ValueError("transcript_has_no_segments")
        blocks = build_blocks(segments, self._max_chars)
        mapped: list[dict[str, Any]] = []
        system = (
            "Ты анализируешь русскоязычные стенограммы совещаний. "
            "Не придумывай решения, ответственных или сроки. Каждый факт связывай с SEG-ID. "
            "Если ответственный или срок прямо не названы, используй null. Верни только JSON."
        )
        for index, block in enumerate(blocks, start=1):
            messages = [
                {"role": "system", "content": system},
                {"role": "user", "content": f"Извлеки факты из блока {index}/{len(blocks)}:\n\n{block}"},
            ]
            mapped.append(await self._invoke_json(messages, SUMMARY_SCHEMA))

        if len(mapped) == 1:
            result = mapped[0]
        else:
            messages = [
                {"role": "system", "content": system},
                {
                    "role": "user",
                    "content": "Объедини результаты блоков, удали дубли и сохрани исходные SEG-ID:\n\n"
                    + json.dumps(mapped, ensure_ascii=False),
                },
            ]
            result = await self._invoke_json(messages, SUMMARY_SCHEMA)

        result = validate_evidence(result, {segment.id for segment in segments})
        result["source_hash"] = transcript_source_hash(segments)
        result["block_count"] = len(blocks)
        return result


def parse_json_content(content: str) -> dict[str, Any]:
    text = content.strip()
    if text.startswith(chr(96) * 3):
        text = text.removeprefix(chr(96) * 3).removeprefix("json").removesuffix(chr(96) * 3).strip()
    try:
        value = json.loads(text)
    except json.JSONDecodeError:
        decoder = json.JSONDecoder()
        starts = [index for index, char in enumerate(text) if char == "{"]
        value = None
        for start in starts:
            try:
                candidate, _ = decoder.raw_decode(text[start:])
                if isinstance(candidate, dict):
                    value = candidate
                    break
            except json.JSONDecodeError:
                continue
        if value is None:
            raise ValueError("llm_invalid_json")
    if not isinstance(value, dict):
        raise ValueError("llm_invalid_json")
    return value


class LlamaCppClient:
    def __init__(self, base_url: str, model: str = "qwen3-8b", timeout_seconds: int = 600) -> None:
        self._url = base_url.rstrip("/") + "/chat/completions"
        self._model = model
        self._timeout = timeout_seconds

    @property
    def model(self) -> str:
        return self._model

    async def invoke_json(self, messages: list[dict[str, str]], schema: dict[str, Any]) -> dict[str, Any]:
        import httpx

        max_tokens = max(512, int(os.getenv("LLM_MAX_OUTPUT_TOKENS", "3072")))
        async with httpx.AsyncClient(timeout=self._timeout) as client:
            for attempt in range(2):
                request_messages = messages
                if attempt:
                    request_messages = [
                        *messages,
                        {
                            "role": "user",
                            "content": "Повтори ответ строго как один валидный JSON-объект без Markdown и пояснений. Уменьши каждую коллекцию максимум до 4 элементов; длина текста каждого элемента — до 260 символов. Не обрывай JSON.",
                        },
                    ]
                body = {
                    "model": self._model,
                    "messages": request_messages,
                    "temperature": 0.1,
                    "max_tokens": min(4096, max_tokens * (attempt + 1)),
                    "response_format": {"type": "json_object", "schema": schema},
                }
                response = await client.post(self._url, json=body)
                response.raise_for_status()
                payload = response.json()
                choice = payload["choices"][0]
                content = choice["message"]["content"]
                if choice.get("finish_reason") == "length" and attempt == 0:
                    continue
                if not isinstance(content, str):
                    raise ValueError("llm_response_content_is_not_text")
                content = content.strip()
                if content.startswith("```"):
                    content = content.removeprefix("```").removeprefix("json").removesuffix("```").strip()
                try:
                    return parse_json_content(content)
                except (json.JSONDecodeError, ValueError):
                    if attempt == 1:
                        raise ValueError("llm_invalid_json")
                    continue
        raise ValueError("llm_invalid_json")
