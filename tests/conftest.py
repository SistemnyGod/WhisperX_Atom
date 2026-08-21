"""Repository-wide pytest marker policy.

The project intentionally keeps most checks as fast source/contract tests.
Classifying them here makes CI selection explicit without requiring every
legacy test module to be edited.  A test can still opt into a more specific
marker with ``pytestmark``; explicit markers always win.
"""

from __future__ import annotations

from pathlib import Path

import pytest


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
