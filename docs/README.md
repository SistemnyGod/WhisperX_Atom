# Документация WhisperX Atom

Документы в этом каталоге описывают фактическое состояние ветки `codex/server-first-platform`. Текущий LAN runtime использует Docker GPU на отдельном Server Node; Desktop-клиенты не запускают Docker и не управляют сервером. Старые host-GPU, WSL2 и Web UI инструкции являются историческими.

## Основные документы

| Документ | Назначение |
| --- | --- |
| [Системное руководство](system-guide.md) | Единое объяснение структуры проекта, всех модулей, потоков данных, запуска и recovery |
| [Обзор приложения](application-overview.md) | Что делает продукт и какие пользовательские сценарии поддержаны |
| [Текущая архитектура](architecture-current.md) | Границы процессов, размещение компонентов и источники истины |
| [Архитектурная эволюция](architecture-incremental.md) | Core Pipeline, state machine jobs, GPU lease и безопасные этапы рефакторинга |
| [Модули](modules.md) | Ответственность API, Desktop, Agent, workers и legacy-кода |
| [Справочник модулей](module-reference.md) | Подробные границы исходных модулей, storage и точки расширения |
| [Clean installed runtime](clean-installed-runtime.md) | Единая identity Desktop/Recorder/Voice Host и границы production payload |
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

Документ считается текущим, если он описывает Server Node/Supervisor, immutable Server Bundle, container GPU и `doctor-server-bundle.ps1`. Старые host-GPU команды и сервисы нельзя считать поддерживаемым сценарием без отдельного диагностического флага.
