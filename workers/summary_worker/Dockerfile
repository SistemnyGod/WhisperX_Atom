FROM python:3.12-slim
WORKDIR /srv
COPY workers/summary_worker /srv/workers/summary_worker
COPY workers/__init__.py /srv/workers/__init__.py
COPY workers/nats_utils.py /srv/workers/nats_utils.py
COPY workers/summary_worker/requirements.txt /srv/summary-requirements.txt
RUN pip install --no-cache-dir -r /srv/summary-requirements.txt
ENV PYTHONPATH=/srv
CMD ["python", "-m", "workers.summary_worker.worker"]
