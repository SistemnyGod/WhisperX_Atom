# WhisperX Atom backend roadmap

Status: active

## Current baseline

The repository already contains a working server-first backend:

- ASP.NET Core API under `apps/server/WhisperX.Atom.Api`;
- PostgreSQL as the source of truth with migrations `001`–`010`;
- NATS JetStream with transactional outbox/inbox delivery;
- media worker for validation and derived audio files;
- GPU worker for WhisperX ASR, alignment and diarization;
- summary/assistant worker with local llama.cpp and GPU lease;
- Recorder Agent contracts for enrollment, commands and chunk finalization;
- legacy FastAPI/desktop processing paths kept for migration compatibility.

The backend is extended incrementally. Existing public contracts, media keys and worker state transitions remain stable unless a migration explicitly documents a compatible change.

## Delivery phases

### B0 — Baseline and boundaries

- keep API, workers and ML runtime in separate processes;
- keep PostgreSQL as the business-state source of truth;
- keep media bytes out of NATS payloads;
- keep one GPU lease for GPU-heavy work;
- keep legacy desktop/Python entry points until the server path has regression coverage;
- verify API readiness, Compose health, Python contracts and API builds.

### B1 — Searchable meeting history — implemented

Add the first missing domain capability on top of the existing transcript index:

- `GET /api/search?q=&meetingId=&limit=&offset=`;
- search only the latest transcript version for each meeting;
- return real meeting and segment evidence: `meetingId`, `segmentId`, `startMs`, `endMs`;
- enforce the authenticated user's meeting scope; Administrator and Operator may search all meetings;
- reject empty queries and queries longer than 200 characters;
- cap page size at 200 and use deterministic rank/date/ordinal ordering;
- cover the route and SQL scope with a server contract test.

### B2 — Processing operations — recovery smoke implemented

- expose a stable job state model for ingest, normalization, ASR, alignment, diarization, quality and persistence;
- add attempt history and operator-visible error codes without exposing stack traces;
- verify retry idempotency and outbox recovery after API/worker restart;
- add bounded integration smoke for upload → media derivatives → GPU job → transcript persistence;
- support `-RestartWorkers` in `scripts/e2e-core.ps1` to restart `media-worker` and, for GPU runs, `gpu-worker` before polling the same job;
- keep the live E2E execution operator-driven because it requires a real audio fixture, running Compose services and `BOOTSTRAP_ADMIN_PASSWORD`.

### B3 — Meeting intelligence — implemented

- preserve transcript versions and speaker mappings;
- keep summary versions immutable and attach evidence to every supported decision/task;
- add task status transition validation and audit records;
- ensure regeneration creates a new summary version instead of overwriting the previous one;
- make summary persistence idempotent by binding each generated summary to its `SUMMARIZE` job;
- return only decisions and tasks from the latest summary version for a meeting.

### B4 — Assistant and evidence navigation — implemented

- keep meeting and global assistant contexts role-gated;
- persist terminal states `READY`, `NEEDS_REVIEW` and `FAILED`;
- require evidence items to carry `meetingId`, `segmentId`, `startMs` and `endMs` when available;
- keep old evidence readable, but block unsafe source navigation when `meetingId` is absent;
- use polling/SSE with cancellation and bounded timeouts;
- allow trusted Voice Host access to meeting-scoped assistant queries without weakening regular session ownership checks;
- make assistant terminal updates idempotent and deduplicate evidence ids before persistence;
- read evidence timecodes as PostgreSQL `bigint` values in the fallback API path.

### B5 — Operations and administration — implemented

- recurring meeting series;
- audit trail and retention/deletion workflows;
- system metrics for stale leases, failed jobs, low disk, unavailable agents and GPU failures;
- notification/outbox consumers with idempotent delivery;
- role matrix tests for Administrator, Operator, Editor and read-only users;
- add privileged `GET /api/admin/operations` and `GET /api/admin/audit` reads with bounded filters;
- report PostgreSQL-backed job, lease, outbox, agent and GPU failure counters without synthetic health values.

### B6 — Production hardening — implemented

- add `scripts/backup.ps1` and a verify-first `scripts/restore.ps1` for PostgreSQL and media archives;
- validate restore manifests, file sizes and SHA-256 hashes before any data-changing action;
- require explicit `-Apply` for `pg_restore` and optional media replacement, with a guarded target root;
- emit JSON console logs, request timing and an `X-Trace-Id` response header from the API;
- add `scripts/verify-backend-deployment.ps1` to validate required deployment files, migration numbering/order, migration packaging, Docker healthcheck and Compose configuration;
- keep the live restore drill and GPU/quality corpus operator-driven because they require real backup artifacts, secrets and running infrastructure.

### B7 — GPU execution guard — implemented

- keep the GPU worker on `DEVICE=cuda`, `COMPUTE_TYPE=float16` and `REQUIRE_CUDA=true` so unavailable CUDA fails fast instead of silently falling back to CPU;
- keep WhisperX ASR, alignment and pyannote diarization on the same configured CUDA device;
- keep Qwen local inference on the CUDA llama.cpp image with `LLM_GPU_LAYERS > 0` and `LLM_REQUIRE_GPU=true`;
- start the production summary worker with the `llm` Compose profile alongside `core` and `gpu`;
- make `scripts/e2e-gpu.ps1` start and restart the complete `core + gpu + llm` processing path;
- cover the GPU/LLM profile contract in server tests and the Windows/WSL2 runbook.

## Backend invariants

1. The API never loads CUDA models.
2. Workers never trust client-provided paths without storage-key validation.
3. Media is streamed to `.part` files, checksummed, then atomically renamed.
4. Every worker delivery is idempotent and recoverable after a lease timeout.
5. Meeting-scoped API responses are filtered before serialization, not in the client.
6. Evidence points only to persisted transcript segments.
7. No production mock data is used in runtime responses.

## Verification for each phase

```powershell
dotnet build apps/server/WhisperX.Atom.Api/WhisperX.Atom.Api.csproj --no-restore
py -m unittest discover -s tests -q
py -m compileall -q whisperx_atom workers
docker compose -f compose.dev.yml --profile core config --quiet
```

B6 and B7 are implemented. The next maintenance slice is an operator-run restore drill with a real backup, followed by the ASR/alignment/diarization quality corpus and load tests.
