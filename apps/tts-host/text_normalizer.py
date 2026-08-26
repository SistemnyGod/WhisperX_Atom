from __future__ import annotations

import unicodedata


def normalize_text(value: str) -> str:
    """Normalize user text without logging or changing its meaning."""
    return " ".join(unicodedata.normalize("NFC", value).strip().split())
