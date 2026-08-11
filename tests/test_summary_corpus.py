import json
import unittest
from pathlib import Path

from workers.summary_worker.extraction import validate_extracted_facts
from workers.summary_worker.reconciliation import reconcile_decisions
from workers.summary_worker.resolvers import resolve_extracted_facts
from workers.summary_worker.contracts import MeetingContext


CORPUS_DIR = Path(__file__).parent / "summary-corpus"


class SummaryCorpusTests(unittest.TestCase):
    def _load(self, name: str) -> dict:
        return json.loads((CORPUS_DIR / f"{name}.json").read_text(encoding="utf-8"))

    def test_short_operative_fact_validation(self):
        case = self._load("short-operative")
        segment_texts = {item["id"]: item["text"] for item in case["segments"]}
        supported, rejected = validate_extracted_facts(case["facts"], set(segment_texts), segment_texts)
        self.assertEqual(case["expected"]["supported"], len(supported))
        self.assertEqual(case["expected"]["rejected"], len(rejected))
        self.assertEqual(case["expected"]["action_items"], sum(item["type"] == "action_item" for item in supported))
        self.assertEqual(case["expected"]["risks"], sum(item["type"] == "risk" for item in supported))

    def test_relative_deadline_and_responsible_resolution(self):
        case = self._load("relative-deadlines")
        segment_texts = {item["id"]: item["text"] for item in case["segments"]}
        supported, rejected = validate_extracted_facts(case["facts"], set(segment_texts), segment_texts)
        self.assertFalse(rejected)
        resolved = resolve_extracted_facts(supported, MeetingContext.from_mapping(case["meeting_context"]))
        self.assertEqual(case["expected"]["deadline_iso"], resolved[0]["deadline_iso"])
        self.assertEqual(case["expected"]["responsible"], resolved[0]["responsible_text"])

    def test_conflicting_decisions_are_preserved_for_review(self):
        case = self._load("conflicting-decisions")
        segment_times = {
            item["id"]: (item["start_ms"], item["end_ms"])
            for item in case["segments"]
        }
        reconciled = reconcile_decisions(case["facts"], segment_times)
        groups = {item.get("conflict_group") for item in reconciled if item.get("conflict_group")}
        reviewed = [item for item in reconciled if item.get("needs_review")]
        self.assertEqual(case["expected"]["conflict_groups"], len(groups))
        self.assertEqual(case["expected"]["reviewed_facts"], len(reviewed))
        self.assertTrue(all(case["expected"]["reason"] in item.get("review_reasons", []) for item in reviewed))
        self.assertEqual("SUPERSEDED_CANDIDATE", reconciled[0]["resolution_status"])
        self.assertEqual("CURRENT_CANDIDATE", reconciled[1]["resolution_status"])


if __name__ == "__main__":
    unittest.main()
