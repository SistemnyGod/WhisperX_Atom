FROM python:3.12-slim
WORKDIR /srv
COPY workers/outbox_relay /srv/workers/outbox_relay
COPY workers/__init__.py /srv/workers/__init__.py
COPY workers/runtime_heartbeat.py /srv/workers/runtime_heartbeat.py
COPY workers/outbox_relay/requirements.txt /srv/relay-requirements.txt
RUN pip install --no-cache-dir -r /srv/relay-requirements.txt
CMD ["python", "-m", "workers.outbox_relay.worker"]

