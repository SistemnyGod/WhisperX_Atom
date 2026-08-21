"""Repository-wide pytest marker policy.

The project intentionally keeps most checks as fast source/contract tests.
Classifying them here makes CI selection explicit without requiring every
legacy test module to be edited.  A test can still opt into a more specific
marker with ``pytestmark``; explicit markers always win.
"""

from __future__ import annotations

import os
from pathlib import Path
import shutil
import tempfile
import uuid

import pytest


def pytest_configure(config: pytest.Config) -> None:
    """Use a repository-local writable temp root for Windows CI/acceptance.

    Some managed Windows profiles expose a short TEMP path that is readable but
    not writable by the test process.  ffmpeg and tempfile-based tests then
    fail before exercising product code.  Keep the override scoped to pytest
    and reset tempfile's cached directory so both Python and subprocesses use
    the same writable location.
    """

    root = Path.cwd() / ".pytest-runtime-temp"
    root.mkdir(parents=True, exist_ok=True)
    os.environ["TMP"] = str(root)
    os.environ["TEMP"] = str(root)
    os.environ["TMPDIR"] = str(root)
    tempfile.tempdir = str(root)

    # Python 3.14 creates Windows temp directories with an owner-only ACL
    # (mode 0700).  The managed test account cannot traverse those ACLs even
    # though the repository itself is writable.  Use the same unique naming
    # contract with an explicitly writable directory for this test process.
    def writable_mkdtemp(suffix=None, prefix=None, dir=None):
        parent = Path(dir or root)
        parent.mkdir(parents=True, exist_ok=True)
        prefix = "tmp" if prefix is None else prefix
        suffix = "" if suffix is None else suffix
        for _ in range(100):
            candidate = parent / f"{prefix}{uuid.uuid4().hex}{suffix}"
            try:
                candidate.mkdir(mode=0o777)
            except FileExistsError:
                continue
            return str(candidate.resolve())
        raise FileExistsError("unable to allocate writable temporary directory")

    tempfile.mkdtemp = writable_mkdtemp


@pytest.fixture
def tmp_path() -> Path:
    """Writable replacement for pytest's owner-only Windows tmp_path."""

    root = Path.cwd() / ".pytest-runtime-temp" / f"tmp-path-{uuid.uuid4().hex}"
    root.mkdir(parents=True, exist_ok=False)
    try:
        yield root
    finally:
        shutil.rmtree(root, ignore_errors=True)


def pytest_collection_modifyitems(config: pytest.Config, items: list[pytest.Item]) -> None:
    for item in items:
        explicit = {marker.name for marker in item.iter_markers()}
        if explicit & {"unit", "contract", "integration", "hardware"}:
            continue

        module_name = Path(str(item.fspath)).name.lower()
        if "hardware" in module_name or "cuda" in module_name:
            item.add_marker(pytest.mark.hardware)
        elif "integration" in module_name or "postgres" in module_name:
            item.add_marker(pytest.mark.integration)
        elif "contract" in module_name:
            item.add_marker(pytest.mark.contract)
        else:
            item.add_marker(pytest.mark.unit)
