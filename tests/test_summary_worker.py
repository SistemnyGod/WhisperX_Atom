import asyncio
import unittest

from workers.summary_worker.summarizer import (
    SummaryOrchestrator,
    TranscriptSegment,
    SUMMARY_SCHEMA,
    build_blocks,
    parse_json_content,
    validate_evidence,
    validate_evidence_v2,
)
from workers.summary_worker.contracts import (
    SUMMARY_SCHEMA_VERSION,
    MeetingContext,
    normalize_summary_payload,
)
from workers.summary_worker.extraction import (
    BLOCK_EXTRACTION_SCHEMA,
    deduplicate_facts,
    normalize_block_extraction,
    validate_extracted_facts,
)
from workers.summary_worker.resolvers import (
    resolve_deadline,
    resolve_responsible,
    resolve_extracted_facts,
)


class SummaryWorkerTests(unittest.TestCase):
    def test_blocks_preserve_segment_ids_and_timecodes(self):
        segments = [
            TranscriptSegment("a", 61_000, 62_000, "Speaker 1", "First statement"),
            TranscriptSegment("b", 62_000, 63_000, "Speaker 2", "Second statement"),
        ]
        blocks = build_blocks(segments, max_chars=80)
        self.assertGreaterEqual(len(blocks), 1)
        self.assertIn("SEG-a", blocks[0])
        self.assertIn("00:01:01", blocks[0])

    def test_segment_prompt_keeps_user_marker_as_non_authoritative_hint(self):
        segment = TranscriptSegment("a", 61_000, 62_000, "Speaker 1", "Decision text", ("DECISION",))
        self.assertIn("USER_MARKERS: DECISION", segment.prompt_line())

    def test_evidence_is_filtered_and_null_strings_are_normalized(self):
        payload = {
            "decisions": [],
            "action_items": [{
                "task": "Task",
                "responsible": "null",
                "deadline": "не указано",
                "evidence_segment_ids": ["SEG-1", "SEG-missing"],
            }],
            "risks": [],
            "open_questions": [],
        }
        result = validate_evidence(payload, {"1"})
        action = result["action_items"][0]
        self.assertIsNone(action["responsible"])
        self.assertIsNone(action["deadline"])
        self.assertEqual(["1"], action["evidence_segment_ids"])
        self.assertFalse(action["needs_review"])

    def test_json_parser_accepts_markdown_and_prefix(self):
        result = parse_json_content("Ответ:\n" + chr(96) * 3 + "json\n{\"summary\": \"ok\"}\n" + chr(96) * 3)
        self.assertEqual("ok", result["summary"])

    def test_json_parser_rejects_truncated_json(self):
        with self.assertRaises(ValueError):
            parse_json_content("{\"summary\": \"unterminated")

    def test_orchestrator_adds_source_hash(self):
        async def fake_invoker(messages, schema):
            return {
                "summary": "Summary",
                "topics": [],
                "decisions": [],
                "action_items": [],
                "risks": [],
                "open_questions": [],
            }

        result = asyncio.run(SummaryOrchestrator(fake_invoker).summarize([
            TranscriptSegment("1", 0, 1000, "Speaker", "Text")
        ]))
        self.assertEqual(1, result["block_count"])
        self.assertEqual(64, len(result["source_hash"]))
        self.assertEqual(0.0, result["quality_score"])

    def test_v2_schema_does_not_have_artificial_four_item_caps(self):
        self.assertEqual("summary-v2", SUMMARY_SCHEMA_VERSION)
        self.assertEqual(20, SUMMARY_SCHEMA["properties"]["topics"]["maxItems"])
        self.assertEqual(30, SUMMARY_SCHEMA["properties"]["decisions"]["maxItems"])
        self.assertEqual(40, SUMMARY_SCHEMA["properties"]["action_items"]["maxItems"])
        self.assertIn("notable_facts", SUMMARY_SCHEMA["properties"])

    def test_block_overlap_keeps_boundary_segment(self):
        segments = [
            TranscriptSegment(str(index), index * 1000, (index + 1) * 1000, "Speaker", "x" * 30)
            for index in range(4)
        ]
        blocks = build_blocks(segments, max_chars=90, overlap_segments=1)
        self.assertGreaterEqual(len(blocks), 2)
        for left, right in zip(blocks, blocks[1:]):
            left_ids = {line.split("|")[0].strip() for line in left.splitlines() if line.startswith("[SEG-")}
            right_ids = {line.split("|")[0].strip() for line in right.splitlines() if line.startswith("[SEG-")}
            self.assertTrue(left_ids & right_ids)

    def test_v1_payload_is_normalized_to_v2_and_keeps_legacy_summary(self):
        result = normalize_summary_payload({
            "summary": "Overview",
            "topics": ["Topic"],
            "decisions": [{"text": "Decision", "evidence_segment_ids": ["SEG-1"]}],
            "action_items": [{"task": "Task", "responsible": "null", "deadline": "2026-08-12", "evidence_segment_ids": ["1"]}],
            "risks": [],
            "open_questions": [],
        })
        self.assertEqual("summary-v2", result["schema_version"])
        self.assertEqual("Overview", result["overview"])
        self.assertEqual("Overview", result["summary"])
        self.assertEqual("Decision", result["decisions"][0]["decision"])
        self.assertIsNone(result["action_items"][0]["responsible"])
        self.assertEqual("2026-08-12", result["action_items"][0]["deadline_text"])

    def test_normalizer_merges_duplicate_items_and_evidence(self):
        result = normalize_summary_payload({
            "overview": "Overview",
            "topics": [],
            "decisions": [
                {"subject": "", "decision": "Утвердить план", "evidence_segment_ids": ["1"]},
                {"subject": "", "decision": "утвердить план", "evidence_segment_ids": ["2"]},
            ],
            "action_items": [],
            "risks": [],
            "open_questions": [],
            "notable_facts": [],
        })
        self.assertEqual(1, len(result["decisions"]))
        self.assertEqual(["1", "2"], result["decisions"][0]["evidence_segment_ids"])

    def test_meeting_context_is_prompt_safe_and_optional(self):
        context = MeetingContext.from_mapping({"title": "Оперативка", "participants": ["Анна", "Борис"]})
        self.assertIn("Название: Оперативка", context.prompt_text())
        self.assertIn("Участники: Анна, Борис", context.prompt_text())

    def test_v2_evidence_requires_text_support_when_segment_texts_are_available(self):
        result = validate_evidence_v2(
            {
                "decisions": [{"subject": "Срок", "decision": "Выбрать сервер", "evidence_segment_ids": ["1"]}],
                "action_items": [],
                "risks": [],
                "open_questions": [],
            },
            {"1"},
            {"1": "Обсудили только бюджет проекта"},
        )
        self.assertTrue(result["decisions"][0]["needs_review"])
        self.assertEqual(1, result["validation"]["unsupported_claims"])

    def test_block_extraction_schema_is_separate_from_final_summary(self):
        self.assertIn("facts", BLOCK_EXTRACTION_SCHEMA["required"])
        self.assertNotIn("overview", BLOCK_EXTRACTION_SCHEMA["properties"])
        self.assertIn("candidate_action_items", BLOCK_EXTRACTION_SCHEMA["properties"])

    def test_extraction_normalizer_and_dedup_merge_overlapping_tasks(self):
        facts = normalize_block_extraction({
            "facts": [
                {"type": "action_item", "text": "Проверить насос", "responsible_text": "Петров", "evidence_segment_ids": ["1"]},
                {"type": "action_item", "text": "Проверить насос", "responsible_text": "Петров", "evidence_segment_ids": ["2"]},
            ],
            "candidate_topics": [],
            "candidate_decisions": [],
            "candidate_action_items": [],
            "candidate_risks": [],
            "candidate_questions": [],
        })
        self.assertEqual(1, len(deduplicate_facts(facts)))
        self.assertEqual(["1", "2"], facts[0]["evidence_segment_ids"])

    def test_extraction_validator_rejects_existing_but_irrelevant_segment(self):
        supported, rejected = validate_extracted_facts(
            [{"type": "decision", "text": "Остановить печь", "evidence_segment_ids": ["1"]}],
            {"1"},
            {"1": "Обсудили только бюджет проекта"},
        )
        self.assertEqual([], supported)
        self.assertEqual(["UNSUPPORTED"], rejected[0]["review_reasons"])

    def test_orchestrator_uses_extraction_schema_before_final_schema(self):
        schemas = []
        progress = []

        async def fake_invoker(messages, schema):
            schemas.append(schema)
            if "facts" in schema["required"]:
                return {
                    "facts": [{"type": "fact", "text": "Бюджет утверждён", "evidence_segment_ids": ["1"]}],
                    "candidate_topics": [],
                    "candidate_decisions": [],
                    "candidate_action_items": [],
                    "candidate_risks": [],
                    "candidate_questions": [],
                }
            return {"overview": "Бюджет утверждён", "topics": [], "decisions": [], "action_items": [], "risks": [], "open_questions": [], "notable_facts": [{"text": "Бюджет утверждён", "evidence_segment_ids": ["1"]}]}

        async def report(stage, value):
            progress.append((stage, value))

        result = asyncio.run(SummaryOrchestrator(fake_invoker, progress=report).summarize([
            TranscriptSegment("1", 0, 1000, "Speaker", "Бюджет утверждён"),
        ]))
        self.assertEqual(BLOCK_EXTRACTION_SCHEMA, schemas[0])
        self.assertIn("overview", schemas[-1]["required"])
        self.assertEqual(1, result["validation"]["extraction_candidates"])
        self.assertEqual(
            ["EXTRACTING_FACTS", "MERGING_FACTS", "VALIDATING_EVIDENCE", "RESOLVING_ENTITIES", "GENERATING_SUMMARY"],
            [stage for stage, _ in progress],
        )

    def test_responsible_resolver_never_assigns_current_speaker_implicitly(self):
        unresolved = resolve_responsible(None, ("Петров П.П.",))
        self.assertEqual("UNRESOLVED", unresolved["status"])
        resolved = resolve_responsible("Петров", ("Петров П.П.",))
        self.assertEqual("RESOLVED", resolved["status"])
        ambiguous = resolve_responsible("Иванов", ("Иванов И.И.", "Иванов П.П."))
        self.assertEqual("AMBIGUOUS", ambiguous["status"])

    def test_deadline_resolver_handles_relative_date_only_with_meeting_date(self):
        self.assertEqual("2026-08-11", resolve_deadline("завтра", "2026-08-10")["deadline_iso"])
        self.assertEqual("UNRESOLVED", resolve_deadline("до конца смены", "2026-08-10")["status"])
        self.assertEqual("MEETING_DATE_REQUIRED", resolve_deadline("завтра", None)["reason"])

    def test_entity_resolution_keeps_explicit_person_and_relative_deadline(self):
        facts = resolve_extracted_facts(
            [{
                "type": "action_item",
                "text": "Проверить насос",
                "responsible_text": "Петров",
                "deadline_text": "завтра",
                "evidence_segment_ids": ["1"],
            }],
            MeetingContext.from_mapping({"date": "2026-08-10", "participants": ["Петров П.П."]}),
        )
        self.assertEqual("Петров П.П.", facts[0]["responsible_text"])
        self.assertEqual("2026-08-11", facts[0]["deadline_iso"])


if __name__ == "__main__":
    unittest.main()
