from __future__ import annotations

import asyncio
import json
import os

import psycopg
from workers.nats_utils import ensure_stream
from workers.runtime_heartbeat import AsyncHeartbeat


async def recover_expired(connection) -> None:
    rows = connection.execute("""
        UPDATE jobs j SET status='QUEUED',stage='UPLOADED',progress=0,worker_id=NULL,lease_expires_at=NULL,last_heartbeat=NULL,updated_at=now()
        FROM media_assets a
        WHERE j.media_asset_id=a.id AND j.type='TRANSCRIBE' AND j.status='RUNNING' AND j.stage IN ('INGEST','UPLOADED','VALIDATING','NORMALIZING') AND j.lease_expires_at IS NOT NULL AND j.lease_expires_at < now()
        RETURNING j.id,j.meeting_id,j.media_asset_id,j.attempt,a.storage_key,a.source_type
    """).fetchall()
    for job_id, meeting_id, asset_id, attempt, storage_key, source_type in rows:
        payload = json.dumps({"message_id": __import__("uuid").uuid4().__str__(), "job_id": str(job_id), "meeting_id": str(meeting_id), "media_asset_id": str(asset_id), "stage": "UPLOADED", "attempt": attempt, "storage_key": storage_key, "source_type": source_type})
        connection.execute("INSERT INTO outbox_messages(id,topic,payload) VALUES(gen_random_uuid(),'media.ingest',%s::jsonb)", (payload,))


async def run() -> None:
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError("Install workers/outbox_relay/requirements.txt") from exc

    conninfo = os.getenv("DATABASE_URL", "host=postgres port=5432 dbname=whisperx_atom user=whisperx password=whisperx")
    client = await nats.connect(os.getenv("NATS_URL", "nats://nats:4222"))
    heartbeat = AsyncHeartbeat("outbox-relay", capabilities=lambda: {"natsConnected": True, "outboxRelay": "ready"})
    await heartbeat.start()
    jetstream = client.jetstream()
    subjects = ["media.ingest", "ml.transcribe", "llm.summarize", "llm.assistant"]
    # Existing development streams may have been created before a new subject
    # was introduced. Update the subject set instead of silently keeping stale
    # configuration (which would block the oldest outbox message forever).
    await ensure_stream(jetstream, name="WHISPERX", subjects=subjects)
    heartbeat.set_state("READY")

    while True:
        published = False
        with psycopg.connect(conninfo) as connection:
            await recover_expired(connection)
            row = connection.execute(
                "SELECT id, topic, payload FROM outbox_messages WHERE published_at IS NULL ORDER BY created_at, id LIMIT 1"
            ).fetchone()

        if row:
            message_id, topic, payload = row
            # Do not hold a PostgreSQL transaction/row lock while waiting on
            # NATS. A relay restart or a slow broker must not stall writers
            # inserting new outbox rows. NATS de-duplicates the stable message
            # id, so two relay instances racing on the same row are harmless.
            await jetstream.publish(
                topic,
                json.dumps(payload).encode("utf-8"),
                headers={"Nats-Msg-Id": str(message_id)},
            )
            with psycopg.connect(conninfo) as connection:
                connection.execute(
                    "UPDATE outbox_messages SET published_at=now() WHERE id=%s AND published_at IS NULL",
                    (message_id,),
                )
            published = True
        if not published:
            await asyncio.sleep(0.5)


if __name__ == "__main__":
    asyncio.run(run())
