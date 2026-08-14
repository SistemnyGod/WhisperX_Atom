from __future__ import annotations

from collections.abc import Sequence
from contextlib import asynccontextmanager
import asyncio
import logging
from typing import Any


LOGGER = logging.getLogger(__name__)


async def ensure_stream(
    jetstream: Any,
    *,
    name: str,
    subjects: Sequence[str],
) -> None:
    """Ensure a JetStream stream exists with the complete subject set.

    A failed stream update used to be swallowed by each worker.  That made a
    worker report READY while it could not consume or publish any work.  Try
    the create path for a first boot, but fail closed when both operations
    fail so Docker/API readiness can restart and expose the real outage.
    """
    normalized_subjects = [str(subject).strip() for subject in subjects if str(subject).strip()]
    if not normalized_subjects:
        raise ValueError("jetstream_subjects_required")
    try:
        await jetstream.update_stream(name=name, subjects=normalized_subjects)
        return
    except Exception as update_error:
        try:
            await jetstream.add_stream(name=name, subjects=normalized_subjects)
            return
        except Exception as add_error:
            LOGGER.error(
                "jetstream stream unavailable name=%s update=%s add=%s",
                name,
                type(update_error).__name__,
                type(add_error).__name__,
            )
            raise RuntimeError(f"nats_stream_unavailable:{name}") from add_error


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


@asynccontextmanager
async def maintain_message(message: Any, *, on_tick: Any = None, interval: float = 20.0):
    """Keep a long-running JetStream delivery and its DB lease alive."""
    stopped = asyncio.Event()

    async def renew() -> None:
        while not stopped.is_set():
            try:
                await asyncio.wait_for(stopped.wait(), timeout=max(5.0, interval))
                break
            except asyncio.TimeoutError:
                try:
                    await message.in_progress()
                    if on_tick is not None:
                        result = on_tick()
                        if asyncio.iscoroutine(result):
                            await result
                except Exception as exc:
                    LOGGER.warning("jetstream heartbeat failed: %s", exc)

    task = asyncio.create_task(renew())
    try:
        yield
    finally:
        stopped.set()
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
