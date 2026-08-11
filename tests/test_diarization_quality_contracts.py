from diarization_quality import normalize_speaker_label, score_diarization_result


def test_unknown_speaker_is_not_a_registry_label():
    assert normalize_speaker_label("UNKNOWN") is None
    assert normalize_speaker_label("Не определён") is None
    assert normalize_speaker_label("SPEAKER_00") == "SPEAKER_00"


def test_diarization_score_penalizes_unassigned_turns():
    score = score_diarization_result(
        [{"start": 0, "end": 2, "text": "текст", "speaker": "UNKNOWN"}],
        [{"start": 0, "end": 2, "speaker": "SPEAKER_00"}],
        "diar",
        1,
        2,
    )
    assert score.unknown_ratio == 1.0
    assert "unknown_speaker_ratio" in score.reasons
