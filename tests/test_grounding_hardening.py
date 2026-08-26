from workers.summary_worker.grounding import claims_are_semantically_grounded


def _valid(text: str):
    return {"seg-1": ("meeting-1", 0, 1000, text, "ENRICHED", "transcript-1", 1)}


def _result(claim: str, evidence_id: str = "seg-1", *, answer: str | None = None):
    return {
        "claims": [{"text": claim, "evidenceIds": [evidence_id]}],
        "answer": answer if answer is not None else claim,
        "voice_answer": answer if answer is not None else claim,
    }


def test_grounding_preserves_negation_polarity():
    evidence = "Ремонт насоса не переносим."
    assert claims_are_semantically_grounded(_result("Ремонт насоса не переносим."), _valid(evidence), "CURRENT_MEETING")
    assert not claims_are_semantically_grounded(_result("Ремонт насоса переносим."), _valid(evidence), "CURRENT_MEETING")


def test_grounding_does_not_accept_numeric_substring():
    evidence = "Срок установлен на 130 дней."
    assert not claims_are_semantically_grounded(_result("Срок установлен на 30 дней."), _valid(evidence), "CURRENT_MEETING")


def test_grounding_accepts_exact_numeric_value():
    evidence = "Срок установлен на 30 дней."
    assert claims_are_semantically_grounded(_result("Срок установлен на 30 дней."), _valid(evidence), "CURRENT_MEETING")
