FROM python:3.12.10-slim-bookworm
WORKDIR /srv
COPY workers/outbox_relay /srv/workers/outbox_relay
COPY workers/__init__.py /srv/workers/__init__.py
COPY workers/nats_utils.py /srv/workers/nats_utils.py
COPY workers/runtime_heartbeat.py /srv/workers/runtime_heartbeat.py
COPY workers/runtime_healthcheck.py /srv/workers/runtime_healthcheck.py
COPY workers/outbox_relay/requirements.txt /srv/relay-requirements.txt
RUN pip install --no-cache-dir -r /srv/relay-requirements.txt
ENV WORKER_NAME=outbox-relay
CMD ["python", "-m", "workers.outbox_relay.worker"]
