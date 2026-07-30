FROM pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg git && rm -rf /var/lib/apt/lists/*
WORKDIR /srv
COPY requirements.txt /srv/requirements.txt
COPY requirements.constraints.txt /srv/requirements.constraints.txt
RUN pip install --no-cache-dir -r /srv/requirements.txt -c /srv/requirements.constraints.txt
COPY workers/ml_worker/requirements.txt /srv/ml-requirements.txt
RUN pip install --no-cache-dir -r /srv/ml-requirements.txt
COPY . /srv
ENV PYTHONPATH=/srv
CMD ["python", "-m", "workers.ml_worker.worker"]



