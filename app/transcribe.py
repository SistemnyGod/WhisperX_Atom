import asyncio

from app.storage import read_job_json
from app.transcription_pipeline import pipeline


def run_transcription(job_id: str):
    read_job_json(job_id)

    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:
        asyncio.run(pipeline.submit(job_id))
    else:
        loop.create_task(pipeline.submit(job_id))
