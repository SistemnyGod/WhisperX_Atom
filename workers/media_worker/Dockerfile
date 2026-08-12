FROM python:3.12.10-slim-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /srv
COPY workers/media_worker /srv/workers/media_worker
COPY workers/__init__.py /srv/workers/__init__.py
COPY workers/nats_utils.py /srv/workers/nats_utils.py
COPY workers/runtime_heartbeat.py /srv/workers/runtime_heartbeat.py
COPY whisperx_atom /srv/whisperx_atom
COPY workers/media_worker/requirements.txt /srv/media-requirements.txt
RUN pip install --no-cache-dir -r /srv/media-requirements.txt
CMD ["python", "-m", "workers.media_worker.worker"]

