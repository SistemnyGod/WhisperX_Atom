# Тестирование и приёмка

## Быстрые проверки

```powershell
py -m pytest -p no:cacheprovider tests/test_transcript_runtime_contracts.py tests/test_transcript_mvp_contracts.py tests/test_nonweb_risks.py -q
dotnet build apps/server/WhisperX.Atom.Api/WhisperX.Atom.Api.csproj --no-restore
dotnet build apps/desktop/WhisperX.Atom.Desktop/WhisperX.Atom.Desktop.csproj --no-restore
.\scripts\check-desktop-ui-encoding.ps1
.\scripts\doctor-whisperx.ps1 -SkipRegistry
```

Для Recorder/Voice Host используйте соответствующие `.csproj`; Desktop acceptance умеет пропускать Docker и GPU через `-SkipDocker` и `-SkipGpu`.

## Реальный Transcript E2E

```powershell
.\scripts\e2e-transcript.ps1 `
  -AudioPath "C:\Users\AI_server\Downloads\SUMMIT_ бизнес-центр инструктажи.m4a" `
  -Runs 1
```

Путь к аудио является локальной подсказкой и не означает, что файл хранится в Git или отправляется во внешний сервис. E2E создаёт отдельный run directory в `artifacts/transcription-mvp`, запускает doctor и сохраняет result/logs/doctor output.

Проверять нужно не только exit code:

- новая цепочка содержит новый `meetingId`, `mediaAssetId`, `jobId` и transcript;
- segments не пустые;
- текст не пустой;
- quality report и quality score сохранены;
- статус — `READY` или обоснованный `PARTIAL_READY`;
- пустой и оборванный transcript не получает `READY`;
- повторный запуск не переиспользует старую встречу по имени файла;
- локальный архив и Recorder spool не изменяются деструктивно.

После первого green прогона:

```powershell
.\scripts\e2e-transcript.ps1 `
  -AudioPath "C:\Users\AI_server\Downloads\SUMMIT_ бизнес-центр инструктажи.m4a" `
  -Runs 5
```

Для вертикальной проверки `V1 → V2 → Summary` добавьте `-WithSummary`.
Перед этим полный локальный runtime должен быть запущен через
`.\scripts\run-whisperx.ps1` без `-TranscriptOnly`; параметр поднимет профиль
`llm` и потребует готовый Summary Worker/Qwen.

## Ручные recovery-сценарии

1. Остановить host GPU Worker во время job и убедиться, что watchdog восстанавливает процесс, а job получает redelivery.
2. Отключить API во время записи: Agent должен сохранить archive/spool и продолжить после восстановления.
3. Прервать TUS upload: повторить клиент и проверить продолжение по `Upload-Offset`.
4. Перезапустить NATS/media/GPU worker: PostgreSQL job и локальная очередь должны сохраниться.
5. Выполнить `stop-whisperx.ps1` и затем `run-whisperx.ps1`: незавершённая очередь не удаляется.

## Известные границы

- Полный diarization acceptance требует реальной двухспикерной записи.
- GPU E2E зависит от локального CUDA/WhisperX окружения и HF доступа.
- Qwen Summary/Assistant не является условием готовности Transcript MVP, если `AUTO_SUMMARY_ENABLED=false`.
- Исторические contract tests могут проверять структуру кода; для критических изменений приоритет имеют behavioral tests и реальный E2E.
