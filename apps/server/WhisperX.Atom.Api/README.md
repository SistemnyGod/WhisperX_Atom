# WhisperX.Atom.Api

## Назначение

ASP.NET API — единственная серверная точка для Desktop/Agent: auth, meetings,
recording finalization, pipeline snapshots, Assistant/Summary jobs, readiness и
внутренние repair/doctor endpoints.

## Навигация

- `Program.cs` — DI, middleware, endpoint registration и health.
- `UnifiedProductStore.cs` — DB access и bounded repository operations.
- `AssistantModeResolver.cs` — `AUTO` → `GENERAL_CHAT`/meeting modes.
- `PipelineSnapshot.cs` — состояние stages/jobs для Desktop.
- `RecordingFinalizeContracts.cs` / `RecordingFinalizeSupport.cs` — finalize и
  idempotent delivery contracts.
- `Migrations/` — применяемая последовательность schema; вручную не редактировать.

## Эксплуатация

API работает внутри Docker Compose и публикует gateway-порт, указанный в
release config. Проверяйте `/health/ready` и authenticated
`/api/internal/runtime/readiness`; внешний клиент не должен обращаться к
PostgreSQL/NATS напрямую. Для безопасного восстановления встречи используйте
существующий repair endpoint через admin Desktop/API flow, а не SQL.

```powershell
dotnet build apps/server/WhisperX.Atom.Api/WhisperX.Atom.Api.csproj -c Release
dotnet test tests/WhisperX.Atom.Api.Tests/WhisperX.Atom.Api.Tests.csproj -c Release --no-restore
```

Meeting answers используют canonical transcript evidence; Assistant history не
является evidence. При отсутствии подтверждения API возвращает `NO_EVIDENCE`.
