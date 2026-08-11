# Потоки данных

## Файл → стенограмма

```mermaid
sequenceDiagram
  participant U as Desktop
  participant T as tusd
  participant A as API
  participant O as Outbox Relay
  participant N as NATS JetStream
  participant M as Media Worker
  participant G as GPU Worker
  participant P as PostgreSQL

  U->>A: POST upload reservation
  U->>T: POST upload
  U->>T: HEAD / PATCH blocks (16 MiB)
  T->>A: completion hook
  A->>P: media + job + outbox
  O->>N: publish media.ingest
  N->>M: durable delivery
  M->>P: probe / normalize / stage
  M->>N: publish ml.transcribe
  N->>G: durable delivery
  G->>G: WhisperX ASR
  G->>G: quality gate / alignment / diarization
  G->>P: transcript version + quality metadata
  U->>A: SSE / polling job status
```

TUS resume выполняется через `HEAD` и чтение `Upload-Offset`; при ошибке повторяется текущий блок, а не весь файл. После подтверждённого блока retry counter сбрасывается.

## Запись → серверная обработка

```mermaid
flowchart TD
  Start[Desktop START] --> Preflight[PREFLIGHT]
  Preflight --> Capture[WASAPI microphone + loopback]
  Capture --> PCM[Local PCM buffers]
  PCM --> FLAC[FLAC chunks около 10 секунд]
  FLAC --> Spool[SQLite spool + SHA-256]
  Spool --> Archive[Permanent local archive]
  Spool --> Bind{API available?}
  Bind -->|no| Offline[meeting_id = null\nWAITING_FOR_API]
  Bind -->|yes| Session[Bind server recording session]
  Offline --> Recover[Retry binding after recovery]
  Recover --> Session
  Session --> Upload[Upload only missing chunks]
  Upload --> Finalize[Idempotent finalize]
  Finalize --> Media[Media Worker]
  Media --> Whisper[WhisperX GPU Worker]
  Whisper --> Transcript[READY / PARTIAL_READY transcript]
```

## Processing status

Типовая последовательность: `VALIDATING → UPLOADING → MEDIA_PROCESSING → TRANSCRIBING → ALIGNING → DIARIZING → TRANSCRIPT_READY` или `PARTIAL_READY`. Ошибка обязательного ASR приводит к `FAILED`; ошибка optional alignment/diarization сохраняет пригодный ASR-текст.

## Идентификаторы корреляции

Для диагностики сохраняется цепочка:

```text
recording_session_id
  → meeting_id
  → media_asset_id
  → job_id
  → trace_id
```

Локальный `recording_session_id` не заменяет серверный `meeting_id`. При offline-start встреча создаётся только после восстановления связи, а уже записанный spool привязывается к реальному идентификатору.
