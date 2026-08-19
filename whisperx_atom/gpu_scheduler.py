"""Small process-local GPU concurrency boundary.

The PostgreSQL advisory lease remains the cross-process source of truth.  This
class only prevents a single worker process from loading more concurrent GPU
jobs than configured and keeps the policy explicit instead of scattering raw
``asyncio.Semaphore`` calls through worker orchestration.
"""

from __future__ import annotations

import asyncio
import os
from contextlib import asynccontextmanager
from typing import AsyncIterator


def _concurrency_from_env() -> int:
    try:
        requested = int(os.getenv("GPU_CONCURRENCY", "1"))
    except (TypeError, ValueError):
        requested = 1
    # The current ProcessingService keeps one resident WhisperX pipeline and
    # is deliberately not assumed thread-safe. Parallel slots therefore need
    # an explicit deployment opt-in after a VRAM/thread-safety gate; changing
    # GPU_CONCURRENCY alone can never accidentally turn it on.
    if requested > 1 and os.getenv("GPU_PIPELINE_PARALLEL", "false").lower() not in {"1", "true", "yes"}:
        return 1
    # More than four concurrent large-v3 jobs is not a supported deployment.
    return max(1, min(4, requested))


class GpuScheduler:
    """Bounded local scheduler for ASR/enrichment GPU work."""

    def __init__(self, concurrency: int | None = None) -> None:
        self.concurrency = _concurrency_from_env() if concurrency is None else max(1, min(4, int(concurrency)))
        self._slots = asyncio.Semaphore(self.concurrency)

    @property
    def available_slots(self) -> int:
        # ``_value`` is intentionally exposed as a diagnostic only; callers do
        # not mutate it and acquisition remains guarded by the semaphore.
        return max(0, int(self._slots._value))

    @asynccontextmanager
    async def slot(self) -> AsyncIterator[None]:
        await self._slots.acquire()
        try:
            yield
        finally:
            self._slots.release()
