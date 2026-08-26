# Server-first architecture

Status: accepted baseline  
Date: 2026-07-29

## Product shape

WhisperX Atom is implemented as three cooperating products:

1. A native Windows Recorder Agent captures room microphones and optional
   application audio, accepts local voice commands, keeps an offline spool,
   and uploads immutable media chunks.
2. A local server owns meetings, media, jobs, transcripts, speakers,
   summaries, search, retention, and administration.
3. A browser application is the primary user interface for history,
   transcripts, summaries, decisions, tasks, search, and system management.

The desktop agent does not run the production WhisperX or LLM pipeline. The
web application does not directly capture unattended room audio.

## Deployment baseline

The first supported deployment is:

```text
Windows 11 Pro / Enterprise host
├── NVIDIA Windows driver
├── native Recorder Agent
└── WSL2 Ubuntu 24.04
    └── Docker Engine + Compose
        ├── gateway
        ├── web
        ├── api
        ├── postgres
        ├── nats
        ├── media-worker
        ├── ml-worker
        └── summary-worker
```

Linux-hosted Docker Compose remains a supported production target. Containers
must not depend on Windows-only paths or APIs.

## Technology baseline

| Concern | Baseline |
|---|---|
| Web UI | Vue 3, TypeScript, Vite |
| Control API | .NET 10 LTS, ASP.NET Core |
| Recorder Agent | .NET 10 Worker Service plus WPF tray |
| Database | PostgreSQL |
| Durable messaging | NATS JetStream |
| Media processing | FFmpeg and FFprobe |
| Speech ML | Python 3.12, WhisperX, faster-whisper, pyannote |
| Local LLM | llama.cpp server and a tested Qwen 9B-class GGUF |
| Deployment | Docker Compose and Caddy |
| Observability | OpenTelemetry, Prometheus, Grafana, structured logs |

Exact patch versions belong in lock files and image digests. CUDA, PyTorch,
WhisperX, pyannote, and model revisions are upgraded only after the regression
audio corpus passes.

## Component boundaries

### Control API

Owns:

- identities and permissions;
- meetings and recurring meeting series;
- participants and rooms;
- media metadata and upload manifests;
- job state and orchestration;
- transcript and summary versions;
- decisions, action items, audit, and retention.

It never loads CUDA models and never reads an entire media file into memory.

### Media worker

Owns:

- probing and validating media;
- assembling agent chunks;
- extracting and normalizing audio;
- producing archival FLAC, web Opus, ASR WAV, and waveform peaks;
- verifying output duration and checksums.

### ML worker

Owns:

- WhisperX ASR;
- word alignment;
- diarization;
- quality checks and controlled retry passes;
- stable meeting-local speaker identifiers;
- voice-profile matching.

One GPU-heavy stage runs at a time on a 16 GB GPU.

### Summary worker

Owns:

- transcript cleanup and thematic chunking;
- structured fact extraction;
- evidence validation against transcript segment IDs;
- summary, decisions, tasks, risks, and open questions;
- prompt and model version recording.

### Recorder Agent

Owns:

- WASAPI capture and application loopback;
- sample-based timestamps;
- sealed lossless chunks;
- SQLite spool and retry;
- local command execution;
- local recovery after network or process failure.

## Media delivery

The Recorder Agent writes 10-second FLAC chunks at 48 kHz mono. Each chunk is
immutable and addressed by:

```text
session_id + track_id + sequence + sha256
```

Chunks are uploaded with idempotent streaming HTTP PUT. The server writes to a
temporary `.part` file, calculates SHA-256 while streaming, flushes the file,
atomically renames it, persists metadata, and only then acknowledges delivery.

Completed files uploaded from the browser use a resumable upload protocol.
Media bytes are stored in the media object store; NATS messages contain only
identifiers and storage keys.

## Processing lifecycle

```text
CREATED
→ RECORDING
→ UPLOADING
→ INGESTED
→ NORMALIZING
→ TRANSCRIBING
→ ALIGNING
→ DIARIZING
→ IDENTIFYING_SPEAKERS
→ SUMMARIZING
→ READY
```

Any processing state may transition to `FAILED` or `PARTIAL`. Retries create a
new attempt and never erase the previous attempt.

## Speaker identity rule

Meeting-local speaker IDs are created by the final full-recording diarization
pass. Short transport chunks never define final speaker identities.

```text
unique high-confidence voice-profile match → employee display name
missing, weak, or ambiguous match          → Speaker N
```

No manual confirmation is required and no uncertain match is displayed as a
real name. Thresholds are calibrated with real recordings from the target room.

## Persistence rules

PostgreSQL stores metadata, state, transcript segments, speaker mappings,
summary structures, tasks, evidence, versions, and audit events.

The media store keeps recordings and derived files. Original media,
transcripts, corrected transcripts, generated summaries, and approved
protocols are separate versioned entities. Container replacement must never
remove persistent data.

## Reliability rules

- PostgreSQL is the source of truth for business state.
- JetStream delivers work; workers are idempotent.
- Database changes and outgoing events use a transactional outbox.
- Worker deliveries use an inbox/idempotency record.
- The API, workers, and agent stream files instead of buffering whole media.
- Original media is retained until derived artifacts pass validation.
- A failed model process cannot delete or corrupt the source recording.
- Recorder commands controlling capture execute locally before synchronization.

## Initial scaling model

The first deployment uses one API instance, one media worker, and one GPU
worker with GPU concurrency set to one. Scale is added by:

1. moving media storage behind the existing object-store interface;
2. adding CPU media workers;
3. adding a second GPU node and queue consumer;
4. replicating API instances;
5. adding high availability only when operational requirements justify it.

Kubernetes is not part of the first implementation.
