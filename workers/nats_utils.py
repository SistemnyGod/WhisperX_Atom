from __future__ import annotations

from collections.abc import Sequence
from typing import Any


async def fetch_available(
    subscription: Any,
    timeout_error: type[BaseException],
    *,
    timeout: float = 30,
) -> Sequence[Any]:
    """Return an empty batch when a JetStream pull simply times out."""
    try:
        return await subscription.fetch(1, timeout=timeout)
    except timeout_error:
        return ()
