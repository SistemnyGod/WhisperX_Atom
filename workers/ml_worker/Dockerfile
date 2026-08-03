FROM pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg git && rm -rf /var/lib/apt/lists/*
WORKDIR /srv
COPY workers/ml_worker/requirements.gpu.txt /srv/ml-requirements.txt
# The base image already contains the CUDA-matched Torch runtime. Install the
# ML packages without the legacy desktop/Web dependency bundle.
RUN pip install --no-cache-dir --no-deps whisperx==3.7.5 pyannote.audio==3.3.2
RUN pip install --no-cache-dir -r /srv/ml-requirements.txt
COPY . /srv
COPY workers/gpu_lease.py /srv/workers/gpu_lease.py
ENV PYTHONPATH=/srv
CMD ["python", "-m", "workers.ml_worker.worker"]