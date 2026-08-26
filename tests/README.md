# Tests and acceptance

## Назначение

Каталог содержит unit, contract, integration, Desktop/Voice behavior и
release-gate проверки. Тесты не должны менять production Docker volumes,
архивы или пользовательские записи.

## Навигация

- `test_*_contracts.py` — schema, IPC, PowerShell и release contracts.
- `tests/WhisperX.Atom.*.Tests/` — .NET API/Voice/Desktop/Recorder/Semantic.
- `regression-audio/` — audio fixtures и privacy-safe quality regressions.
- `qa/` / `scripts/run-mifodiy-*.py` — Assistant/Mifodiy cases.
- `artifacts/acceptance/` — identity-bound evidence, не исходные данные.

## Эксплуатация

```powershell
py -3.12 -m pytest -q
dotnet test tests/WhisperX.Atom.Voice.Tests/WhisperX.Atom.Voice.Tests.csproj -c Release --no-restore
```

Критический suite со статусом `SKIPPED` не считается release PASS. Hardware
сценарии классифицируются только как `PASSED` или `BLOCKED_BY_HARDWARE`.
При отладке используйте отдельный temp root и удаляйте только созданные
вами временные файлы.
