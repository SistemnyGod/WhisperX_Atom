# Hybrid retrieval Assistant

Assistant retrieval теперь использует один строгий контур:

```text
RBAC + meetingId + quality gate
    ↓
Russian PostgreSQL FTS
    +
локальное embedding-представление
    ↓
детерминированный rerank
    ↓
до 12 anchors + соседи той же transcript/meeting
    ↓
до 36 сегментов / 36 000 символов
    ↓
RETRIEVED evidence snapshot
    ↓
Qwen → grounding → CITED snapshot
```

## Границы безопасности

`AssistantRepository.context()` получает только строки, прошедшие SQL-фильтры:

- выбранный `meetingId` для `CURRENT_MEETING` и `LIVE_MEETING`;
- `m.owner_id` для обычного пользователя;
- расширенный scope только для `Administrator`/`Operator`;
- последняя версия transcript;
- `READY`/`PARTIAL_READY` без `ASR_LANGUAGE_MISMATCH`, `AUDIO_SIGNAL_UNUSABLE` и `NO_SPEECH_DETECTED`;
- скрытые технические сегменты исключаются.

Embedding-код не принимает meeting IDs и не выполняет SQL. Соседи добавляются
только по тройке `(meeting_id, transcript_id, ordinal)`, поэтому rerank не
может пересечь границу совещания или версии стенограммы. Evidence фиксируется
до запуска Qwen тем же `snapshot_evidence`, что использовался в FTS-контуре.

## Провайдер embeddings

По умолчанию используется `ASSISTANT_EMBEDDING_PROVIDER=auto` (режим
offline-safe):

1. если в образе/локальном кэше доступен `sentence-transformers`, загружается
   уже подготовленная модель из `ASSISTANT_EMBEDDING_MODEL` (по умолчанию
   multilingual MiniLM); `auto` не скачивает модель при старте;
2. при отсутствии optional-пакета или модели включается dependency-free
   `hashed-local-v1` — нормализованные русские токены, символьные n-граммы и
   небольшой прозрачный словарь синонимов. Он не отправляет текст по сети.

Параметры безопасно ограничены и не меняют API:

```text
ASSISTANT_FTS_ANCHOR_LIMIT=64
ASSISTANT_SEMANTIC_CANDIDATE_LIMIT=512
ASSISTANT_FINAL_TOP_K=12
ASSISTANT_NEIGHBOUR_LIMIT=36
ASSISTANT_HYBRID_MIN_SCORE=0.30
ASSISTANT_HYBRID_EMBEDDING_MIN=0.72
ASSISTANT_EMBEDDING_CACHE=4096
ASSISTANT_EMBEDDING_DIMENSION=384
```

FTS остаётся самым сильным сигналом точного совпадения. Embedding помогает
перефразировкам, lexical-нормализация поддерживает русские словоформы,
а низкосигнальные hash-collisions отбрасываются порогом. При недоступности
embedding-провайдера Assistant продолжает работать на локальном fallback, не
ослабляя grounding или quality gates.

## Диагностика

`assistant_queries.answer_metadata.retrieval` сохраняет только технические
метрики: provider, число кандидатов, FTS-кандидатов, anchors, выбранных
сегментов и scope meeting ID. Вопросы, ответы и текст стенограммы туда не
дублируются. Это позволяет сравнивать FTS и hybrid на acceptance-тестах без
изменения evidence API.
