"""Preview/apply recovery for legacy duplicate recorder tracks.

The command is deliberately narrow: callers must provide explicit session IDs,
preview is the default, and apply deletes only an empty track that is paired
with a confirmed track of the same type.  Chunks and media files are never
deleted.  The existing media job is requeued in the same transaction and an
unpublished media.ingest event is created only when one is missing.
"""

from __future__ import annotations

import argparse
import json
import os
import uuid
from typing import Any

import psycopg


RECOVERABLE_ERRORS = {"MEDIA_PROCESSING_FAILED", "AUDIO_PROCESSING_ERROR", "AUDIO_TRACK_DRIFT_HIGH"}


def conninfo() -> str:
    return os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")


def _track_rows(connection: psycopg.Connection[Any], session_id: str) -> list[dict[str, Any]]:
    rows = connection.execute(
        """
        SELECT t.id,t.track_type,t.local_track_id,
               COUNT(c.id) FILTER (WHERE c.status='CONFIRMED') AS confirmed_count,
               COALESCE(SUM(c.sample_count) FILTER (WHERE c.status='CONFIRMED'),0) AS confirmed_samples,
               COALESCE(MAX(c.start_sample+c.sample_count) FILTER (WHERE c.status='CONFIRMED'),0) AS end_sample
        FROM recording_tracks t
        LEFT JOIN recording_chunks c ON c.track_id=t.id
        WHERE t.session_id=%s
        GROUP BY t.id,t.track_type,t.local_track_id
        ORDER BY t.created_at,t.id
        """,
        (session_id,),
    ).fetchall()
    return [
        {
            "trackId": str(row[0]),
            "trackType": str(row[1]),
            "localTrackId": row[2],
            "confirmedChunks": int(row[3] or 0),
            "confirmedSamples": int(row[4] or 0),
            "endSample": int(row[5] or 0),
        }
        for row in rows
    ]


def _candidate(connection: psycopg.Connection[Any], session_id: str) -> dict[str, Any]:
    session = connection.execute(
        "SELECT id,meeting_id,state,pipeline_correlation_id FROM recording_sessions WHERE id=%s FOR UPDATE",
        (session_id,),
    ).fetchone()
    if session is None:
        return {"sessionId": session_id, "action": "NOT_FOUND"}
    tracks = _track_rows(connection, session_id)
    empty: list[dict[str, Any]] = []
    for track in tracks:
        if track["confirmedChunks"] != 0:
            continue
        has_sibling = any(
            sibling["trackType"] == track["trackType"] and sibling["confirmedChunks"] > 0
            for sibling in tracks
        )
        if has_sibling:
            empty.append(track)
    if len(empty) != 1:
        return {
            "sessionId": session_id,
            "meetingId": str(session[1]),
            "state": str(session[2]),
            "tracks": tracks,
            "action": "BLOCKED_EXPECTED_ONE_EMPTY_SIBLING",
        }
    asset = connection.execute(
        """
        SELECT a.id,a.status,j.id,j.status,j.error_code
        FROM media_assets a
        LEFT JOIN jobs j ON j.media_asset_id=a.id AND j.type IN ('TRANSCRIBE_ASR','TRANSCRIBE')
        WHERE a.storage_key=%s AND a.source_type='recorder_session'
        ORDER BY j.created_at DESC NULLS LAST
        LIMIT 1
        """,
        (f"/data/recordings/{session_id.replace('-', '')}",),
    ).fetchone()
    asset_info = None
    if asset:
        asset_info = {
            "assetId": str(asset[0]),
            "assetStatus": str(asset[1]),
            "jobId": str(asset[2]) if asset[2] else None,
            "jobStatus": str(asset[3]) if asset[3] else None,
            "jobErrorCode": str(asset[4]) if asset[4] else None,
        }
    action = "READY_TO_APPLY" if asset and asset[2] and (asset[3] == "FAILED" and asset[4] in RECOVERABLE_ERRORS or asset[3] == "QUEUED") else "BLOCKED_MEDIA_JOB_MISSING_OR_TERMINAL"
    return {
        "sessionId": session_id,
        "meetingId": str(session[1]),
        "state": str(session[2]),
        "tracks": tracks,
        "emptyTrackId": empty[0]["trackId"],
        "asset": asset_info,
        "action": action,
    }


def recover(session_ids: list[str], apply: bool) -> dict[str, Any]:
    parsed = [str(uuid.UUID(value)) for value in session_ids]
    with psycopg.connect(conninfo()) as connection:
        with connection.transaction():
            candidates = [_candidate(connection, session_id) for session_id in parsed]
            result: dict[str, Any] = {"mode": "apply" if apply else "preview", "count": len(candidates), "sessions": candidates}
            ready = [item for item in candidates if item.get("action") == "READY_TO_APPLY"]
            if not apply or len(ready) != len(candidates):
                return result
            for item in ready:
                session_id = item["sessionId"]
                empty_track_id = item["emptyTrackId"]
                connection.execute(
                    "DELETE FROM recording_tracks WHERE id=%s AND session_id=%s AND NOT EXISTS (SELECT 1 FROM recording_chunks WHERE track_id=%s)",
                    (empty_track_id, session_id, empty_track_id),
                )
                connection.execute(
                    """
                    UPDATE recording_sessions
                    SET state='FINALIZING',
                        total_samples=(SELECT COALESCE(MAX(start_sample+sample_count),0) FROM recording_chunks WHERE session_id=%s AND status='CONFIRMED'),
                        finished_at=COALESCE(finished_at,now())
                    WHERE id=%s
                    """,
                    (session_id, session_id),
                )
                asset_id = item["asset"]["assetId"]
                job_id = item["asset"]["jobId"]
                connection.execute(
                    "UPDATE media_assets SET status='INGESTING',failure_code=NULL,failure_detail=NULL WHERE id=%s AND status='FAILED'",
                    (asset_id,),
                )
                connection.execute(
                    """
                    UPDATE jobs
                    SET status='QUEUED',stage='INGEST',progress=0,error_message=NULL,error_code=NULL,
                        worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,not_before=NULL,
                        scheduled_reason=NULL,queue_entered_at=now(),worker_claimed_at=NULL,
                        attempt=attempt+1,updated_at=now()
                    WHERE id=%s AND (status='FAILED' OR status='QUEUED')
                    """,
                    (job_id,),
                )
                connection.execute(
                    "UPDATE meetings SET status='INGESTING' WHERE id=%s",
                    (item["meetingId"],),
                )
                connection.execute(
                    """
                    INSERT INTO outbox_messages(id,topic,payload)
                    SELECT %s,'media.ingest',jsonb_build_object(
                        'message_id',%s::text,'job_id',j.id,'meeting_id',j.meeting_id,
                        'media_asset_id',j.media_asset_id,'stage','INGEST','attempt',j.attempt,
                        'storage_key',a.storage_key,'source_type',a.source_type,
                        'session_id',%s::text,'language','ru','correlation_id',rs.pipeline_correlation_id)
                    FROM jobs j
                    JOIN media_assets a ON a.id=j.media_asset_id
                    JOIN recording_sessions rs ON rs.id=%s
                    WHERE j.id=%s
                      AND NOT EXISTS (
                          SELECT 1 FROM outbox_messages o
                          WHERE o.topic='media.ingest' AND o.published_at IS NULL
                            AND o.payload->>'job_id'=j.id::text)
                    """,
                    (str(uuid.uuid4()), str(uuid.uuid4()), session_id, session_id, job_id),
                )
            result["applied"] = len(ready)
            return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Recover explicitly selected legacy duplicate recorder tracks")
    parser.add_argument("--session-id", action="append", required=True, help="recording session UUID; repeat for multiple sessions")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--preview", action="store_true", help="show candidates without changes (default)")
    mode.add_argument("--apply", action="store_true", help="apply the narrow transactional repair")
    args = parser.parse_args()
    try:
        result = recover(args.session_id, bool(args.apply))
    except (ValueError, psycopg.Error) as exc:
        print(json.dumps({"mode": "apply" if args.apply else "preview", "error": type(exc).__name__}, ensure_ascii=False))
        return 2
    print(json.dumps(result, ensure_ascii=False, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
