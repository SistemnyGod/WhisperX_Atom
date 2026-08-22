FROM python:3.12-slim
ARG WHISPERX_RELEASE_VERSION=dev
ARG WHISPERX_BUILD_IDENTITY=dev
ARG WHISPERX_REVISION=dev
WORKDIR /srv
ENV PYTHONUNBUFFERED=1 PYTHONPATH=/srv WORKER_NAME=memory-worker
COPY workers/memory_worker/requirements.txt /srv/memory-requirements.txt
RUN pip install --no-cache-dir -r /srv/memory-requirements.txt
COPY workers/memory_worker /srv/workers/memory_worker
COPY workers/__init__.py /srv/workers/__init__.py
COPY workers/nats_utils.py /srv/workers/nats_utils.py
COPY workers/runtime_heartbeat.py /srv/workers/runtime_heartbeat.py
COPY workers/runtime_healthcheck.py /srv/workers/runtime_healthcheck.py
LABEL org.opencontainers.image.title="WhisperX Atom Memory Worker" \
      org.opencontainers.image.version="$WHISPERX_RELEASE_VERSION" \
      org.opencontainers.image.revision="$WHISPERX_REVISION" \
      io.whisperx.atom.build-identity="$WHISPERX_BUILD_IDENTITY"
CMD ["python", "-m", "workers.memory_worker.worker"]
