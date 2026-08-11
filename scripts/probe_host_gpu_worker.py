from __future__ import annotations

import os
import sys

import psycopg


def main() -> int:
    with psycopg.connect(os.environ["DATABASE_URL"]) as connection:
        row = connection.execute(
            """
            select count(*)
              from worker_instances
             where worker_name = 'gpu-worker'
               and status in ('READY', 'BUSY')
               and last_seen_at > now() - interval '60 seconds'
               and capabilities ->> 'runtime' = 'host'
            """
        ).fetchone()
    return 0 if row and row[0] else 1


if __name__ == "__main__":
    sys.exit(main())
