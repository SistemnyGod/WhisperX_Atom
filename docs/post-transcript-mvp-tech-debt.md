# Технический долг после Transcript MVP

Документ фиксирует остаточные проблемы после стабилизации пути `media → job → transcript`. Они не входят в текущий критический цикл и не должны смешиваться с изменением моделей WhisperX, pyannote или Qwen.

## Runtime и диагностика

- GPU worker нужно проверять на реальном GPU runtime; локальная сборка CUDA-образа зависит от доступности внешнего Docker Registry.
- Readiness зависит от heartbeat worker-ов и требует контроля времени на хосте и в PostgreSQL.
- Для старых установок Recorder нужно отдельно подтвердить соответствие запущенного Windows Service последней сборке.
- Старые записи `meeting_speakers` с неизвестными метками не удаляются автоматически; для них нужна отдельная проверяемая cleanup-команда.

## API и Desktop

- `Program.cs` и `ServerApiClient.cs` остаются широкими модульными файлами. Их можно разделить после green E2E без изменения публичных контрактов.
- `TranscriptsViewModel` всё ещё загружает подробные стенограммы отдельными запросами для каждой встречи. Нужен агрегированный list endpoint до масштабирования реестра.
- В legacy XAML и сообщениях встречается mojibake. Исправление должно выполняться отдельным UI-проходом с encoding regression check.

## Legacy и Summary

- Корневой `transcription_quality.py` сохранён как compatibility shim для legacy import-путей.
- В Summary Worker остаётся подтверждённый dead code и неоднородность старых retry-инструкций; это не менялось в Transcript MVP.
- Legacy `app.py` и старые runtime-каталоги сохраняются до отдельной regression validation.

## Regression corpus

- Эталонное аудио не хранится в Git и не загружается во внешние сервисы. В репозитории фиксируются только manifest-метаданные и минимальные пороги.
- Для этапа diarization нужен отдельный локальный fixture с минимум двумя различимыми голосами; текущий длинный SUMMIT-файл используется как Transcript Quality fixture до подтверждения speaker diversity.
