from pathlib import Path

from workers.memory_worker.entity_resolver import canonical_topic_name, normalize_entity_name, resolve_entities
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


def test_fact_extractor_keeps_explicit_subject_for_relations():
    facts = extract_memory_facts(
        [{"id": "seg-1", "startMs": 1000, "endMs": 2000, "text": "Ответственным за ремонт второй печи назначен Иванов."}],
        owner_user_id="owner-a",
        meeting_id="meeting-a",
        transcript_id="transcript-a",
        transcript_version=2,
    )
    assert facts[0].subject == "ремонт второй печи"


def test_fact_extractor_does_not_guess_subject_from_ambiguous_decision():
    facts = extract_memory_facts(
        [{"id": "seg-1", "startMs": 1000, "endMs": 2000, "text": "Решили перенести срок."}],
        owner_user_id="owner-a",
        meeting_id="meeting-a",
        transcript_id="transcript-a",
        transcript_version=2,
    )
    assert facts[0].subject is None


def test_entities_are_normalized_without_cross_owner_global_space():
    assert normalize_entity_name("Печь №2") == "печь №2"
    facts = [_fact("f1", "m1", "RESPONSIBLE", "Иванов", 10)]
    entities = resolve_entities(facts)
    assert entities[0].entity_type == "PERSON"
    assert entities[0].normalized_name == "иванов"


def test_subject_entities_include_equipment_aliases():
    facts = [_fact("f1", "m1", "DEADLINE", "30 августа", 10, subject="ремонта насоса")]
    entities = resolve_entities(facts)
    assert any(entity.entity_type == "TOPIC" and entity.normalized_name == "ремонт насос" for entity in entities)
    equipment = next(entity for entity in entities if entity.entity_type == "EQUIPMENT")
    assert "ремонта насоса" in equipment.aliases
    assert "ремонт насос" in equipment.aliases


def test_subject_aliases_share_one_cross_meeting_key():
    assert canonical_topic_name("ремонт второй печи") == "ремонт печь №2"
    assert canonical_topic_name("ремонта печи №2") == "ремонт печь №2"
    assert canonical_topic_name("ремонт 2-я печь") == "ремонт печь №2"


def test_relation_and_current_state_select_latest_superseding_fact():
    old = _fact("old", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    new = _fact("new", "m2", "DEADLINE", "30 августа", 10, subject="ремонт печи")
    relations = resolve_relations([old, new], {"m1": 1, "m2": 2})
    assert relations[0].relation_type == "CONTRADICTS"
    # A supersedes relation is explicit-marker dependent; without one both
    # values remain visible rather than silently choosing a winner.
    assert {fact.fact_id for fact in current_state([old, new], relations)} == {"old", "new"}


def test_equal_repeated_value_is_confirmation_not_conflict():
    first = _fact("first", "m1", "DEADLINE", "30 августа", 10, subject="ремонт печи")
    second = _fact("second", "m2", "DEADLINE", "30   августа", 10, subject="ремонт печи")
    relations = resolve_relations([first, second], {"m1": 1, "m2": 2})
    assert relations[0].relation_type == "CONFIRMS"
    assert build_threads([first, second], relations)[0].state == "OPEN"


def test_explicit_supersedes_marker_allows_current_state_projection():
    old = _fact("old", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    new = _fact("new", "m2", "DEADLINE", "30 августа", 10, subject="ремонт печи")
    new = MemoryFact(**{**new.__dict__, "source_text": "Срок теперь 30 августа."})
    relations = resolve_relations([old, new], {"m1": 1, "m2": 2})
    assert relations[0].relation_type == "SUPERSEDES"
    assert [fact.fact_id for fact in current_state([old, new], relations)] == ["new"]


def test_later_explicit_change_resolves_previous_conflict():
    first = _fact("first", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    conflicting = _fact("conflicting", "m2", "DEADLINE", "30 августа", 10, subject="ремонт печи")
    final = MemoryFact(**{**_fact("final", "m3", "DEADLINE", "5 сентября", 10, subject="ремонт печи").__dict__, "source_text": "Срок теперь 5 сентября."})
    relations = resolve_relations([first, conflicting, final], {"m1": 1, "m2": 2, "m3": 3})
    assert any(item.relation_type == "CONTRADICTS" for item in relations)
    assert any(item.relation_type == "SUPERSEDES" for item in relations)
    assert build_threads([first, conflicting, final], relations)[0].state == "OPEN"
    assert [fact.fact_id for fact in current_state([first, conflicting, final], relations, {"m1": "2026-08-01", "m2": "2026-08-02", "m3": "2026-08-03"})] == ["final"]


def test_current_state_groups_normalized_subject_aliases():
    old = _fact("old", "m1", "DEADLINE", "25 августа", 10, subject="ремонт второй печи")
    new = _fact("new", "m2", "DEADLINE", "30 августа", 10, subject="ремонта печи №2")
    new = MemoryFact(**{**new.__dict__, "source_text": "Срок теперь 30 августа."})
    relations = resolve_relations([old, new], {"m1": 1, "m2": 2})
    assert [fact.fact_id for fact in current_state([old, new], relations)] == ["new"]


def test_relations_never_link_unrelated_topics_by_fact_type_only():
    first = _fact("first", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    second = _fact("second", "m2", "DEADLINE", "30 августа", 10, subject="поставка насоса")
    assert resolve_relations([first, second], {"m1": 1, "m2": 2}) == ()


def test_relations_never_cross_owner_even_for_same_topic():
    first = _fact("first", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    second = MemoryFact(**{**_fact("second", "m2", "DEADLINE", "30 августа", 10, subject="ремонт печи").__dict__, "owner_user_id": "other-owner"})
    assert resolve_relations([first, second], {"m1": 1, "m2": 2}) == ()


def test_relations_use_conservative_topic_normalization():
    first = _fact("first", "m1", "DEADLINE", "25 августа", 10, subject="ремонт второй печи")
    second = _fact("second", "m2", "DEADLINE", "30 августа", 10, subject="ремонта печи №2")
    assert len(resolve_relations([first, second], {"m1": 1, "m2": 2})) == 1


def test_sequential_cross_meeting_projection_keeps_full_thread():
    first = _fact("first", "m1", "DEADLINE", "25 августа", 10, subject="ремонт второй печи")
    second = MemoryFact(**{**_fact("second", "m2", "DEADLINE", "30 августа", 10, subject="ремонта печи №2").__dict__, "source_text": "Срок теперь 30 августа."})
    relations = resolve_relations([first, second], {"m1": 1, "m2": 2})
    threads = build_threads([first, second], relations)
    assert relations[0].source_fact_id == "first"
    assert relations[0].target_fact_id == "second"
    assert relations[0].relation_type == "SUPERSEDES"
    assert threads[0].fact_ids == ("first", "second")


def test_conflict_does_not_select_a_winner():
    first = _fact("first", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    second = _fact("second", "m2", "DEADLINE", "30 августа", 10, subject="ремонт печи")
    relations = resolve_relations([first, second], {"m1": 1, "m2": 2})
    threads = build_threads([first, second], relations)
    assert relations[0].relation_type == "CONTRADICTS"
    assert threads[0].state == "CONFLICTED"


def test_task_completion_closes_thread():
    task = _fact("task", "m1", "TASK", "Проверить насос", 10, subject="ремонт насоса")
    done = MemoryFact(**{**_fact("done", "m2", "STATUS", "Задача выполнена", 10, subject="ремонта насоса").__dict__, "source_text": "Задача выполнена."})
    relations = resolve_relations([task, done], {"m1": 1, "m2": 2})
    threads = build_threads([task, done], relations)
    assert relations[0].relation_type == "CLOSES"
    assert threads[0].state == "RESOLVED"


def test_negated_completion_does_not_close_thread():
    task = _fact("task", "m1", "TASK", "Подготовить ведомость", 10, subject="ремонт насоса")
    not_done = MemoryFact(**{**_fact("not-done", "m2", "STATUS", "Ведомость не подготовлена", 10, subject="ремонта насоса").__dict__, "source_text": "Ведомость не подготовлена."})
    relations = resolve_relations([task, not_done], {"m1": 1, "m2": 2})
    assert all(item.relation_type != "CLOSES" for item in relations)
    assert build_threads([task, not_done], relations)[0].state == "OPEN"


def test_completed_task_can_be_reopened_by_latest_fact():
    task = _fact("task", "m1", "TASK", "Проверить насос", 10, subject="ремонт насоса")
    done = MemoryFact(**{**_fact("done", "m2", "STATUS", "Задача выполнена", 10, subject="ремонта насоса").__dict__, "source_text": "Задача выполнена."})
    reopened = MemoryFact(**{**_fact("reopened", "m3", "TASK", "Снова проверить насос", 10, subject="ремонта насоса").__dict__, "source_text": "Проблему обнаружили снова, нужно повторно проверить насос."})
    relations = resolve_relations([task, done, reopened], {"m1": 1, "m2": 2, "m3": 3})
    thread = build_threads([task, done, reopened], relations)[0]
    assert thread.state == "REOPENED"


def test_negated_change_does_not_create_supersedes():
    old = _fact("old", "m1", "DEADLINE", "25 августа", 10, subject="ремонт печи")
    unchanged = MemoryFact(**{**_fact("same", "m2", "DEADLINE", "30 августа", 10, subject="ремонт печи").__dict__, "source_text": "Срок не перенесли, он 30 августа."})
    relations = resolve_relations([old, unchanged], {"m1": 1, "m2": 2})
    assert relations[0].relation_type == "CONTRADICTS"


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


def test_first_seen_memory_questions_use_chronological_plan():
    plan = build_memory_query_plan("FACT_LOOKUP", "Когда впервые обсуждали замену двигателя?", "двигатель")
    assert plan.temporal_mode == "FIRST_SEEN"
    assert plan.include_superseded is True


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
    runtime = (root / "056_memory_runtime.sql").read_text(encoding="utf-8")
    projection = (root / "057_memory_cross_meeting_projection.sql").read_text(encoding="utf-8")
    normalizer = (root / "058_memory_normalizer_version.sql").read_text(encoding="utf-8")
    assert "owner_user_id" in entities and "fact_entities" in entities
    assert "invalidated_at" in relations and "derivation_type" in relations
    assert "memory_thread_facts" in threads
    assert "UNIQUE(transcript_id, transcript_version)" in jobs
    assert "invalidated_by_version" in invalidation
    assert "lease_expires_at" in runtime and "invalidate_memory_projection_for_fact" in runtime
    assert "subject_normalized" in projection and "ix_transcript_facts_owner_active_subject" in projection
    assert "subject_normalizer_version" in normalizer and "ix_transcript_facts_memory_normalizer" in normalizer


def test_assistant_uses_memory_index_then_safe_transcript_fallback():
    source = (Path(__file__).resolve().parents[1] / "workers" / "summary_worker" / "assistant.py").read_text(encoding="utf-8")
    assert "self.repository.memory_context" in source
    assert "MEMORY_INDEX_CANONICAL_REHYDRATION" in source
    assert "self.repository.context_for_plan" in source
    memory_sql = source[source.index("SELECT f.id,f.meeting_id"):source.index("SELECT s.id,t.meeting_id", source.index("SELECT f.id,f.meeting_id"))]
    assert "m.owner_id=%s::uuid" in memory_sql
    assert "f.state='ACTIVE'" in memory_sql
    assert "f.subject_normalized=%s" in memory_sql
    assert "_merge_memory_and_transcript_context" in source


def test_memory_runtime_is_wired_without_gpu_dependency():
    root = Path(__file__).resolve().parents[1]
    worker = (root / "workers" / "memory_worker" / "worker.py").read_text(encoding="utf-8")
    compose = (root / "compose.dev.yml").read_text(encoding="utf-8")
    outbox = (root / "workers" / "outbox_relay" / "worker.py").read_text(encoding="utf-8")
    assert "memory.index" in worker and "memory-worker" in compose
    assert "recover_expired_leases" in worker
    assert "recover_starved_jobs" in worker
    assert '"memory.index"' in outbox
    assert "GPU" not in worker.split("async def run", 1)[0]


def test_memory_worker_payload_is_owner_and_version_scoped():
    worker = (Path(__file__).resolve().parents[1] / "workers" / "memory_worker" / "worker.py").read_text(encoding="utf-8")
    assert "memory_payload_scope_mismatch" in worker
    assert "f.transcript_version=%s" in worker
    assert "COALESCE(f.owner_user_id,m.owner_id)=j.owner_user_id" in worker
