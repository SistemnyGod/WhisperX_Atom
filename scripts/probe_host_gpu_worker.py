from __future__ import annotations

import os
import sys
import json

import psycopg


def main() -> int:
    stale_seconds = max(20, int(os.getenv("GPU_WORKER_HEARTBEAT_STALE_SECONDS", "75")))
    with psycopg.connect(os.environ["DATABASE_URL"]) as connection:
        row = connection.execute(
            """
            select status, last_seen_at, current_job_id, last_error_code, capabilities
              from worker_instances
             where worker_name = 'gpu-worker'
               and last_seen_at > now() - (%s * interval '1 second')
               and capabilities ->> 'runtime' = 'host'
             order by last_seen_at desc
             limit 1
            """
        , (stale_seconds,)).fetchone()
    if not row:
        return 1
    if len(sys.argv) > 1 and sys.argv[1] == "--json":
        print(json.dumps({
            "status": row[0],
            "lastSeenAt": row[1].isoformat() if row[1] else None,
            "currentJobId": str(row[2]) if row[2] else None,
            "lastErrorCode": row[3],
            "capabilities": row[4] or {},
        }, ensure_ascii=False))
    return 0 if row[0] in {"READY", "BUSY"} else 1


if __name__ == "__main__":
    sys.exit(main())
