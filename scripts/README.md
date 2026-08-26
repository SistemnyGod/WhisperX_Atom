# Runtime scripts

## Назначение

PowerShell/Python scripts — единственная поддерживаемая навигация запуска,
doctor, recovery, release и acceptance. Скрипты не заменяют API/worker
контракты и не должны редактировать PostgreSQL напрямую.

## Навигация

- `start-*` / `stop-*` — запуск и безопасная остановка runtime.
- `doctor-*` — health, identity, model и readiness checks.
- `build-*` / `publish-*` — reproducible bundle/installer staging.
- `acceptance-*`, `e2e-*`, `run-*-qa.*` — smoke и evidence runners.
- `supervise-server-runtime.ps1` / `install-server-startup-task.ps1` —
  Supervisor lifecycle.
- `backup.ps1` / `restore.ps1` — backup/rollback; volumes не удаляются.
- `historical-import.py` — локальный TXT/DOCX PREVIEW и подтверждённый APPLY через admin API; пути и текст не попадают в preview artifact.

## Эксплуатация

Перед release используйте clean commit, pinned manifest и `-Mode Release`.
Для диагностики сначала запускайте read-only doctor/preview. `APPLY`,
maintenance, Scheduled Task registration и удаление artifacts требуют
явного admin action. Не применяйте `docker system prune`, `volume prune` или
глобальные удаления cache.

Все acceptance artifacts должны содержать IDs/metrics/identity, но не PCM,
transcript text, вопросы, токены или секреты.
