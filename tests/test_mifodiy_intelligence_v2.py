from workers.summary_worker.answer_planner import assess_requested_fields, build_answer_plan
from workers.summary_worker.evidence_reasoner import detect_conflicts
from workers.summary_worker.evidence_bundles import build_evidence_bundles
from workers.summary_worker.fact_extraction import extract_transcript_facts
from workers.summary_worker.intelligence_qa import CASE_COUNTS, evaluate_intent_cases, generate_reasoning_cases, validate_reasoning_corpus
from pathlib import Path
from workers.summary_worker.query_understanding import AssistantQueryPlan, _cosine, understand_query
from workers.summary_worker.retrieval_planner import build_retrieval_plan


def test_query_understanding_classifies_required_fields_without_scope_resolution():
    plan = understand_query("Кто отвечает за ремонт второй печи?")
    assert plan.intent == "RESPONSIBLE"
    assert plan.requested_fields == ("responsible",)
    assert plan.topic and "ремонт" in plan.topic
    assert plan.confidence >= 0.9


def test_semantic_similarity_is_scale_invariant_and_rejects_zero_vectors():
    assert _cosine((10.0, 0.0), (1.0, 0.0)) == 1.0
    assert _cosine((1.0, 0.0), (0.0, 1.0)) == 0.0
    assert _cosine((0.0, 0.0), (1.0, 0.0)) == 0.0


def test_follow_up_inherits_topic_but_changes_requested_field():
    previous = understand_query("Что решили по ремонту второй печи?")
    follow_up = understand_query("А кто отвечает?", previous)
    assert previous.intent == "DECISION"
    assert follow_up.intent == "RESPONSIBLE"
    assert follow_up.follow_up is True
    assert follow_up.topic == previous.topic


def test_cause_query_expands_neighbour_window_and_requires_explicit_causality():
    plan = understand_query("Почему перенесли ремонт?")
    answer_plan = build_answer_plan(plan)
    assert plan.intent == "CAUSE"
    assert plan.neighbour_window == 4
    assert "causal_link_must_be_explicit" in answer_plan["policy"]


def test_deadline_conflict_is_reported_without_selecting_a_winner():
    plan = understand_query("Какой срок ремонта?")
    valid = {
        "1": ("meeting", 1000, 2000, "Сделаем до 25 августа.", "V1", "transcript", 1),
        "2": ("meeting", 3000, 4000, "Позже назвали срок 30 августа.", "V1", "transcript", 1),
    }
    conflicts = detect_conflicts(valid, plan)
    assert len(conflicts) == 2
    assert build_answer_plan(plan, conflicts)["answerType"] == "CONTRADICTION"


def test_plan_round_trip_is_additive_and_does_not_turn_state_into_evidence():
    plan = understand_query("Кому поручили подготовить ведомость?")
    restored = AssistantQueryPlan.from_mapping(plan.to_dict())
    assert restored is not None
    assert restored.intent == plan.intent
    assert restored.topic == plan.topic
    assert "evidence" not in str(restored.to_dict()).lower()


def test_multi_field_question_can_be_marked_partial_without_inventing_missing_value():
    plan = understand_query("Кто отвечает и какой срок ремонта?")
    valid = {
        "1": ("meeting", 1000, 2000, "Ответственным назначен Иванов.", "V1", "transcript", 1),
    }
    supported, missing = assess_requested_fields(plan, valid, ["1"])
    assert plan.answer_type == "MULTI_FACT"
    assert supported == ["responsible"]
    assert missing == ["deadline"]


def test_multi_field_topic_removes_question_scaffolding_before_retrieval():
    plan = understand_query("Что решили и кто отвечает за ремонт второй печи?")
    assert plan.intent == "DECISION"
    assert plan.requested_fields == ("decision", "responsible")
    assert plan.topic == "за ремонт второй печи"


def test_retrieval_plan_keeps_comparison_as_explicit_two_topic_intent():
    plan = understand_query("Чем отличаются вариант А и вариант Б?")
    retrieval = build_retrieval_plan(plan, "Чем отличаются вариант А и вариант Б?")
    assert retrieval.intent == "COMPARISON"
    assert retrieval.split_topics == ("чем отличаются вариант а", "вариант б?")
    assert retrieval.neighbour_window == 2


def test_generic_follow_up_inherits_previous_intent_without_using_answer_as_evidence():
    previous = understand_query("Что решили по ремонту?")
    follow_up = understand_query("А подробнее?", previous)
    assert follow_up.follow_up is True
    assert follow_up.intent == "DECISION"
    assert follow_up.topic == previous.topic


def test_evidence_bundle_keeps_segment_ids_and_candidate_facts_source_bound():
    plan = understand_query("Кто отвечает за ремонт?")
    valid = {
        "seg-1": ("meeting-a", 1000, 2000, "Ответственным назначен Иванов.", "ENRICHED", "tr-1", 2),
        "seg-2": ("meeting-a", 2000, 3000, "Срок до 25 августа.", "ENRICHED", "tr-1", 2),
    }
    bundles = build_evidence_bundles(valid, plan)
    assert len(bundles) == 1
    assert bundles[0].segment_ids == ("seg-1", "seg-2")
    assert bundles[0].candidate_facts["responsible"][0]["evidenceSegmentIds"] == ["seg-1"]


def test_fact_extraction_is_explicit_and_versioned():
    facts = extract_transcript_facts(
        [{"id": "seg-1", "startMs": 1000, "endMs": 2500, "text": "Ответственным назначен Иванов. Срок до 25 августа."}],
        meeting_id="meeting-a",
        transcript_id="transcript-v2",
        transcript_version=2,
    )
    assert {fact.fact_type for fact in facts} == {"RESPONSIBLE", "DEADLINE"}
    assert next(fact for fact in facts if fact.fact_type == "RESPONSIBLE").value == "Иванов"
    assert all(fact.transcript_version == 2 for fact in facts)
    assert all(fact.evidence_segment_ids == ("seg-1",) for fact in facts)


def test_fact_extraction_does_not_infer_cause_across_separate_segments():
    facts = extract_transcript_facts(
        [
            {"id": "seg-1", "startMs": 1000, "endMs": 1500, "text": "Двигатель не приехал."},
            {"id": "seg-2", "startMs": 40000, "endMs": 41000, "text": "Переносим ремонт на неделю."},
        ],
        meeting_id="meeting-a",
        transcript_id="transcript-v1",
        transcript_version=1,
    )
    assert not any(fact.fact_type == "CAUSE" for fact in facts)


def test_synthetic_reasoning_corpus_matches_explicit_category_matrix_without_text_artifacts():
    cases = generate_reasoning_cases()
    validation = validate_reasoning_corpus(cases)
    assert validation["valid"] is True
    assert validation["caseCount"] == sum(CASE_COUNTS.values()) == 400
    evaluation = evaluate_intent_cases(cases)
    assert evaluation["accuracy"] == 1.0
    assert all("question" not in item and "answer" not in item for item in evaluation["results"])


def test_fact_and_conversation_migrations_are_additive_and_version_safe():
    root = Path(__file__).resolve().parents[1]
    facts_sql = (root / "apps/server/WhisperX.Atom.Api/Migrations/049_transcript_facts.sql").read_text(encoding="utf-8")
    state_sql = (root / "apps/server/WhisperX.Atom.Api/Migrations/050_assistant_conversation_state.sql").read_text(encoding="utf-8")
    assert "CREATE TABLE IF NOT EXISTS transcript_facts" in facts_sql
    assert "transcript_version" in facts_sql and "evidence_segment_ids" in facts_sql
    assert "INVALIDATED" in facts_sql and "NEW.version" in facts_sql
    assert "CREATE TABLE IF NOT EXISTS assistant_conversation_state" in state_sql
    assert "last_user_question" in state_sql and "assistant_messages" not in state_sql
