"""Preview/apply recovery for GPU jobs after a worker restart.

The command is intentionally part of the GPU worker image so it uses the same
database contract as the consumer.  It never deletes media, transcripts or
volumes; only durable ownership metadata is changed.  Preview is the default
and apply must be explicit.
"""

from __future__ import annotations

import argparse
import json
import os
from typing import Any

import psycopg


GPU_TYPES = ("TRANSCRIBE", "TRANSCRIBE_ASR", "TRANSCRIBE_REPROCESS", "TRANSCRIPT_ENRICH", "SUMMARIZE")


def conninfo() -> str:
    return os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")


def _rows(connection: psycopg.Connection[Any]) -> list[dict[str, Any]]:
    rows = connection.execute(
        """
        SELECT id,meeting_id,type,status,stage,progress,attempt,worker_id,
               lease_expires_at,last_heartbeat,pipeline_correlation_id
        FROM jobs
        WHERE type = ANY(%s::text[])
          AND (status='RUNNING' OR (status='QUEUED' AND (worker_id IS NOT NULL OR lease_expires_at IS NOT NULL)))
        ORDER BY created_at,id
        """,
        (list(GPU_TYPES),),
    ).fetchall()
    return [
        {
            "id": str(row[0]), "meetingId": str(row[1]) if row[1] else None,
            "type": str(row[2]), "status": str(row[3]), "stage": str(row[4]),
            "progress": int(row[5] or 0), "attempt": int(row[6] or 0),
            "workerId": row[7], "leaseExpiresAt": row[8].isoformat() if row[8] else None,
            "lastHeartbeat": row[9].isoformat() if row[9] else None,
            "pipelineCorrelationId": str(row[10]) if row[10] else None,
        }
        for row in rows
    ]


def _recovery_stage(job_type: str) -> str:
    if job_type == "SUMMARIZE":
        return "TRANSCRIPT_READY"
    if job_type == "TRANSCRIPT_ENRICH":
        return "ASR_READY"
    if job_type in {"TRANSCRIBE", "TRANSCRIBE_ASR", "TRANSCRIBE_REPROCESS"}:
        return "READY_FOR_ASR"
    return "UPLOADED"


def recover(apply: bool) -> dict[str, Any]:
    with psycopg.connect(conninfo()) as connection:
        with connection.transaction():
            candidates = _rows(connection)
            result: dict[str, Any] = {"mode": "apply" if apply else "preview", "count": len(candidates), "jobs": candidates}
            if not apply or not candidates:
                return result
            ids = [item["id"] for item in candidates]
            updated = 0
            for item in candidates:
                row = connection.execute(
                    """
                    UPDATE jobs
                    SET status='QUEUED',stage=%s,progress=0,
                        worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,
                        error_message=NULL,error_code='WORKER_RESTART_RECOVERY',
                        attempt=attempt+1,updated_at=now()
                    WHERE id=%s AND status NOT IN ('READY','FAILED','CANCELLED')
                    RETURNING id
                    """,
                    (_recovery_stage(item["type"]), item["id"]),
                ).fetchone()
                updated += 1 if row else 0
            connection.execute(
                """UPDATE inbox_messages
                   SET lease_expires_at=now()-interval '1 second',worker_id=NULL
                   WHERE job_id = ANY(%s::uuid[])""",
                (ids,),
            )
            result["updated"] = updated
            result["errorCode"] = "WORKER_RESTART_RECOVERY"
            return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Recover unfinished GPU jobs safely")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--preview", action="store_true", help="show candidates without changes (default)")
    mode.add_argument("--apply", action="store_true", help="requeue candidates in one transaction")
    args = parser.parse_args()
    print(json.dumps(recover(bool(args.apply)), ensure_ascii=False, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
