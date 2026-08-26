"""WhisperX Atom processing package compatibility boundary.

The legacy desktop application remains in the repository root as app.py. The
package exposes its GUI symbols lazily so server/worker imports do not pull
CustomTkinter into headless containers.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
from types import ModuleType


def _legacy_module() -> ModuleType:
    path = Path(__file__).resolve().parent.parent / "app.py"
    spec = importlib.util.spec_from_file_location("whisperx_atom_legacy_gui", path)
    if spec is None or spec.loader is None:
        raise ImportError("legacy app.py could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def __getattr__(name: str):
    if name in {"WhisperXApp", "WhisperXService"}:
        return getattr(_legacy_module(), name)
    raise AttributeError(name)


__all__ = ["WhisperXApp", "WhisperXService"]

