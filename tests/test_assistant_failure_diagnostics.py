from workers.summary_worker.assistant import assistant_failure_code, assistant_failure_fingerprint


def test_unexpected_internal_errors_use_stable_safe_code():
    assert assistant_failure_code(AttributeError("question text must not be persisted")) == "ASSISTANT_INTERNAL_ERROR"


def test_failure_fingerprint_excludes_exception_message():
    first = assistant_failure_fingerprint(ValueError("secret question one"), "GROUNDING")
    second = assistant_failure_fingerprint(ValueError("secret question two"), "GROUNDING")
    assert first == second
    assert len(first) == 24


def test_general_chat_grounding_reads_repository_metadata():
    source = open("workers/summary_worker/assistant.py", encoding="utf-8").read()
    assert "getattr(self.repository, \"_last_retrieval_metadata\", {})" in source
