FROM ghcr.io/ggml-org/llama.cpp:server-cuda-b9445@sha256:39f4f2c5fd4537f85208f905e70269b5b691fbc6de00fd916c37a60a710b7d52 AS llama-runtime

FROM nvidia/cuda:12.8.1-cudnn-runtime-ubuntu24.04@sha256:ac55d124da4882b497f732d8dfd9a702d5447a5f29d08d56da6f64f0a1eb34bc
WORKDIR /srv
ENV DEBIAN_FRONTEND=noninteractive
# The pinned llama.cpp runtime is copied into the worker and launched only for
# the duration of one leased summary job. No resident LLM container is needed.
COPY --from=llama-runtime /app /opt/llama
ENV PATH="/opt/llama:${PATH}"
ENV LD_LIBRARY_PATH="/opt/llama:/usr/local/cuda/lib64"
RUN apt-get update && apt-get install -y --no-install-recommends python3.12 python3.12-venv python3-pip libgomp1 libstdc++6 libssl3 ca-certificates && rm -rf /var/lib/apt/lists/*
COPY workers/summary_worker /srv/workers/summary_worker
COPY workers/__init__.py /srv/workers/__init__.py
COPY workers/nats_utils.py /srv/workers/nats_utils.py
COPY workers/gpu_lease.py /srv/workers/gpu_lease.py
COPY workers/runtime_healthcheck.py /srv/workers/runtime_healthcheck.py
COPY workers/summary_worker/requirements.txt /srv/summary-requirements.txt
RUN python3.12 -m pip install --break-system-packages --no-cache-dir -r /srv/summary-requirements.txt
ENV PYTHONPATH=/srv
ENV WORKER_NAME=summary-worker
CMD ["python3.12", "-m", "workers.summary_worker.worker"]
