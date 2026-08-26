# Implementation plan

Status: active  
Branch: `codex/server-first-platform`

## Baseline found on 2026-07-29

The current repository contains:

- a large CustomTkinter desktop application;
- a watch-folder processor;
- a small FastAPI web prototype;
- an in-process multi-stage `asyncio.Queue` pipeline;
- JSON job persistence;
- a Dockerfile that copies only `app/`;
- shared processing modules at the repository root;
- one broad non-web unit-test module.

The existing Docker image cannot reliably import the root processing modules
used by `app/transcription_pipeline.py`. The current upload path also reads the
entire request into memory. These are migration inputs, not the target server
architecture.

## Delivery strategy

Development proceeds through vertical slices. Each milestone must leave a
demonstrable end-to-end path and preserve the existing desktop pipeline until
its replacement is verified.

### Milestone 0 — architecture and reproducible baseline

Deliverables:

- accepted architecture document;
- repository map and migration boundaries;
- supported development runtime;
- repeatable baseline test command;
- Docker smoke test;
- regression fixture policy;
- decision log for changes to the fixed stack.

Acceptance:

- a clean clone can run the baseline non-GPU tests;
- the current web image either passes an import smoke test or has a documented
  failing test that the first vertical slice replaces;
- no current user workflow has been deleted.

### Milestone 1 — server skeleton and meeting history

Deliverables:

- `apps/server` .NET solution organised as a modular monolith;
- PostgreSQL schema and migrations for meetings, media assets, jobs, outbox,
  and audit events;
- NATS JetStream development service;
- minimal Vue shell;
- Docker Compose development profile;
- health and readiness endpoints.

Acceptance:

- create and list a meeting through the API and web UI;
- restart API and keep the meeting;
- publish a durable test job through the outbox;
- run the stack with one documented command.

### Milestone 2 — streamed file ingestion and media preparation

Deliverables:

- streamed file upload without whole-file buffering;
- SHA-256, size limits, allowlist, and FFprobe validation;
- local media object-store interface;
- media worker producing archive FLAC, preview Opus, ASR WAV, and waveform
  peaks;
- job progress and error events.

Acceptance:

- upload a large file with bounded API memory;
- interrupt and resume a browser upload;
- reject media without an audio stream;
- restart the media worker and safely retry the same job;
- verify derived duration before marking media ready.

### Milestone 3 — shared ML processing

Deliverables:

- extract reusable processing code from the desktop and watcher paths;
- explicit request and result contracts;
- WhisperX ASR, alignment, diarization, quality checks, and retry policy;
- PostgreSQL transcript versions and segments;
- one GPU lease and subprocess lifecycle;
- CPU-only contract tests plus GPU smoke tests.

Acceptance:

- upload → normalize → transcribe → diarize → persist;
- a restart does not lose the job;
- the same fixture produces equivalent output through the extracted core and
  the migration compatibility path;
- peak GPU memory and runtime are recorded.

### Milestone 4 — summaries and searchable history

Deliverables:

- local llama.cpp runtime integration;
- structured map/reduce summary pipeline;
- evidence references to transcript segments;
- summary versions, decisions, action items, and source hashes;
- PostgreSQL full-text search;
- meeting history and transcript/summary screens.

Acceptance:

- a 60–90 minute Russian transcript produces a stored structured summary;
- every decision and task has evidence or is marked unsupported;
- missing names and deadlines are not invented;
- a summary can be regenerated without overwriting the previous version.

### Milestone 5 — Windows Recorder Agent

Deliverables:

- Windows Service and tray application;
- WASAPI room-microphone capture;
- 10-second sealed FLAC chunks;
- SQLite spool and idempotent PUT delivery;
- manifests, missing-chunk recovery, and finalization;
- heartbeat and room configuration.

Acceptance:

- record for at least two hours without an open browser;
- continue recording through server/network loss;
- resend only missing chunks;
- recover after agent restart;
- assemble a sample-exact complete recording.

### Milestone 6 — speaker profiles and voice commands

Deliverables:

- voice-profile enrolment and versioned embeddings;
- automatic unique high-confidence matching;
- `Speaker N` fallback;
- local wake word, command classifier, streaming ASR fallback, state
  validation, and local execution;
- command audit and server synchronization.

Acceptance:

- no ambiguous voice is assigned an employee name;
- final speaker IDs remain stable throughout one meeting;
- start, pause, resume, marker, status, and stop work without the server;
- a simple command meets the measured latency target on the target room PC.

### Milestone 7 — production hardening

Deliverables:

- OIDC and role enforcement;
- retention and deletion workflows;
- backup and tested restore;
- OpenTelemetry, metrics, logs, dashboards, and alerts;
- Windows/WSL2 installation and upgrade automation;
- pilot report using real room recordings.

Acceptance:

- restore PostgreSQL and media to a clean environment;
- survive Windows, WSL, API, worker, and agent restart scenarios;
- detect low disk, missing chunks, stale agents, failed backups, and GPU
  failures;
- complete a 2–4 week pilot without lost recordings.

## First implementation sprint

The first sprint is intentionally smaller than the full platform.

### Scope

1. Establish a supported Python 3.12 baseline for the legacy tests.
2. Add a Docker import smoke test that exposes the current missing-root-module
   problem.
3. Create the .NET solution skeleton under `apps/server`.
4. Add PostgreSQL and NATS to a development Compose file.
5. Implement the first migration for `meetings`, `jobs`, and
   `outbox_messages`.
6. Implement `POST /api/meetings`, `GET /api/meetings`, and
   `GET /api/meetings/{id}`.
7. Add health/readiness checks.
8. Add a minimal Vue page that creates and lists meetings.
9. Preserve all existing Python entry points.

### Definition of done

```text
docker compose -f compose.dev.yml up -d
→ PostgreSQL healthy
→ NATS healthy
→ API ready
→ web reachable
→ meeting created
→ stack restarted
→ meeting still present
```

The sprint does not yet include Recorder Agent, speaker recognition, Qwen,
Keycloak, monitoring, or a wholesale move of the WhisperX pipeline.

## Immediate technical decisions

- Do not extend JSON job storage for new server features.
- Do not put media bytes on NATS.
- Do not combine the API and GPU model runtime.
- Do not rewrite the existing ML quality logic until contract fixtures exist.
- Do not introduce Kubernetes or multiple storage products in the first slice.
- Do not make exact model/package patch versions architectural promises; pin
  tested revisions in lock files and manifests.

## Baseline commands

Legacy tests require Python 3.12 and the desktop import dependencies:

```powershell
py -3.12 -m unittest discover -s tests -v
```

The current workstation only exposed Python 3.14 during kickoff, and the first
baseline test stopped because `customtkinter` was not installed. A dedicated
3.12 environment is therefore part of Milestone 0 rather than an assumption.
