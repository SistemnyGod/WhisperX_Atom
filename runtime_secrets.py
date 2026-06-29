from __future__ import annotations

import os


def load_hf_token_from_env() -> str:
    return (os.getenv("HF_TOKEN") or "").strip()
