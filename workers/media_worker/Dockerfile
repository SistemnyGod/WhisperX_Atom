FROM python:3.12.10-slim-bookworm
ARG WHISPERX_RELEASE_VERSION=dev
ARG WHISPERX_BUILD_IDENTITY=dev
ARG WHISPERX_REVISION=dev
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /srv
COPY workers/media_worker /srv/workers/media_worker
COPY workers/__init__.py /srv/workers/__init__.py
COPY workers/nats_utils.py /srv/workers/nats_utils.py
COPY workers/runtime_heartbeat.py /srv/workers/runtime_heartbeat.py
COPY workers/runtime_healthcheck.py /srv/workers/runtime_healthcheck.py
COPY workers/pipeline_timeline.py /srv/workers/pipeline_timeline.py
COPY whisperx_atom /srv/whisperx_atom
COPY workers/media_worker/requirements.txt /srv/media-requirements.txt
RUN pip install --no-cache-dir -r /srv/media-requirements.txt
LABEL org.opencontainers.image.title="WhisperX Atom Media Worker" \
      org.opencontainers.image.version="$WHISPERX_RELEASE_VERSION" \
      org.opencontainers.image.revision="$WHISPERX_REVISION" \
      io.whisperx.atom.build-identity="$WHISPERX_BUILD_IDENTITY" \
      org.opencontainers.image.source="WhisperX Atom"
ENV WORKER_NAME=media-worker
CMD ["python", "-m", "workers.media_worker.worker"]

