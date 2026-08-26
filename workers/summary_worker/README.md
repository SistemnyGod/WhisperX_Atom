# Summary / Assistant Worker

## Назначение

Один worker обслуживает две очереди: `llm.summarize` и `llm.assistant`, используя
общий resident Qwen/llama runtime и GPU scheduler. Это один Assistant контур,
а не два ассистента.

## Навигация

- `worker.py` — `SummaryWorker`, `AssistantWorker`, leases и terminal states.
- `assistant.py` / `answer_planner.py` — query planning и response contract.
- `hybrid_retrieval.py` / `evidence_bundles.py` — canonical evidence.
- `grounding.py` / `evidence_reasoner.py` — fail-closed claims validation.
- `deterministic_summary.py` / `fact_extraction.py` — V1/V2 fallback и facts.
- `contracts.py` — summary/assistant JSON schemas.

## Эксплуатация

Запускается profile `llm`. Сначала проверяйте LLM Doctor и model identity;
`GENERAL_CHAT` не требует meeting evidence, а meeting modes обязаны пройти
retrieval/grounding или вернуть `NO_EVIDENCE`. При отказе Qwen deterministic
summary сохраняется с `NEEDS_REVIEW`; retry должен улучшать тот же summary без
дубля. Не помещайте вопрос, ответ или evidence text в telemetry.

Проверяйте queue/lease/terminal status и один `responseId`/TTS. Перезапуск
worker не должен создавать второй Assistant query.
