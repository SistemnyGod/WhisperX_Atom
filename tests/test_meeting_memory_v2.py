from pathlib import Path

from workers.memory_worker.entity_resolver import normalize_entity_name, resolve_entities
from workers.memory_worker.fact_extractor import extract_memory_facts
from workers.memory_worker.indexer import build_memory_index
from workers.memory_worker.memory_retrieval import build_memory_query_plan, rehydrate_evidence
from workers.memory_worker.models import MemoryFact
from workers.memory_worker.relation_resolver import resolve_relations
from workers.memory_worker.temporal_resolver import current_state, timeline
from workers.memory_worker.thread_builder import build_threads
from workers.memory_worker.worker import MemoryIndexWorker


def _fact(fact_id, meeting_id, fact_type, value, start_ms, subject=None, state="ACTIVE"):
    return MemoryFact(
        fact_id=fact_id,
        owner_user_id="owner-a",
        meeting_id=meeting_id,
        transcript_id=f"transcript-{meeting_id}",
        transcript_version=2,
        fact_type=fact_type,
        subject=subject,
        value=value,
        start_ms=start_ms,
        end_ms=start_ms + 1000,
        confidence=0.9,
        evidence_segment_ids=(f"seg-{fact_id}",),
        state=state,
    )


def test_memory_facts_are_explicit_and_keep_canonical_evidence():
    facts = extract_memory_facts(
        [{"id": "seg-1", "startMs": 1000, "endMs": 2000, "text": "Ответственным назначен Иванов."}],
        owner_user_id="owner-a",
        meeting_id="meeting-a",
        transcript_id="transcript-a",
        transcript_version=2,
    )
    assert len(facts) == 1
    assert facts[0].fact_type == "RESPONSIBLE"
    assert facts[0].derivation_type == "EXPLICIT"
    assert facts[0].evidence_segment_ids == ("seg-1",)


def test_entities_are_normalized_without_cross_owner_global_space():
    assert normalize_entity_name("Печь №2") == "печь №2"
    facts = [_fact("f1", "m1", "RESPONSIBLE", "Иванов", 10)]
    entities = resolve_entities(facts)
    assert entities[0].entity_type == "PERSON"
    assert entities[0].normalized_name == "иванов"


def test_relation_and_current_state_select_latest_superseding_fact():
    old = _fact("old", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    new = _fact("new", "m2", "DEADLINE", "30 августа", 10, subject="ремонт печи")
    relations = resolve_relations([old, new], {"m1": 1, "m2": 2})
    assert relations[0].relation_type == "CONTRADICTS"
    # A supersedes relation is explicit-marker dependent; without one both
    # values remain visible rather than silently choosing a winner.
    assert {fact.fact_id for fact in current_state([old, new], relations)} == {"old", "new"}


def test_explicit_supersedes_marker_allows_current_state_projection():
    old = _fact("old", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    new = _fact("new", "m2", "DEADLINE", "30 августа", 10, subject="ремонт печи")
    new = MemoryFact(**{**new.__dict__, "source_text": "Срок теперь 30 августа."})
    relations = resolve_relations([old, new], {"m1": 1, "m2": 2})
    assert relations[0].relation_type == "SUPERSEDES"
    assert [fact.fact_id for fact in current_state([old, new], relations)] == ["new"]


def test_relations_never_link_unrelated_topics_by_fact_type_only():
    first = _fact("first", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    second = _fact("second", "m2", "DEADLINE", "30 августа", 10, subject="поставка насоса")
    assert resolve_relations([first, second], {"m1": 1, "m2": 2}) == ()


def test_timeline_uses_meeting_dates_not_segment_start_only():
    first = _fact("first", "m-first", "DECISION", "заказать двигатель", 5000)
    second = _fact("second", "m-second", "DECISION", "перенести ремонт", 1000)
    ordered = timeline([second, first], {"m-first": "2026-08-12", "m-second": "2026-08-18"})
    assert [fact.fact_id for fact in ordered] == ["first", "second"]


def test_memory_query_plan_and_rehydration_fail_closed():
    plan = build_memory_query_plan("TIMELINE", "Как менялся срок ремонта?", "ремонт")
    assert plan.temporal_mode == "HISTORY"
    assert plan.include_superseded is True
    facts = [_fact("a", "m1", "DEADLINE", "25 августа", 10), _fact("b", "m2", "DEADLINE", "30 августа", 10, state="INVALIDATED")]
    assert rehydrate_evidence(facts, {"seg-a", "seg-foreign"}) == ("seg-a",)


def test_threads_ignore_invalidated_facts_and_keep_ids():
    facts = [_fact("f1", "m1", "TASK", "Подготовить ведомость", 10, subject="ремонт печи"), _fact("f2", "m2", "TASK", "Старое поручение", 10, subject="старый", state="INVALIDATED")]
    threads = build_threads(facts)
    assert len(threads) == 1
    assert threads[0].fact_ids == ("f1",)


def test_indexer_is_idempotent_in_shape_and_never_accepts_fact_without_evidence():
    facts = [_fact("f1", "m1", "RESPONSIBLE", "Иванов", 10)]
    first = build_memory_index(facts, meeting_order={"m1": 1})
    second = build_memory_index(facts, meeting_order={"m1": 1})
    assert first.diagnostics() == second.diagnostics()
    assert MemoryIndexWorker().index(facts).status == "READY"
    bad = MemoryFact(**{**_fact("bad", "m1", "TASK", "нет источника", 10).__dict__, "evidence_segment_ids": ()})
    assert MemoryIndexWorker().index([bad]).status == "NEEDS_REVIEW"


def test_memory_migrations_are_additive_and_owner_scoped():
    root = Path(__file__).resolve().parents[1] / "apps" / "server" / "WhisperX.Atom.Api" / "Migrations"
    entities = (root / "051_memory_entities.sql").read_text(encoding="utf-8")
    relations = (root / "052_memory_fact_relations.sql").read_text(encoding="utf-8")
    threads = (root / "053_memory_threads.sql").read_text(encoding="utf-8")
    jobs = (root / "054_memory_jobs.sql").read_text(encoding="utf-8")
    invalidation = (root / "055_memory_invalidation.sql").read_text(encoding="utf-8")
    assert "owner_user_id" in entities and "fact_entities" in entities
    assert "invalidated_at" in relations and "derivation_type" in relations
    assert "memory_thread_facts" in threads
    assert "UNIQUE(transcript_id, transcript_version)" in jobs
    assert "invalidated_by_version" in invalidation


def test_assistant_uses_memory_index_then_safe_transcript_fallback():
    source = (Path(__file__).resolve().parents[1] / "workers" / "summary_worker" / "assistant.py").read_text(encoding="utf-8")
    assert "self.repository.memory_context" in source
    assert "MEMORY_INDEX_CANONICAL_REHYDRATION" in source
    assert "self.repository.context_for_plan" in source
    memory_sql = source[source.index("SELECT f.id,f.meeting_id"):source.index("SELECT s.id,t.meeting_id", source.index("SELECT f.id,f.meeting_id"))]
    assert "m.owner_id=%s::uuid" in memory_sql
    assert "f.state='ACTIVE'" in memory_sql
