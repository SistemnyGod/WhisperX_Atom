from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_ci_has_the_four_required_non_gpu_checks():
    workflow = read(".github/workflows/ci.yml")
    for job in (
        "dotnet-build:",
        "python-targeted-tests:",
        "migration-validation:",
        "desktop-resource-check:",
    ):
        assert job in workflow
    assert "optional-package-installer:" in workflow
    assert "workflow_dispatch" in workflow
    assert "cuda" not in workflow.lower()
    assert "pip install whisperx" not in workflow.lower()
    assert "python -m whisperx" not in workflow.lower()
    assert "qwen" not in workflow.lower()


def test_migration_job_uses_a_clean_postgres_and_filename_order():
    workflow = read(".github/workflows/ci.yml")
    assert "postgres:17-alpine" in workflow
    assert "find apps/server/WhisperX.Atom.Api/Migrations" in workflow
    assert "| sort" in workflow
    assert "ON_ERROR_STOP=1" in workflow


def test_desktop_resource_check_is_a_checked_in_script():
    workflow = read(".github/workflows/ci.yml")
    script = read("scripts/test-desktop-resources.ps1")
    assert "./scripts/test-desktop-resources.ps1" in workflow
    assert "XAML_DUPLICATE_KEY" in script
    assert "XAML_ENCODING_REPLACEMENT_CHARACTER" in script
