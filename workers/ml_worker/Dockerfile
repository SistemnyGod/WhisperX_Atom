ARG GPU_BASE_IMAGE=pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime
FROM ${GPU_BASE_IMAGE}
ARG WHISPERX_RELEASE_VERSION=dev
ARG WHISPERX_BUILD_IDENTITY=dev
ARG WHISPERX_REVISION=dev
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg git && rm -rf /var/lib/apt/lists/*
WORKDIR /srv
COPY workers/ml_worker/requirements.gpu.txt /srv/ml-requirements.txt
# The base image already contains the CUDA-matched Torch runtime. Install the
# ML packages without the legacy desktop/Web dependency bundle.
RUN pip install --no-cache-dir --no-deps whisperx==3.7.5 pyannote.audio==3.3.2
RUN pip install --no-cache-dir -r /srv/ml-requirements.txt
# Keep the GPU image build context small and deterministic. The previous
# COPY . /srv pulled the whole monorepo, including artifacts and app build
# outputs, into every worker build. These are the runtime modules imported by
# the ML worker and WhisperX pipeline.
COPY workers /srv/workers
COPY whisperx_atom /srv/whisperx_atom
COPY app /srv/app
COPY diarization_quality.py glossary_utils.py local_io.py media_binaries.py processing_runtime.py transcription_quality.py hotwords.txt /srv/
COPY workers/gpu_lease.py /srv/workers/gpu_lease.py
COPY workers/ml_worker/healthcheck.py /srv/workers/ml_worker/healthcheck.py
LABEL org.opencontainers.image.title="WhisperX Atom GPU Worker" \
      org.opencontainers.image.version="$WHISPERX_RELEASE_VERSION" \
      org.opencontainers.image.revision="$WHISPERX_REVISION" \
      io.whisperx.atom.build-identity="$WHISPERX_BUILD_IDENTITY" \
      org.opencontainers.image.source="WhisperX Atom"
ENV PYTHONPATH=/srv
CMD ["python", "-m", "workers.ml_worker.worker"]
