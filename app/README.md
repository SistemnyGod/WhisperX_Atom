# Legacy local API

## Назначение

`app/` — deprecated legacy/local FastAPI upload surface для development и
fixture диагностики. Production LAN использует
`apps/server/WhisperX.Atom.Api` в immutable Docker runtime. Этот путь не
является альтернативным сервером и не получает новых бизнес-функций.

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

### Ограничения совместимости

Legacy pipeline держит небольшие bounded очереди и при остановке сначала
дренирует стадии `audio → ASR → alignment → diarization → postprocess`.
Задания, оставшиеся в `running` после перезапуска, помечаются
`LEGACY_PIPELINE_INTERRUPTED_AFTER_RESTART`; они не запускаются автоматически,
чтобы не создать дубликат длительной обработки. Повтор выполняется только
явным действием пользователя.

Ошибки стадий сохраняют безопасный код стадии и удаляют только производные
`.asr/.diar` файлы. Исходная запись не удаляется. Для диаризации исходные ASR
сегменты сохраняются до вызова WhisperX, поэтому аварийный или пустой ответ
speaker-assignment не превращает стенограмму в пустую.

Для новой разработки используйте `whisperx_atom.WhisperXCorePipeline` и
серверные workers. Удаление legacy будет отдельным проходом после проверки
регрессионных импортов и production smoke.
