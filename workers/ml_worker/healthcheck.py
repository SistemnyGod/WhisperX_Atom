from __future__ import annotations

import os
import sys
from datetime import datetime, timezone

import psycopg


def main() -> int:
    conninfo = os.getenv("DATABASE_URL", "")
    if not conninfo:
        return 1
    try:
        with psycopg.connect(conninfo, connect_timeout=3) as connection:
            row = connection.execute(
                """
                SELECT status,last_seen_at,capabilities
                FROM worker_instances
                WHERE worker_name='gpu-worker' AND instance_id=%s
                """,
                (os.getenv("WORKER_INSTANCE_ID", "gpu-worker"),),
            ).fetchone()
            if row is None or str(row[0]).upper() in {"STARTING", "FAILED", "STOPPED", "UNAVAILABLE"}:
                return 1
            capabilities = row[2] or {}
            if capabilities.get("cudaAvailable") is not True:
                return 1
            last_seen = row[1].replace(tzinfo=timezone.utc) if row[1].tzinfo is None else row[1]
            age = (datetime.now(timezone.utc) - last_seen).total_seconds()
            return 0 if 0 <= age <= 60 else 1
    except Exception:
        return 1


if __name__ == "__main__":
    sys.exit(main())
