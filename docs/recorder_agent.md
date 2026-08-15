# Windows Recorder Host — локальный запуск

Поддерживаемый Phase 1 runtime — current-user AudioGraph Recorder Host из
`C:\Program Files\WhisperX Atom\RecorderHost`. Legacy Windows Service остаётся
ручным fallback и не должен запускаться параллельно с Host.

Host умеет:

- открыть выбранный или Windows-default microphone через AudioGraph;
- нормализовать AudioGraph Float32 в mono PCM16;
- сохранять raw PCM-сегменты в SQLite-backed spool (30 секунд по умолчанию);
- завершать запись на durable raw-границе независимо от FFmpeg;
- кодировать PCM в FLAC в фоне через SQLite backlog с bounded retry;
- считать SHA-256 и сохранять метаданные чанка;
- выполнять команды START/PAUSE/RESUME/STOP через IPC v6.

Исходные аудиофайлы из `C:\Users\AI_server\Documents\Audacity` автоматически
не читаются. Системный loopback и дополнительные дорожки не входят в Phase 1.

## Предварительные условия

- Windows 10/11;
- установленный Desktop/Host одной build identity;
- доступный выбранный/default microphone;
- FFmpeg/ffprobe рекомендуются для FLAC и master, но их временная
  недоступность не блокирует START и STOP: состояние будет `WAITING_FOR_ENCODER`.

## Быстрый smoke

```powershell
$env:ATOM_AGENT_DATA_ROOT = "C:\WhisperXAtom\Agent"
Start-Process "C:\Program Files\WhisperX Atom\RecorderHost\WhisperX.Atom.Recorder.Host.exe"
```

После завершения в каталоге `ATOM_AGENT_DATA_ROOT` должны появиться:

```text
agent.db
agent-YYYYMMDD.log
sessions/<session>/<track>/<sequence>.pcm
sessions/<session>/<track>/<sequence>.flac
```

Остановить запись следует штатной командой STOP. Legacy Service запускается
только для отдельной диагностики и не является основным runtime.

## Raw-first и восстановление

1. Источником заданий encoder является SQLite: `RAW_READY`/`ENCODE_FAILED`, а не
   память процесса. Raw удаляется только после проверенного FLAC.
2. `STOP` возвращает `LOCAL_READY` после flush/rename/hash raw; `ArchivePath` и
   отправка появляются позже. При отключённом сервере запись продолжается локально.
3. `ATOM_RAW_FINALIZER_QUEUE_CAPACITY` ограничивает in-memory finalizer (2–32,
   default 4). Переполнение оставляет `.pcm.part` для recovery.
4. Токены и аудио не входят в диагностические архивы; enrollment управляется
   Desktop и DPAPI.
