# ADR-0001: Server-first platform boundaries

Date: 2026-07-29  
Status: Accepted

## Context

The repository currently has three execution paths: a desktop GUI, a
watch-folder processor, and a FastAPI prototype. The mature processing logic is
not shared consistently, web jobs are stored as JSON, queues live in process
memory, and the current Docker image omits root modules imported by the web
pipeline.

The target product must support unattended Windows recording, large media,
durable meeting history, local GPU processing, local summaries, and later
horizontal worker scaling.

## Decision

Use a server-first platform with these boundaries:

- Vue browser UI for all history, review, search, summary, and administration;
- .NET modular-monolith control API for business state and orchestration;
- native .NET Windows Recorder Agent for capture, local commands, and spool;
- Python workers only for speech ML;
- FFmpeg/FFprobe media worker;
- PostgreSQL as the source of truth;
- NATS JetStream as durable work delivery;
- filesystem-backed media object store behind an interface;
- Docker Compose for the first server deployment.

The existing Python desktop and watcher remain available during migration.
Their ML quality logic is extracted behind contracts after fixtures exist.

## Consequences

Positive:

- capture failures and model failures are isolated;
- the server can restart without losing meeting state;
- Windows audio APIs remain native;
- API instances never compete for GPU memory;
- workers can later move to separate hosts;
- long-term history has transactional, searchable storage.

Costs:

- the repository becomes polyglot;
- contracts and idempotency require explicit design;
- local development needs .NET, Node, Python 3.12, Docker, and GPU-specific
  smoke environments;
- migration must temporarily support both legacy and server-first paths.

## Rejected alternatives

### Extend the existing FastAPI prototype as the complete control plane

Rejected because the current code couples API startup to in-process model
queues and JSON state. Python remains appropriate for ML workers, but the
prototype is not used as the long-term business-state foundation.

### Put the complete product into the Windows desktop GUI

Rejected because history, permissions, updates, search, and multi-room
operation must be centralized.

### Browser-only recording

Rejected as the primary capture path because unattended background operation,
WASAPI application loopback, offline spool, and reliable recovery require a
native agent.

### Microservices from the first release

Rejected because the initial team and one-server deployment do not justify the
operational complexity. Business modules begin in one API process; media and
ML execution are separate workers because their failure and scaling profiles
are materially different.

### Kubernetes on the first server

Rejected until multiple hosts, multiple GPUs, or availability requirements
make orchestration benefits exceed operational cost.
