from workers.summary_worker.deterministic_summary import DETERMINISTIC_SUMMARY_MODEL, build_deterministic_summary
from workers.summary_worker.summarizer import TranscriptSegment


def test_deterministic_summary_exposes_only_canonical_evidence():
    result = build_deterministic_summary([
        TranscriptSegment("seg-1", 0, 1000, "Иван", "Решили перенести ремонт на пятницу", ("DECISION",)),
        TranscriptSegment("seg-2", 1000, 2000, "Ольга", "Проверить насос до пятницы", ("ACTION_ITEM",)),
    ])

    assert result["prompt_version"] == DETERMINISTIC_SUMMARY_MODEL
    assert result["contentValidity"] == "NEEDS_REVIEW"
    assert result["generationState"] == "READY_WITH_WARNINGS"
    assert result["decisions"][0]["evidence_segment_ids"] == ["seg-1"]
    assert result["action_items"][0]["evidence_segment_ids"] == ["seg-2"]
    assert result["action_items"][0]["responsible"] is None
    assert result["action_items"][0]["deadline_text"] is None
    assert result["action_items"][0]["deadline_iso"] is None
    assert result["overview"] == result["summary"]


def test_protocol_fallback_keeps_protocol_shape_and_warning_state():
    result = build_deterministic_summary([
        TranscriptSegment("seg-1", 0, 1000, "Иван", "Решили перенести ремонт на пятницу", ("DECISION",)),
    ], profile="MEETING_PROTOCOL_RU")

    assert result["profile"] == "MEETING_PROTOCOL_RU"
    assert result["quality"]["status"] == "NEEDS_REVIEW"
    assert result["questions_and_decisions"][0]["evidence_segment_ids"] == ["seg-1"]


def test_empty_transcript_cannot_create_fallback():
    import pytest

    with pytest.raises(ValueError, match="transcript_has_no_segments"):
        build_deterministic_summary([])
