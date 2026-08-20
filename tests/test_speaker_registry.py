import math
from pathlib import Path

from workers.ml_worker.speaker_registry import (
    cosine_similarity,
    default_display_label,
    match_profile,
    normalize_embedding,
    update_centroid,
)


def test_embedding_validation_rejects_empty_nonfinite_and_oversized_vectors():
    assert normalize_embedding([]) is None
    assert normalize_embedding([1.0, math.inf]) is None
    assert normalize_embedding([1.0, "bad"]) is None
    assert normalize_embedding([1.0] * 2049) is None
    assert normalize_embedding([1.0, 0.0]) == (1.0, 0.0)


def test_cosine_and_weighted_centroid_are_deterministic():
    assert cosine_similarity((1, 0), (1, 0)) == 1.0
    assert cosine_similarity((1, 0), (0, 1)) == 0.0
    centroid = update_centroid((1, 0), (0, 1), 1)
    assert centroid is not None
    assert abs(centroid[0] - centroid[1]) < 1e-9


def test_high_confidence_match_requires_margin():
    result = match_profile([1, 0], [{"id": "ivan", "embedding_centroid": [1, 0]}, {"id": "petr", "embedding_centroid": [0, 1]}])
    assert result.status == "MATCHED"
    assert result.profile_id == "ivan"
    assert result.confidence == 1.0


def test_ambiguous_or_low_confidence_is_only_a_suggestion():
    result = match_profile([1, 0], [{"id": "ivan", "embedding_centroid": [1, 0]}, {"id": "petr", "embedding_centroid": [0.99, 0.1]}])
    assert result.status == "SUGGESTION"
    assert result.profile_id is None
    assert result.suggestion_profile_id in {"ivan", "petr"}


def test_missing_embedding_keeps_human_safe_label():
    result = match_profile(None, [{"id": "ivan", "embedding_centroid": [1, 0]}])
    assert result.status == "UNMATCHED"
    assert default_display_label("SPEAKER_03") == "Спикер 3"


def test_persistence_keeps_profile_mapping_out_of_v1_and_records_v2_metadata():
    source = Path("workers/ml_worker/persistence.py").read_text(encoding="utf-8")
    assert "speaker_profiles" in source
    assert "profile_match_status" in source
    assert "DIARIZATION_EMBEDDING_MISSING" in Path("workers/ml_worker/speaker_registry.py").read_text(encoding="utf-8")
    assert "UPDATE transcripts SET quality_metadata" in source
