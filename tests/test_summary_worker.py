import asyncio
import unittest

from workers.summary_worker.summarizer import (
    SummaryOrchestrator,
    TranscriptSegment,
    build_blocks,
    validate_evidence,
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


if __name__ == "__main__":
    unittest.main()