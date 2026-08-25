# Legacy local API

## Назначение

`app/` — legacy/local FastAPI upload surface для development и fixture
диагностики. Production LAN использует `apps/server/WhisperX.Atom.Api` в
immutable Docker runtime.

## Навигация

- `main.py` — `/`, `/api/jobs`, `/health`.
- `storage.py` — локальное состояние jobs.
- `transcribe.py` / `transcription_pipeline.py` — legacy processing path.
- `templates/` / `static/` — диагностический UI.

## Эксплуатация

Запускайте только локально для development fixture:

```powershell
uvicorn app.main:app --host 127.0.0.1 --port 8080
```

Не публикуйте этот API в LAN и не используйте его как второй production
server. Для рабочих записей используйте canonical Desktop → Server API flow.
