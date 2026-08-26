"""Small, lazy PostgreSQL connection pool shared by long-lived workers.

The pool is opened on the first query, not during process startup.  This keeps
health endpoints usable while PostgreSQL is still booting and preserves the
existing transaction boundaries of each repository method.
"""

from __future__ import annotations

import os
import threading
from contextlib import contextmanager
from typing import Any, Iterator

import psycopg

try:
    from psycopg_pool import ConnectionPool
except ImportError:  # pragma: no cover - compatibility for minimal tooling
    ConnectionPool = None  # type: ignore[assignment,misc]


class DatabaseConnectionPool:
    def __init__(self, conninfo: str, name: str) -> None:
        self._conninfo = conninfo
        self._pool = None
        self._open_lock = threading.Lock()
        self._opened = False
        if ConnectionPool is not None:
            try:
                configured_maximum = int(os.getenv("DATABASE_POOL_MAX_SIZE", "4"))
            except ValueError:
                configured_maximum = 4
            try:
                timeout = float(os.getenv("DATABASE_POOL_TIMEOUT_SECONDS", "15"))
            except ValueError:
                timeout = 15.0
            self._pool = ConnectionPool(
                conninfo,
                min_size=1,
                max_size=max(2, min(8, configured_maximum)),
                open=False,
                name=name,
                timeout=max(1.0, timeout),
            )

    @contextmanager
    def connection(self) -> Iterator[Any]:
        if self._pool is None:
            with psycopg.connect(self._conninfo) as connection:
                yield connection
            return
        with self._open_lock:
            if not self._opened:
                self._pool.open(wait=True)
                self._opened = True
        with self._pool.connection() as connection:
            yield connection

    def close(self) -> None:
        if self._pool is not None:
            with self._open_lock:
                if self._opened:
                    self._pool.close()
                    self._opened = False
