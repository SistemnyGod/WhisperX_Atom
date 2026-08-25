# Исторический импорт стенограмм

Модуль переносит старые TXT/DOCX из локального каталога в каноническую
стенограмму и Meeting Memory. Qwen и обучение модели в импорте не участвуют:
Мифодий получает факты только после индексации canonical transcript segments.

## Навигация и режимы

```text
локальная папка
  → scripts/historical-import.py --mode PREVIEW
  → privacy-safe report
  → проверка решений IMPORT/REVIEW_REQUIRED/QUARANTINE
  → POST /api/admin/historical-imports (APPLY)
  → transcript v1 (HISTORICAL_IMPORT)
  → memory.index → Meeting Memory
```

PREVIEW не меняет сервер и не отправляет исходный текст. В отчёте остаются
только имена файлов, hashes, дата/точность даты, формат, число сегментов и
слов, timing quality и решение дедупликации. APPLY отправляет сегменты только
аутентифицированному admin/operator API; локальные пути и исходные файлы в API
не принимаются.

## Эксплуатация

1. Сначала сделайте резервную копию PostgreSQL.
2. Запустите `py -3.12 scripts/historical-import.py "<каталог>" --mode PREVIEW --report historical-preview.json`.
3. Проверьте `QUARANTINE` (нет даты, повреждённый DOCX, summary/test) и
   `REVIEW_REQUIRED` (похожие, но не идентичные документы).
4. Применяйте сначала небольшую партию, затем повторяйте APPLY. Повторный
   APPLY безопасен: уникальная пара owner + canonical SHA возвращает прежние
   IDs и не создаёт вторые meeting/transcript/memory job.
5. После APPLY дождитесь обработки `memory.index`; исходная стенограмма имеет
   статус `PARTIAL_READY` и предупреждение `HISTORICAL_IMPORT_UNVERIFIED`.

TXT с `[HH:MM:SS]` получает timing quality `EXACT`. DOCX без таймкодов
сохраняется с `startMs=endMs=0` и `ABSENT`; дата всё равно должна быть однозначно
найдена в имени (`DD.MM.YYYY` или `YYYYMMDD_HHMMSS`). Summary-файлы никогда не
становятся evidence.

