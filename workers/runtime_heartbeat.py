from __future__ import annotations

import asyncio
import json
import os
import socket
import threading
import time
from collections.abc import Awaitable, Callable
from typing import Any

import psycopg


def _conninfo() -> str:
    return os.getenv(
        "DATABASE_URL",
        "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx",
    )


def _instance_id(worker_name: str) -> str:
    return os.getenv("WORKER_INSTANCE_ID") or f"{worker_name}-{socket.gethostname()}-{os.getpid()}"


def write_heartbeat(
    worker_name: str,
    *,
    status: str = "READY",
    current_job_id: str | None = None,
    capabilities: dict[str, Any] | None = None,
    last_error_code: str | None = None,
) -> None:
    """Publish a best-effort worker heartbeat without affecting the worker loop."""
    version = os.getenv("APP_VERSION", os.getenv("WHISPERX_VERSION", "dev"))
    payload = json.dumps(capabilities or {}, ensure_ascii=False)
    try:
        with psycopg.connect(_conninfo(), connect_timeout=3) as connection:
            connection.execute(
                """
                INSERT INTO worker_instances(worker_name,instance_id,status,last_seen_at,current_job_id,version,capabilities,last_error_code)
                VALUES(%s,%s,%s,now(),%s,%s,%s::jsonb,%s)
                ON CONFLICT(worker_name,instance_id) DO UPDATE SET
                  status=excluded.status,
                  last_seen_at=excluded.last_seen_at,
                  current_job_id=excluded.current_job_id,
                  version=excluded.version,
                  capabilities=excluded.capabilities,
                  last_error_code=excluded.last_error_code
                """,
                (worker_name, _instance_id(worker_name), status, current_job_id, version, payload, last_error_code),
            )
    except Exception:
        # Readiness must observe failures, but a transient DB failure must not
        # terminate a worker that can still recover its connection.
        return


class AsyncHeartbeat:
    def __init__(
        self,
        worker_name: str,
        *,
        capabilities: Callable[[], dict[str, Any]] | None = None,
        interval: float = 20.0,
    ) -> None:
        self.worker_name = worker_name
        self.capabilities = capabilities
        self.interval = max(5.0, interval)
        self._task: asyncio.Task[None] | None = None
        self._job_id: str | None = None
        self._status = "STARTING"
        self._error: str | None = None

    def set_job(self, job_id: str | None) -> None:
        self._job_id = job_id

    def set_state(self, status: str, error_code: str | None = None) -> None:
        self._status = status
        self._error = error_code

    async def start(self) -> None:
        if self._task is None:
            self._task = asyncio.create_task(self._run())

    async def stop(self, status: str = "STOPPED") -> None:
        self._status = status
        if self._task is not None:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
            self._task = None
        await asyncio.to_thread(self._write)

    async def _run(self) -> None:
        while True:
            await asyncio.to_thread(self._write)
            await asyncio.sleep(self.interval)

    def _write(self) -> None:
        write_heartbeat(
            self.worker_name,
            status=self._status,
            current_job_id=self._job_id,
            capabilities=self.capabilities() if self.capabilities else {},
            last_error_code=self._error,
        )


def start_sync_heartbeat(
    worker_name: str,
    *,
    capabilities: Callable[[], dict[str, Any]] | None = None,
    interval: float = 20.0,
) -> tuple[threading.Event, threading.Thread]:
    stop_event = threading.Event()

    def run() -> None:
        while not stop_event.is_set():
            write_heartbeat(worker_name, capabilities=capabilities() if capabilities else {})
            stop_event.wait(max(5.0, interval))

    thread = threading.Thread(target=run, name=f"{worker_name}-heartbeat", daemon=True)
    thread.start()
    return stop_event, thread
