FROM python:3.12.10-slim-bookworm
ARG WHISPERX_RELEASE_VERSION=dev
ARG WHISPERX_BUILD_IDENTITY=dev
ARG WHISPERX_REVISION=dev
WORKDIR /srv
COPY workers/outbox_relay /srv/workers/outbox_relay
COPY workers/__init__.py /srv/workers/__init__.py
COPY workers/nats_utils.py /srv/workers/nats_utils.py
COPY workers/runtime_heartbeat.py /srv/workers/runtime_heartbeat.py
COPY workers/runtime_healthcheck.py /srv/workers/runtime_healthcheck.py
COPY workers/outbox_relay/requirements.txt /srv/relay-requirements.txt
RUN pip install --no-cache-dir -r /srv/relay-requirements.txt
LABEL org.opencontainers.image.title="WhisperX Atom Outbox Relay" \
      org.opencontainers.image.version="$WHISPERX_RELEASE_VERSION" \
      org.opencontainers.image.revision="$WHISPERX_REVISION" \
      io.whisperx.atom.build-identity="$WHISPERX_BUILD_IDENTITY" \
      org.opencontainers.image.source="WhisperX Atom"
ENV WORKER_NAME=outbox-relay
CMD ["python", "-m", "workers.outbox_relay.worker"]
