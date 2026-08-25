import importlib.util
from pathlib import Path


ROOT = Path(__file__).parents[1]
SPEC = importlib.util.spec_from_file_location("pipeline_repair", ROOT / "scripts" / "repair-meeting-pipeline.py")
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def test_repair_runner_only_selects_meetings_with_v1_and_missing_or_retryable_summary():
    assert MODULE._needs_repair({"transcriptV1Id": "v1", "summaryId": None})
    assert MODULE._needs_repair({"transcriptV1Id": "v1", "summaryId": "s", "summaryStatus": "FAILED"})
    assert not MODULE._needs_repair({"transcriptV1Id": None, "summaryId": None})
    assert not MODULE._needs_repair({"transcriptV1Id": "v1", "summaryId": "s", "summaryStatus": "READY"})


def test_repair_runner_report_projection_has_no_content_fields():
    actions = MODULE._safe_actions(
        {
            "actions": [
                {
                    "stage": "SUMMARY",
                    "state": "QUEUED",
                    "reason": "DETERMINISTIC_ONLY",
                    "jobId": "job-1",
                    "transcriptId": "tr-1",
                    "text": "must never be copied",
                }
            ]
        }
    )
    assert actions == [
        {
            "stage": "SUMMARY",
            "state": "QUEUED",
            "reason": "DETERMINISTIC_ONLY",
            "jobId": "job-1",
            "transcriptId": "tr-1",
        }
    ]
