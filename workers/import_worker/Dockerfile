FROM python:3.12.10-slim-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /srv
COPY workers /srv/workers
COPY whisperx_atom /srv/whisperx_atom
COPY workers/runtime_healthcheck.py /srv/workers/runtime_healthcheck.py
COPY workers/import_worker/requirements.txt /srv/import-requirements.txt
RUN pip install --no-cache-dir -r /srv/import-requirements.txt
ENV PYTHONPATH=/srv
ENV WORKER_NAME=import-worker
CMD ["python", "-m", "workers.import_worker.worker"]
