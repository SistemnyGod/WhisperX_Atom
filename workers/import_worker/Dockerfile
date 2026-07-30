FROM python:3.12-slim
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /srv
COPY workers /srv/workers
COPY whisperx_atom /srv/whisperx_atom
COPY workers/import_worker/requirements.txt /srv/import-requirements.txt
RUN pip install --no-cache-dir -r /srv/import-requirements.txt
ENV PYTHONPATH=/srv
CMD ["python", "-m", "workers.import_worker.worker"]
