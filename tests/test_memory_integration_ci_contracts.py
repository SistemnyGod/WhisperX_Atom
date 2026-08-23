from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_ci_applies_migrations_and_requires_the_explicit_memory_database():
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    helper = (ROOT / "scripts" / "apply-test-migrations.py").read_text(encoding="utf-8")
    integration = (ROOT / "tests" / "test_memory_postgres_integration.py").read_text(encoding="utf-8")

    assert "WHISPERX_TEST_DATABASE" in workflow
    assert "WHISPERX_REQUIRE_MEMORY_INTEGRATION" in workflow
    assert "python scripts/apply-test-migrations.py" in workflow
    assert "tests/test_memory_postgres_integration.py" in workflow
    assert "schema_migration_checksums" in helper
    assert "057_memory_cross_meeting_projection" in integration
    assert "pytest.fail" in integration
