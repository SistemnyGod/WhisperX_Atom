# Import Worker

## Назначение

Наблюдает inbox/hot-folder, безопасно копирует поддерживаемые аудио/видео в
staging, проверяет имя/SHA и создаёт idempotent import job через API.

## Навигация

- `worker.py` — `HotFolderImporter` и bounded polling loop.
- `safe_filename`, `atomic_copy` — path/SHA safety.
- `post_import` — API submission.
- `Dockerfile` / `requirements.txt` — image runtime.

## Эксплуатация

Запускается Compose profile `core`; входной каталог задаётся host volume.
Повторное появление того же SHA не должно создавать вторую meeting/job.
Сбой API оставляет файл в outbox/staging для retry, а не удаляет оригинал.

```powershell
python -m workers.import_worker.worker
pytest -q tests -k "import or inbox"
```

Проверяйте heartbeat и очередь через readiness; не удаляйте inbox вручную во
время активной обработки.
