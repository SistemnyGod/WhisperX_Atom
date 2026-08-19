FROM python:3.12.10-slim-bookworm
ARG WHISPERX_RELEASE_VERSION=dev
ARG WHISPERX_BUILD_IDENTITY=dev
ARG WHISPERX_REVISION=dev
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /srv
COPY workers /srv/workers
COPY whisperx_atom /srv/whisperx_atom
COPY workers/runtime_healthcheck.py /srv/workers/runtime_healthcheck.py
COPY workers/import_worker/requirements.txt /srv/import-requirements.txt
RUN pip install --no-cache-dir -r /srv/import-requirements.txt
LABEL org.opencontainers.image.title="WhisperX Atom Import Worker" \
      org.opencontainers.image.version="$WHISPERX_RELEASE_VERSION" \
      org.opencontainers.image.revision="$WHISPERX_REVISION" \
      io.whisperx.atom.build-identity="$WHISPERX_BUILD_IDENTITY" \
      org.opencontainers.image.source="WhisperX Atom"
ENV PYTHONPATH=/srv
ENV WORKER_NAME=import-worker
CMD ["python", "-m", "workers.import_worker.worker"]
