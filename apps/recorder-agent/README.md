# Recorder Agent

## Назначение

Recorder Agent — локальный IPC/служебный слой для управления записью и
доставкой. Основной захват выполняет `Recorder Host`; Agent не заменяет
AudioGraph и не переписывает канонический архив.

## Навигация

- `AgentStateMachine.cs` — состояния подключения и recovery.
- `AgentPipeHost.cs` / `AgentIpcProtocol.cs` — локальный pipe-контракт.
- `AudioFrameDurableConsumer.cs` — durable consumer локальных фреймов.
- `AudioPhaseQualityGate.cs` — проверки тишины/речи и noise window.
- `WhisperX.Atom.Recorder.Service.csproj` — legacy Windows Service fallback.

## Эксплуатация

Обычный режим запускается Desktop через `Recorder Host`. Legacy Service
используется только при явном `LEGACY_WASAPI`; второй захват параллельно не
запускается. При остановке Agent сначала завершает durable stop, затем закрывает
pipe; spool и незавершённая доставка сохраняются.

Проверки:

```powershell
dotnet build apps/recorder-agent/WhisperX.Atom.Recorder.Core.csproj -c Release
dotnet test tests/WhisperX.Atom.Recorder.Tests/WhisperX.Atom.Recorder.Tests.csproj -c Release --no-restore
```

Не помещайте PCM, токены или текст команд в логи Agent. Для диагностики
используйте snapshot состояний, sequence, RMS/Peak и error code.
