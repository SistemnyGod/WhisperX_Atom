FROM pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime

ENV DEBIAN_FRONTEND=noninteractive
ENV PYTHONUNBUFFERED=1

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /srv

COPY requirements.txt /srv/requirements.txt
COPY requirements.constraints.txt /srv/requirements.constraints.txt
RUN pip install --no-cache-dir -r /srv/requirements.txt -c /srv/requirements.constraints.txt

COPY . /srv/

# Keep legacy root imports resolvable while the processing core is migrated.
ENV PYTHONPATH=/srv

EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
