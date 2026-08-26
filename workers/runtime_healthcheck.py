"""Docker healthcheck for long-running WhisperX workers.

Importing a worker module is not a liveness check: it succeeds even when the
worker process has already exited or can no longer reach PostgreSQL.  Workers
publish a durable heartbeat, so the healthcheck verifies the latest heartbeat
for the configured worker and rejects stale/terminal states.
"""

from __future__ import annotations

import os
import sys
from datetime import datetime, timezone

import psycopg


MAX_HEARTBEAT_AGE_SECONDS = float(os.getenv("WORKER_HEALTH_MAX_AGE_SECONDS", "60"))
# STARTING is deliberately not healthy: the worker has not completed NATS
# stream/subscription setup yet and accepting traffic at this point loses
# messages or makes readiness lie about processing availability.
NON_READY_STATES = {"STARTING", "FAILED", "STOPPED", "UNAVAILABLE"}


def main() -> int:
    worker_name = os.getenv("WORKER_NAME", "").strip()
    conninfo = os.getenv("DATABASE_URL", "").strip()
    if not worker_name or not conninfo:
        return 1

    instance_id = os.getenv("WORKER_INSTANCE_ID", "").strip() or None
    try:
        with psycopg.connect(conninfo, connect_timeout=3) as connection:
            if instance_id:
                row = connection.execute(
                    """
                    SELECT status,last_seen_at
                    FROM worker_instances
                    WHERE worker_name=%s AND instance_id=%s
                    """,
                    (worker_name, instance_id),
                ).fetchone()
            else:
                row = connection.execute(
                    """
                    SELECT status,last_seen_at
                    FROM worker_instances
                    WHERE worker_name=%s
                    ORDER BY last_seen_at DESC
                    LIMIT 1
                    """,
                    (worker_name,),
                ).fetchone()
            if row is None:
                return 1
            status, last_seen = row
            if str(status).upper() in NON_READY_STATES or last_seen is None:
                return 1
            if last_seen.tzinfo is None:
                last_seen = last_seen.replace(tzinfo=timezone.utc)
            age = (datetime.now(timezone.utc) - last_seen).total_seconds()
            return 0 if 0 <= age <= MAX_HEARTBEAT_AGE_SECONDS else 1
    except Exception:
        # Healthchecks must fail closed. Docker will restart the worker and the
        # API readiness endpoint will expose the same unavailable state.
        return 1


if __name__ == "__main__":
    sys.exit(main())
