# Документация WhisperX Atom

Документы в этом каталоге описывают фактическое состояние ветки `codex/server-first-platform`. Если старый документ содержит Docker GPU, WSL2 или Web UI как обязательную часть запуска, используйте его как историческую справку и сверяйтесь с текущим host-runtime.

## Основные документы

| Документ | Назначение |
| --- | --- |
| [Обзор приложения](application-overview.md) | Что делает продукт и какие пользовательские сценарии поддержаны |
| [Текущая архитектура](architecture-current.md) | Границы процессов, размещение компонентов и источники истины |
| [Модули](modules.md) | Ответственность API, Desktop, Agent, workers и legacy-кода |
| [Технологический стек](technology-stack.md) | Языки, фреймворки, ML и инфраструктура |
| [Потоки данных](data-flow.md) | Файл, запись, чанки, media pipeline, WhisperX и transcript |
| [Настройка](configuration.md) | `.env`, порты, каталоги и режимы обработки |
| [Эксплуатация host-runtime](operations-host-runtime.md) | Запуск, остановка, doctor, watchdog и восстановление |
| [API и IPC](api-and-ipc.md) | Основные REST, SSE, TUS и Named Pipe контракты |
| [Тестирование](testing.md) | Сборка, targeted tests, doctor и E2E |
| [«Мифодий»: текущее состояние](mifodiy-current-state.md) | Реализованные функции, подтверждённые gates и оставшиеся дефекты Voice/Assistant |

## Справочные и исторические документы

- [Server-first architecture](server_first_architecture.md) — исходные архитектурные границы и ADR-материалы; часть deployment-инструкций устарела.
- [Server-first MVP](server_first_mvp.md) — ранний MVP и Web/Tus поток; для запуска используйте `scripts/run-whisperx.ps1`.
- [Windows/WSL2 runbook](runbook_windows_wsl2.md) — исторический Docker/WSL2 runbook.
- [Recorder Agent](recorder_agent.md) — раннее описание Agent; актуальные IPC-команды перечислены в [API и IPC](api-and-ipc.md).
- [Post-Transcript MVP technical debt](post-transcript-mvp-tech-debt.md) — список незакрытого долга, а не инструкция по запуску.

## Правило актуальности

Документ считается текущим, если он ссылается на `run-whisperx.ps1`, host GPU Worker, `doctor-whisperx.ps1` и `artifacts/runtime/state.json`. Старые команды и сервисы нельзя считать поддерживаемым сценарием без проверки соответствующего скрипта.
