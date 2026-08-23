from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_verify_runner_uses_locked_pytest_runtime_instead_of_unittest_discovery():
    script = (ROOT / "scripts" / "verify.ps1").read_text(encoding="utf-8")
    assert 'py -3.12 -m pytest -q' in script
    assert 'import pytest' in script
    assert 'unittest discover' not in script
    assert 'PYTHON_TEST_RUNTIME_MISSING' in script
