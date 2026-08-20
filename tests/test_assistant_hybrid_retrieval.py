from workers.summary_worker.hybrid_retrieval import (
    HashedEmbeddingProvider,
    HybridRetriever,
    RetrievalCandidate,
    normalized_tokens,
)


def candidate(segment_id: str, ordinal: int, text: str, meeting: str = "meeting-a") -> RetrievalCandidate:
    return RetrievalCandidate(
        segment_id=segment_id,
        meeting_id=meeting,
        transcript_id="transcript-1",
        transcript_version=2,
        start_ms=ordinal * 1000,
        end_ms=(ordinal + 1) * 1000,
        speaker="Спикер 01",
        text=text,
        version_kind="ENRICHED",
        ordinal=ordinal,
    )


def test_russian_normalization_keeps_yo_and_common_inflections_equivalent():
    assert "ремонт" in normalized_tokens("ремонте")
    assert any(value.startswith("ответствен") for value in normalized_tokens("ответственного"))
    assert normalized_tokens("ёх") == normalized_tokens("ех")


def test_hybrid_embedding_recovers_paraphrase_without_fts_rank():
    retriever = HybridRetriever(HashedEmbeddingProvider())
    results = retriever.rank(
        "Кто отвечает за починку?",
        [
            candidate("target", 0, "Ответственный назначен Иваном."),
            candidate("noise", 1, "Погода и кофе."),
        ],
    )
    assert results
    assert results[0].candidate.segment_id == "target"
    assert results[0].fts_score == 0
    assert results[0].embedding_score >= 0.60


def test_neighbours_stay_inside_same_meeting_and_transcript():
    retriever = HybridRetriever(HashedEmbeddingProvider())
    anchor = candidate("anchor", 1, "Решили перенести ремонт на пятницу.")
    same_before = candidate("before", 0, "Обсудили причины задержки.")
    same_after = candidate("after", 2, "Ответственный подтвердил дату.")
    other_meeting = candidate("foreign", 2, "Ремонт в другой встрече.", meeting="meeting-b")
    selected = retriever.expand_neighbours(
        retriever.rank("Что решили по ремонту?", [anchor]),
        [anchor, same_before, same_after, other_meeting],
    )
    assert {item.segment_id for item in selected} == {"before", "anchor", "after"}


def test_ranking_is_deterministic_for_equal_candidates():
    retriever = HybridRetriever(HashedEmbeddingProvider())
    values = [candidate("b", 1, "План встречи."), candidate("a", 0, "План встречи.")]
    first = [item.candidate.segment_id for item in retriever.rank("план", values)]
    second = [item.candidate.segment_id for item in retriever.rank("план", values)]
    assert first == second


def test_assistant_worker_uses_hybrid_before_snapshot_and_keeps_quality_gate():
    source = ("workers/summary_worker/assistant.py")
    text = open(source, encoding="utf-8").read()
    assert "HybridRetriever" in text
    assert "self._hybrid.rank" in text
    assert "self._hybrid.expand_neighbours" in text
    assert "t.status IN ('READY','PARTIAL_READY')" in text
    assert "self.repository.snapshot_evidence, query_id, valid" in text
