# whisperx_atom core

## Назначение

Общие Python-контракты и quality logic для media/GPU/transcription pipeline.
Пакет не является самостоятельным сервером: его вызывают `workers/*` и
diagnostic runners.

## Навигация

- `contracts.py` / `pipeline_contract.py` — публичные stage/job contracts.
- `core_pipeline.py` / `processing.py` — orchestration.
- `asr_engine.py` / `alignment_engine.py` / `diarization_engine.py` — ASR/V2.
- `preprocessing_engine.py` / `postprocessing_engine.py` — derivative stages.
- `audio_signal.py` / `transcript_quality.py` — quality gates.
- `gpu_scheduler.py` / `runtime.py` / `runtime_state.py` — runtime ownership.
- `storage.py` / `checkpoint_store.py` — durable artifacts/checkpoints.

## Эксплуатация

Изменения в этом пакете требуют запуска Python unit/contract suite и
проверки backward compatibility JSON contracts. Не писать пользовательские
аудио/тексты в debug output. V1 должен быть durable и независим от V2/LLM;
при ошибке качества возвращайте стабильный code, а не скрытый retry.
