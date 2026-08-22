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

В Server Bundle по умолчанию используется
`ASSISTANT_EMBEDDING_PROVIDER=onnx`. Провайдер загружает заранее проверенный
snapshot `paraphrase-multilingual-MiniLM-L12-v2` через `onnxruntime` с
единственным `CPUExecutionProvider`; сеть и CUDA во время старта не
используются. Пути к immutable-файлам задаются `ASSISTANT_EMBEDDING_ONNX_PATH`
и `ASSISTANT_EMBEDDING_TOKENIZER_PATH`.

Для development rolling upgrade допускается безопасный fallback на
`hashed-local-v1`: readiness/heartbeat фиксирует причину деградации, а hash-
provider не имеет права создавать evidence только по cosine similarity.
Server Node release работает fail-closed: при
`ASSISTANT_EMBEDDING_REQUIRE_VERIFIED=true` обязательны оба SHA256, и worker
не стартует как готовый, если snapshot отсутствует или повреждён. Реальный
ONNX-provider может создать semantic-only anchor только при `raw cosine >= 0.72`
и общем hybrid score `>= 0.30`.

Параметры безопасно ограничены и не меняют API:

```text
ASSISTANT_FTS_ANCHOR_LIMIT=64
ASSISTANT_SEMANTIC_CANDIDATE_LIMIT=512
ASSISTANT_FINAL_TOP_K=12
ASSISTANT_NEIGHBOUR_LIMIT=36
ASSISTANT_HYBRID_MIN_SCORE=0.30
ASSISTANT_HYBRID_EMBEDDING_MIN=0.72
ASSISTANT_EMBEDDING_PROVIDER=onnx
ASSISTANT_EMBEDDING_ONNX_PATH=/models/embeddings/paraphrase-multilingual-MiniLM-L12-v2.onnx
ASSISTANT_EMBEDDING_TOKENIZER_PATH=/models/embeddings/tokenizer.json
ASSISTANT_EMBEDDING_ONNX_SHA256=<sha256>
ASSISTANT_EMBEDDING_TOKENIZER_SHA256=<sha256>
ASSISTANT_EMBEDDING_REQUIRE_VERIFIED=true  # только для release Server Node
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
