from __future__ import annotations

import hashlib
import json
import os
import re
from dataclasses import dataclass
from typing import Any, Awaitable, Callable, Iterable

from .contracts import (
    MEETING_PROTOCOL_RU,
    MEETING_PROTOCOL_RU_SCHEMA,
    PROTOCOL_RU_PROMPT_VERSION,
    PROTOCOL_RU_SCHEMA_VERSION,
    SUMMARY_SCHEMA_V2,
    SUMMARY_SCHEMA_VERSION,
    MeetingContext,
    SummaryContentError,
    normalize_summary_payload,
    profile_for,
    SummaryProfile,
    validate_summary_content,
)
from .extraction import (
    BLOCK_EXTRACTION_SCHEMA,
    PROTOCOL_BLOCK_EXTRACTION_SCHEMA,
    facts_prompt,
    normalize_block_extraction,
    normalize_protocol_block_extraction,
    validate_extracted_facts,
)
from .protocol import (
    group_protocol_candidates,
    group_protocol_tasks,
    limit_protocol_items,
    normalize_protocol_result,
    protocol_candidates_prompt,
    validate_protocol_candidates,
    validate_protocol_result,
)
from .reconciliation import decision_conflict_facts, reconcile_decisions
from .resolvers import resolve_extracted_facts, resolve_summary_action_items

SUMMARY_SCHEMA_V1: dict[str, Any] = {
    "type": "object",
    "required": ["summary", "topics", "decisions", "action_items", "risks", "open_questions"],
    "properties": {
        "summary": {"type": "string", "maxLength": 4000},
        "topics": {"type": "array", "maxItems": 20, "items": {"type": "string", "maxLength": 220}},
        "decisions": {
            "type": "array",
            "maxItems": 30,
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
            "maxItems": 40,
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
            "maxItems": 20,
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
            "maxItems": 20,
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

# The legacy schema remains available for old fixtures and downstream imports.
# New Qwen calls use the versioned contract below.
SUMMARY_SCHEMA: dict[str, Any] = SUMMARY_SCHEMA_V2


@dataclass(frozen=True)
class TranscriptSegment:
    id: str
    start_ms: int
    end_ms: int
    speaker: str
    text: str
    markers: tuple[str, ...] = ()

    def prompt_line(self) -> str:
        start = self.start_ms // 1000
        timecode = f"{start // 3600:02d}:{start // 60 % 60:02d}:{start % 60:02d}"
        marker_text = f"\n[USER_MARKERS: {'; '.join(self.markers)}]" if self.markers else ""
        return f"[SEG-{self.id} | {timecode} | {self.speaker}]\n{self.text.strip()}{marker_text}"


JsonInvoker = Callable[[list[dict[str, str]], dict[str, Any]], Awaitable[dict[str, Any]]]


def build_blocks(
    segments: Iterable[TranscriptSegment],
    max_chars: int = 24_000,
    overlap_segments: int = 0,
) -> list[str]:
    """Build bounded blocks while retaining a small context overlap.

    Overlap is deliberately segment-based rather than character-based: it keeps
    speaker turns and SEG-ID boundaries intact. Deduplication happens after the
    map/reduce step, so the reducer can still see facts that cross a block edge.
    """

    blocks: list[str] = []
    current: list[TranscriptSegment] = []
    size = 0
    overlap = max(0, int(overlap_segments))
    for segment in segments:
        if not segment.text.strip():
            continue
        line = segment.prompt_line()
        line_size = len(line) + 2
        if current and size + line_size > max_chars:
            blocks.append("\n\n".join(item.prompt_line() for item in current))
            current = current[-overlap:] if overlap else []
            size = sum(len(item.prompt_line()) + 2 for item in current)
        current.append(segment)
        size += line_size
    if current:
        blocks.append("\n\n".join(item.prompt_line() for item in current))
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


def validate_evidence_v2(
    payload: dict[str, Any],
    valid_ids: set[str],
    segment_texts: dict[str, str] | None = None,
) -> dict[str, Any]:
    """Validate v2 evidence references and expose review metadata."""

    checked_items = 0
    needs_review_items = 0
    for collection in ("topics", "decisions", "action_items", "risks", "open_questions", "notable_facts"):
        for item in payload.get(collection, []):
            if not isinstance(item, dict):
                continue
            if collection == "action_items":
                for field in ("responsible", "deadline", "deadline_text", "deadline_iso"):
                    value = item.get(field)
                    if isinstance(value, str) and value.strip().lower() in {"", "null", "none", "не указано", "не указан"}:
                        item[field] = None
            evidence = [str(value).removeprefix("SEG-") for value in item.get("evidence_segment_ids", [])]
            item["evidence_segment_ids"] = [value for value in evidence if value in valid_ids]
            evidence_supported = _claim_has_text_support(item, collection, segment_texts, item["evidence_segment_ids"])
            validation = item.get("validation") if isinstance(item.get("validation"), dict) else {}
            raw_reasons = validation.get("review_reasons", [])
            review_reasons = list(raw_reasons) if isinstance(raw_reasons, list) else []
            if not item["evidence_segment_ids"] and "NO_EVIDENCE" not in review_reasons:
                review_reasons.append("NO_EVIDENCE")
            if item["evidence_segment_ids"] and not evidence_supported and "UNSUPPORTED" not in review_reasons:
                review_reasons.append("UNSUPPORTED")
            needs_review = bool(review_reasons)
            item["needs_review"] = needs_review
            validation["evidence"] = bool(item["evidence_segment_ids"]) and evidence_supported
            if collection == "action_items":
                validation["responsible"] = bool(item.get("responsible"))
                validation["deadline"] = bool(item.get("deadline_iso") or item.get("deadline_text") or item.get("deadline"))
            validation["needs_review"] = needs_review
            if review_reasons:
                validation["review_reasons"] = review_reasons
                item["review_reasons"] = review_reasons
            item["validation"] = validation
            checked_items += 1
            needs_review_items += int(needs_review)
    payload["validation"] = {
        "evidence_checked": True,
        "checked_items": checked_items,
        "needs_review_items": needs_review_items,
        # Compatibility for existing persistence and API consumers. This
        # field now means items requiring review, not all inspected items.
        "review_items": needs_review_items,
        "unsupported_claims": needs_review_items,
    }
    return payload


def _claim_has_text_support(
    item: dict[str, Any],
    collection: str,
    segment_texts: dict[str, str] | None,
    evidence_ids: list[str],
) -> bool:
    if not evidence_ids:
        return False
    if not segment_texts:
        return True
    fields = {
        "topics": ("title", "summary"),
        "decisions": ("subject", "decision", "text"),
        "action_items": ("task", "responsible", "deadline_text"),
        "risks": ("text",),
        "open_questions": ("text",),
        "notable_facts": ("text",),
    }.get(collection, ("text",))
    claim = " ".join(str(item.get(field, "")) for field in fields)
    claim_terms = _content_terms(claim)
    evidence_terms = _content_terms(" ".join(segment_texts.get(segment_id, "") for segment_id in evidence_ids))
    if not claim_terms or not evidence_terms:
        return False
    required_overlap = 1 if len(claim_terms) <= 3 else 2
    return len(claim_terms & evidence_terms) >= required_overlap


def _content_terms(text: str) -> set[str]:
    stopwords = {
        "это", "как", "что", "для", "или", "при", "если", "надо", "будет", "был", "быть",
        "также", "после", "перед", "когда", "есть", "они", "она", "его", "ее", "их", "мы",
    }
    return {
        token for token in re.findall(r"[a-zа-яё0-9]{3,}", text.lower())
        if token not in stopwords
    }


class SummaryOrchestrator:
    def __init__(
        self,
        invoke_json: JsonInvoker,
        max_chars: int | None = None,
        profile: str | SummaryProfile | None = None,
        context: MeetingContext | None = None,
        progress: Callable[[str, int], Awaitable[None]] | None = None,
    ) -> None:
        self._invoke_json = invoke_json
        configured = os.getenv("LLM_BLOCK_MAX_CHARS", "16000") if max_chars is None else str(max_chars)
        self._max_chars = max(4000, int(configured))
        self._overlap_segments = max(0, int(os.getenv("LLM_BLOCK_OVERLAP_SEGMENTS", "8")))
        self._profile = profile if isinstance(profile, SummaryProfile) else profile_for(profile)
        self._context = context or MeetingContext()
        self._progress = progress

    async def _report_progress(self, stage: str, progress: int) -> None:
        if self._progress is not None:
            await self._progress(stage, progress)

    async def _invoke_final_summary(
        self,
        messages: list[dict[str, str]],
        valid_ids: set[str],
        segment_texts: dict[str, str],
    ) -> dict[str, Any]:
        """Generate the final summary with one deterministic schema retry.

        JSON parsing is already retried by the llama client.  This second gate
        is intentionally local and catches a syntactically valid response
        whose fields contain a dict/list repr or otherwise unsafe types.
        """

        last_error: SummaryContentError | None = None
        for attempt in range(2):
            request = messages
            if attempt:
                request = [
                    *messages,
                    {
                        "role": "user",
                        "content": (
                            "Предыдущий результат не прошёл content-gate. Повтори строго JSON: overview — обычная русская строка, "
                            "все коллекции — массивы объектов, без Python dict repr, SEG-ID в тексте и служебных токенов. "
                            "Сохраняй только существующие evidence_segment_ids."
                        ),
                    },
                ]
            try:
                payload = await self._invoke_json(request, SUMMARY_SCHEMA)
                result = normalize_summary_payload(payload)
                result = resolve_summary_action_items(result, self._context)
                result = validate_evidence_v2(result, valid_ids, segment_texts)
                validate_summary_content(result, valid_ids)
                # Keep rejected claims in validation.review_reasons for
                # diagnostics, but never expose them as user-facing summary
                # items or persist them as decisions/tasks.
                for collection in ("topics", "decisions", "action_items", "risks", "open_questions", "notable_facts"):
                    result[collection] = [item for item in result.get(collection, []) if not item.get("needs_review")]
                return result
            except SummaryContentError as exc:
                last_error = exc
                if attempt == 0:
                    continue
                raise
        raise last_error or SummaryContentError("unknown")

    async def summarize(self, segments: list[TranscriptSegment]) -> dict[str, Any]:
        if not segments:
            raise ValueError("transcript_has_no_segments")
        blocks = build_blocks(segments, self._max_chars, self._overlap_segments)
        if not blocks:
            raise ValueError("transcript_has_no_text")
        if self._profile.name == MEETING_PROTOCOL_RU:
            return await self._summarize_protocol_ru(segments, blocks)
        return await self._summarize_with_extraction(segments, blocks)
        mapped: list[dict[str, Any]] = []
        system = (
            "Ты анализируешь русскоязычные стенограммы совещаний. "
            "Не придумывай решения, ответственных или сроки. Каждый факт связывай с SEG-ID. "
            "Если ответственный или срок прямо не названы, используй null. Верни только JSON."
        )
        system = (
            "Ты анализируешь русскоязычную стенограмму совещания. "
            "Не придумывай решения, ответственных, сроки или факты. Каждый тезис связывай с SEG-ID. "
            "Если ответственный или срок прямо не названы, используй null. Сохраняй противоречия как риск или открытый вопрос. "
            "Верни только JSON по схеме Summary v2. "
            + self._profile.prompt_text()
            + "\nКонтекст встречи:\n"
            + self._context.prompt_text()
        )
        for index, block in enumerate(blocks, start=1):
            messages = [
                {"role": "system", "content": system},
                {"role": "user", "content": f"Извлеки факты из блока {index}/{len(blocks)}:\n\n{block}"},
            ]
            messages[-1] = {
                "role": "user",
                "content": f"Извлеки факты из блока {index}/{len(blocks)}. Не объединяй факты без общей опоры в тексте:\n\n{block}",
            }
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
            messages[-1] = {
                "role": "user",
                "content": "Объедини результаты блоков, удали дубликаты и сохрани исходные SEG-ID. Если факты конфликтуют, не выбирай молча одну версию:\n\n"
                + json.dumps(mapped, ensure_ascii=False),
            }
            result = await self._invoke_json(messages, SUMMARY_SCHEMA)

        result = normalize_summary_payload(result)
        result = validate_evidence_v2(
            result,
            {segment.id for segment in segments},
            {segment.id: segment.text for segment in segments},
        )
        reviewed = int(result["validation"].get("review_items", 0))
        unsupported = int(result["validation"].get("unsupported_claims", 0))
        result["quality_score"] = round(
            1.0 if reviewed == 0 else max(0.0, min(1.0, 1.0 - unsupported / reviewed)),
            3,
        )
        result["source_hash"] = transcript_source_hash(segments)
        result["block_count"] = len(blocks)
        result["schema_version"] = SUMMARY_SCHEMA_VERSION
        return result

    async def _summarize_protocol_ru(
        self,
        segments: list[TranscriptSegment],
        blocks: list[str],
    ) -> dict[str, Any]:
        valid_ids = {segment.id for segment in segments}
        segment_texts = {segment.id: segment.text for segment in segments}
        segment_times = {segment.id: (segment.start_ms, segment.end_ms) for segment in segments}
        extraction_system = (
            "Ты извлекаешь кандидатов для русского протокола совещания. "
            "Работай только с текстом текущего блока и указывай только существующие SEG-ID. "
            "Не формируй финальный протокол, не придумывай факты, решения, задачи или сроки. "
            "Не извлекай и не создавай ответственных. "
            "Тип discussion/decision используй для темы и принятого решения; task — только для отдельной исполнимой задачи. "
            "deadline_text заполняй только если срок прямо сказан в цитируемых сегментах. Верни только JSON."
        )
        extracted: list[dict[str, Any]] = []
        for index, block in enumerate(blocks, start=1):
            await self._report_progress("EXTRACTING_FACTS", 10 + int(40 * (index - 1) / max(1, len(blocks))))
            payload = await self._invoke_json(
                [
                    {"role": "system", "content": extraction_system},
                    {
                        "role": "user",
                        "content": (
                            f"Извлеки кандидатов из блока {index}/{len(blocks)}. "
                            "Для обсуждения укажи topic_hint, краткий context и decision только при наличии подтверждённого решения. "
                            "Для задачи укажи text и deadline_text.\n\n"
                            + block
                        ),
                    },
                ],
                PROTOCOL_BLOCK_EXTRACTION_SCHEMA,
            )
            extracted.extend(normalize_protocol_block_extraction(payload))

        await self._report_progress("GROUPING_TOPICS", 55)
        supported, rejected = validate_protocol_candidates(extracted, valid_ids, segment_texts)
        supported = reconcile_decisions(supported, segment_times)
        conflicts = decision_conflict_facts(supported)
        groups = group_protocol_candidates(supported)
        tasks = group_protocol_tasks(supported)
        groups, groups_truncated = limit_protocol_items(groups)
        tasks, tasks_truncated = limit_protocol_items(tasks)
        truncated = groups_truncated or tasks_truncated
        deadline_reviews = sum("UNSUPPORTED_DEADLINE" in item.get("review_reasons", []) for item in supported)
        await self._report_progress("RESOLVING_DECISIONS", 62)
        await self._report_progress("EXTRACTING_TASKS", 66)
        await self._report_progress("RESOLVING_DEADLINES", 70)
        reducer_system = (
            "Сформируй русский структурированный протокол только из переданных validated candidates. "
            "Не возвращайся к исходной стенограмме и не добавляй новые факты. "
            "В questions_and_decisions включай только темы с подтверждённым решением: topic — короткая тема, "
            "context — 1–3 предложения о ситуации, decision — конкретное подтверждённое действие. "
            "tasks формируй отдельно и не копируй автоматически решение как задачу. "
            "Сохраняй только переданные evidence_segment_ids. Не генерируй таймкоды. "
            "Полностью исключи поля responsible, responsible_text, responsible_status и любые аналоги. "
            "Если срок не подтверждён, deadline_text=null и deadline_iso=null. Верни только JSON по схеме."
        )
        await self._report_progress("VALIDATING_EVIDENCE", 76)
        result_payload = await self._invoke_json(
            [
                {"role": "system", "content": reducer_system},
                {"role": "user", "content": protocol_candidates_prompt(groups, tasks)},
            ],
            MEETING_PROTOCOL_RU_SCHEMA,
        )
        await self._report_progress("DEDUPLICATING", 88)
        result = validate_protocol_result(
            normalize_protocol_result(result_payload),
            valid_ids,
            segment_texts,
            segment_times,
            self._context.date,
            self._context.timezone,
        )
        quality = result["quality"]
        quality["extraction_candidates"] = len(extracted)
        quality["decision_conflicts"] = len(conflicts)
        quality["decision_conflict_groups"] = len({item["conflict_group"] for item in conflicts})
        quality["review_items"] = int(quality.get("review_items", 0)) + deadline_reviews + len(conflicts)
        quality["rejected_items"] = int(quality.get("rejected_items", 0)) + len(rejected)
        quality["question_count"] = len(result["questions_and_decisions"])
        quality["task_count"] = len(result["tasks"])
        quality["supported_count"] = quality["question_count"] + quality["task_count"]
        quality["partial_count"] = quality["review_items"]
        quality["rejected_count"] = quality["rejected_items"]
        quality["unsupported_deadlines_removed"] = deadline_reviews
        topic_candidate_count = sum(
            1 for item in supported
            if item.get("type") in {"discussion", "decision", "fact"} and item.get("decision")
        )
        task_candidate_count = sum(1 for item in supported if item.get("type") == "task")
        quality["duplicate_topics_removed"] = max(0, topic_candidate_count - len(groups))
        quality["duplicate_tasks_removed"] = max(0, task_candidate_count - len(tasks))
        quality["truncated"] = truncated
        reasons = list(quality.get("reasons", []))
        if conflicts and "CONFLICTING_STATEMENTS" not in reasons:
            reasons.append("CONFLICTING_STATEMENTS")
        if deadline_reviews and "UNSUPPORTED_DEADLINE" not in reasons:
            reasons.append("UNSUPPORTED_DEADLINE")
        if rejected and "UNSUPPORTED_ITEMS_REJECTED" not in reasons:
            reasons.append("UNSUPPORTED_ITEMS_REJECTED")
        if truncated and "PROTOCOL_LIMIT_TRUNCATED" not in reasons:
            reasons.append("PROTOCOL_LIMIT_TRUNCATED")
        quality["reasons"] = reasons
        total_items = len(result["questions_and_decisions"]) + len(result["tasks"]) + quality["rejected_items"]
        quality["score"] = round(
            1.0 if total_items == 0 else max(0.0, min(1.0, 1.0 - (quality["rejected_items"] + quality["review_items"]) / total_items)),
            3,
        )
        if quality["rejected_items"] and not result["questions_and_decisions"] and not result["tasks"]:
            quality["status"] = "FAILED"
        elif quality["rejected_items"] or quality["review_items"]:
            quality["status"] = "NEEDS_REVIEW"
        await self._report_progress("QUALITY_CHECK", 92)
        result.update({
            "schema_version": PROTOCOL_RU_SCHEMA_VERSION,
            "prompt_version": PROTOCOL_RU_PROMPT_VERSION,
            "profile": MEETING_PROTOCOL_RU,
            "source_hash": transcript_source_hash(segments),
            "block_count": len(blocks),
            "quality_score": quality["score"],
        })
        await self._report_progress("PERSISTING", 95)
        return result

    async def _summarize_with_extraction(
        self,
        segments: list[TranscriptSegment],
        blocks: list[str],
    ) -> dict[str, Any]:
        valid_ids = {segment.id for segment in segments}
        segment_texts = {segment.id: segment.text for segment in segments}
        extraction_system = (
            "Extract only factual candidates from the Russian transcript block. "
            "Do not write a meeting summary. Every candidate must cite one or more SEG-ID values from the block. "
            "USER_MARKERS are hints, not proof: verify the spoken text. "
            "Do not infer a responsible person or a deadline unless it is explicitly stated. Return only JSON."
        )
        extracted: list[dict[str, Any]] = []
        for index, block in enumerate(blocks, start=1):
            await self._report_progress("EXTRACTING_FACTS", 10 + int(45 * (index - 1) / max(1, len(blocks))))
            messages = [
                {"role": "system", "content": extraction_system},
                {
                    "role": "user",
                    "content": f"Extract factual candidates from block {index}/{len(blocks)}.\n\n{block}",
                },
            ]
            payload = await self._invoke_json(messages, BLOCK_EXTRACTION_SCHEMA)
            extracted.extend(normalize_block_extraction(payload))

        await self._report_progress("MERGING_FACTS", 60)
        extracted = normalize_block_extraction({"facts": extracted})
        supported, rejected = validate_extracted_facts(extracted, valid_ids, segment_texts)
        await self._report_progress("VALIDATING_EVIDENCE", 70)
        supported = resolve_extracted_facts(supported, self._context)
        segment_times = {segment.id: (segment.start_ms, segment.end_ms) for segment in segments}
        supported = reconcile_decisions(supported, segment_times)
        conflicts = decision_conflict_facts(supported)
        await self._report_progress("RESOLVING_ENTITIES", 78)
        reducer_system = (
            "Create Summary v2 from validated facts only. Do not add facts that are not present in the input. "
            "Keep evidence SEG-ID values. If a field is not supported, use null or an empty collection. "
            "For facts with the same conflict_group, keep each conflicting decision as a separate decision; "
            "do not silently merge or discard the earlier version. Mark those decisions for review with "
            "validation.review_reasons containing CONFLICTING_STATEMENTS. Preserve conflicts as risks or open questions. "
            "Return only JSON.\n"
            + self._profile.prompt_text()
            + "\nMeeting context:\n"
            + self._context.prompt_text()
        )
        final_messages = [
            {"role": "system", "content": reducer_system},
            {
                "role": "user",
                "content": "Validated facts:\n\n" + facts_prompt(supported),
            },
        ]
        await self._report_progress("GENERATING_SUMMARY", 85)
        result = await self._invoke_final_summary(final_messages, valid_ids, segment_texts)
        validation = result.setdefault("validation", {})
        validation["extraction_candidates"] = len(extracted)
        validation["rejected_facts"] = len(rejected)
        validation["decision_conflicts"] = len(conflicts)
        validation["decision_conflict_groups"] = len({item["conflict_group"] for item in conflicts})
        review_reasons = [
            {
                "type": fact.get("type"),
                "text": fact.get("text"),
                "reasons": fact.get("review_reasons", ["UNSUPPORTED"]),
                "evidence_segment_ids": fact.get("evidence_segment_ids", []),
            }
            for fact in rejected
        ]
        if review_reasons:
            validation["review_reasons"] = review_reasons[:40]
        if conflicts:
            validation.setdefault("review_reasons", [])
            validation["review_reasons"].extend(conflicts[:40])
        reviewed = int(validation.get("review_items", 0)) + len(rejected)
        unsupported = int(validation.get("unsupported_claims", 0)) + len(rejected)
        result["quality_score"] = round(
            1.0 if reviewed == 0 else max(0.0, min(1.0, 1.0 - unsupported / reviewed)),
            3,
        )
        result["source_hash"] = transcript_source_hash(segments)
        result["block_count"] = len(blocks)
        result["schema_version"] = SUMMARY_SCHEMA_VERSION
        result["contentValidity"] = "NEEDS_REVIEW" if reviewed else "VALID"
        result["generationState"] = "READY_WITH_WARNINGS" if reviewed else "READY"
        result["errorCode"] = None
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
        self._client: Any | None = None

    @property
    def model(self) -> str:
        return self._model

    @property
    def url(self) -> str:
        return self._url

    async def _get_client(self) -> Any:
        if self._client is None:
            import httpx

            self._client = httpx.AsyncClient(timeout=self._timeout)
        return self._client

    async def aclose(self) -> None:
        client, self._client = self._client, None
        if client is not None:
            await client.aclose()

    async def invoke_json(self, messages: list[dict[str, str]], schema: dict[str, Any]) -> dict[str, Any]:
        max_tokens = max(512, int(os.getenv("LLM_MAX_OUTPUT_TOKENS", "3072")))
        client = await self._get_client()
        for attempt in range(2):
            request_messages = messages
            if attempt:
                request_messages = [
                    *messages,
                    {
                        "role": "user",
                        "content": "Повтори ответ строго одним валидным JSON по Summary v2. Не ограничивай разделы четырьмя элементами; используй только подтверждённые SEG-ID и оставляй неподтверждённые поля пустыми или null.",
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
