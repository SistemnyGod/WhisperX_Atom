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
from .entity_resolver import canonical_topic_name, SUBJECT_NORMALIZER_VERSION


def _conninfo() -> str:
    return os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", action="store_true")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--limit", type=int, default=100)
    parser.add_argument("--stage", choices=("subjects", "jobs"), default="jobs")
    parser.add_argument("--rebuild-existing", action="store_true")
    args = parser.parse_args()
    if args.preview == args.apply:
        parser.error("choose exactly one of --preview or --apply")
    limit = max(1, min(int(args.limit), 1000))
    with psycopg.connect(_conninfo()) as connection:
        if args.stage == "subjects":
            rows = connection.execute(
                """SELECT id,subject
                     FROM transcript_facts
                    WHERE state='ACTIVE' AND subject IS NOT NULL
                      AND subject_normalized IS NULL
                    ORDER BY created_at,id
                    LIMIT %s""",
                (limit,),
            ).fetchall()
            identifiers = [{"factId": str(row[0])} for row in rows]
            if args.apply:
                with connection.transaction():
                    for fact_id, subject in rows:
                        connection.execute(
                            "UPDATE transcript_facts SET subject_normalized=%s WHERE id=%s AND state='ACTIVE'",
                            (canonical_topic_name(str(subject)), fact_id),
                        )
            print(json.dumps({
                "mode": "apply" if args.apply else "preview",
                "stage": "subjects",
                "normalizerVersion": SUBJECT_NORMALIZER_VERSION,
                "count": len(identifiers),
                "items": identifiers,
            }, ensure_ascii=False))
            return 0

        rebuild_status = "AND j.id IS NULL" if not args.rebuild_existing else "AND (j.id IS NULL OR j.status IN ('READY','NEEDS_REVIEW'))"
        rows = connection.execute(
            f"""SELECT m.owner_id,m.id,t.id,t.version,j.id,j.status
                  FROM transcripts t
                  JOIN meetings m ON m.id=t.meeting_id
                  LEFT JOIN memory_jobs j ON j.transcript_id=t.id AND j.transcript_version=t.version
                 WHERE t.status IN ('READY','PARTIAL_READY')
                   AND t.version_kind IN ('ENRICHED','V2')
                   AND t.version=(SELECT max(t2.version) FROM transcripts t2 WHERE t2.meeting_id=t.meeting_id)
                   {rebuild_status}
                 ORDER BY COALESCE((SELECT MIN(rs.started_at) FROM recording_sessions rs WHERE rs.meeting_id=m.id),m.created_at) NULLS LAST,m.id,t.id
                 LIMIT %s""",
            (limit,),
        ).fetchall()
        identifiers = [
            {"meetingId": str(row[1]), "transcriptId": str(row[2]), "transcriptVersion": int(row[3]), "existingJobId": str(row[4]) if row[4] else None}
            for row in rows
        ]
        if args.apply:
            with connection.transaction():
                for owner_id, meeting_id, transcript_id, version, existing_job_id, existing_status in rows:
                    job_id = connection.execute(
                        """INSERT INTO memory_jobs(owner_user_id,meeting_id,transcript_id,transcript_version,status,stage,progress,attempt)
                           VALUES(%s,%s,%s,%s,'QUEUED','QUEUED',0,0)
                           ON CONFLICT(transcript_id,transcript_version) DO UPDATE SET
                             status=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN 'QUEUED' ELSE memory_jobs.status END,
                             stage=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN 'QUEUED' ELSE memory_jobs.stage END,
                             progress=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN 0 ELSE memory_jobs.progress END,
                             attempt=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN 0 ELSE memory_jobs.attempt END,
                             worker_id=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN NULL ELSE memory_jobs.worker_id END,
                             lease_expires_at=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN NULL ELSE memory_jobs.lease_expires_at END,
                             last_heartbeat=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN NULL ELSE memory_jobs.last_heartbeat END,
                             error_code=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN NULL ELSE memory_jobs.error_code END,
                             error_message=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN NULL ELSE memory_jobs.error_message END,
                             completed_at=CASE WHEN %s AND memory_jobs.status IN ('READY','NEEDS_REVIEW') THEN NULL ELSE memory_jobs.completed_at END,
                             updated_at=now()
                           RETURNING id""",
                        (owner_id, meeting_id, transcript_id, int(version), args.rebuild_existing, args.rebuild_existing, args.rebuild_existing, args.rebuild_existing, args.rebuild_existing, args.rebuild_existing, args.rebuild_existing, args.rebuild_existing, args.rebuild_existing, args.rebuild_existing),
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
        print(json.dumps({"mode": "apply" if args.apply else "preview", "stage": "jobs", "rebuildExisting": args.rebuild_existing, "count": len(identifiers), "items": identifiers}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
