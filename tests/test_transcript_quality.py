from whisperx_atom.transcript_quality import (
    TranscriptQualityThresholds,
    build_transcript_quality_report,
    compare_transcript_quality,
    quality_gate,
)


def _result(texts, *, end=10.0, words=True):
    segments = []
    for index, text in enumerate(texts):
        start = float(index) * (end / len(texts))
        finish = float(index + 1) * (end / len(texts))
        segment = {"start": start, "end": finish, "text": text}
        if words:
            segment["words"] = [{"word": word, "start": start, "end": finish, "score": 0.9} for word in text.split()]
        segments.append(segment)
    return {"segments": segments}


def test_empty_transcript_is_not_ready():
    report = build_transcript_quality_report({"segments": []}, 10)
    gate = quality_gate(report)
    assert gate["valid"] is False
    assert "TRANSCRIPT_EMPTY" in report.reasons


def test_good_transcript_is_ready():
    report = build_transcript_quality_report(_result(["Привет мир", "Проверка записи"]), 10)
    gate = quality_gate(report)
    assert gate["valid"] is True
    assert gate["ready"] is True
    assert report.word_count == 4
    assert report.quality_score >= 85


def test_early_transcript_is_retryable_and_warned():
    report = build_transcript_quality_report(_result(["только начало"], end=10), 100)
    gate = quality_gate(report)
    assert gate["valid"] is True
    assert gate["retryable"] is True
    assert "TRANSCRIPT_LOW_COVERAGE" in report.reasons


def test_better_fallback_is_selected():
    thresholds = TranscriptQualityThresholds(max_trailing_gap_seconds=5, min_coverage=0.5)
    primary = _result(["короткий фрагмент"], end=5)
    fallback = _result(["полный текст записи", "вторая часть записи"], end=10)
    selected, selected_pass, primary_report, fallback_report = compare_transcript_quality(primary, fallback, 10, thresholds)
    assert selected is fallback
    assert selected_pass == "fallback"
    assert fallback_report.reasons == []
    assert primary_report.reasons


def test_non_monotonic_timestamps_are_invalid():
    report = build_transcript_quality_report(
        {"segments": [{"start": 2, "end": 3, "text": "один"}, {"start": 1, "end": 2, "text": "два"}]},
        5,
    )
    gate = quality_gate(report)
    assert gate["valid"] is False
    assert "TRANSCRIPT_NON_MONOTONIC" in report.reasons
