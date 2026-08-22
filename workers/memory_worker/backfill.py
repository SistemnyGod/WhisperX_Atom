"""Bounded, idempotent Meeting Memory backfill.

The command emits only identifiers and counts. It never reads or writes audio,
transcript text, assistant answers or secrets into an artifact.
"""

from __future__ import annotations

import argparse
import json
import os
import uuid

import psycopg


def _conninfo() -> str:
    return os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", action="store_true")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--limit", type=int, default=100)
    args = parser.parse_args()
    if args.preview == args.apply:
        parser.error("choose exactly one of --preview or --apply")
    limit = max(1, min(int(args.limit), 1000))
    with psycopg.connect(_conninfo()) as connection:
        rows = connection.execute(
            """SELECT m.owner_id,m.id,t.id,t.version
                 FROM transcripts t
                 JOIN meetings m ON m.id=t.meeting_id
                WHERE t.status IN ('READY','PARTIAL_READY')
                  AND t.version_kind IN ('ENRICHED','V2')
                  AND t.version=(SELECT max(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
                  AND NOT EXISTS (SELECT 1 FROM memory_jobs j WHERE j.transcript_id=t.id AND j.transcript_version=t.version)
                ORDER BY m.started_at NULLS LAST,m.id,t.id
                LIMIT %s""",
            (limit,),
        ).fetchall()
        identifiers = [
            {"meetingId": str(row[1]), "transcriptId": str(row[2]), "transcriptVersion": int(row[3])}
            for row in rows
        ]
        if args.apply:
            with connection.transaction():
                for owner_id, meeting_id, transcript_id, version in rows:
                    job_id = connection.execute(
                        """INSERT INTO memory_jobs(owner_user_id,meeting_id,transcript_id,transcript_version,status,stage,progress,attempt)
                           VALUES(%s,%s,%s,%s,'QUEUED','QUEUED',0,0)
                           ON CONFLICT(transcript_id,transcript_version) DO UPDATE SET updated_at=now()
                           RETURNING id""",
                        (owner_id, meeting_id, transcript_id, int(version)),
                    ).fetchone()[0]
                    exists = connection.execute(
                        "SELECT EXISTS(SELECT 1 FROM outbox_messages WHERE topic='memory.index' AND payload->>'jobId'=%s)",
                        (str(job_id),),
                    ).fetchone()[0]
                    if not exists:
                        payload = json.dumps({
                            "jobId": str(job_id),
                            "meetingId": str(meeting_id),
                            "transcriptId": str(transcript_id),
                            "transcriptVersion": int(version),
                            "ownerUserId": str(owner_id),
                            "pipelineCorrelationId": None,
                        })
                        connection.execute(
                            "INSERT INTO outbox_messages(id,topic,payload) VALUES(%s,'memory.index',%s::jsonb)",
                            (uuid.uuid4(), payload),
                        )
    print(json.dumps({"mode": "apply" if args.apply else "preview", "count": len(identifiers), "items": identifiers}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
