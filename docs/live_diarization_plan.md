# План Live / Near-Real-Time диаризации

Дата: 2026-06-19
Статус: запланировано
Scope: desktop GUI и локальный runtime/watcher. Web/API/Docker в эту задачу не входят.

## Кратко

Сейчас проект работает в file-based режиме: GUI или watcher получают готовый аудиофайл, затем выполняют WhisperX ASR, alignment, diarization, постобработку, сохранение JSON/TXT/quality/journal и отображение в History.

Диаризация прямо во время совещания возможна, но для текущей архитектуры реалистичный вариант - не мгновенная идеальная диаризация, а near-real-time режим с задержкой:

- микрофон пишет аудио чанками;
- каждый чанк попадает в очередь обработки;
- ASR и diarization выполняются последовательно;
- GUI показывает текущий черновой текст с задержкой примерно 30-90 секунд;
- speaker registry стабилизирует имена спикеров между чанками;
- после завершения совещания запускается финальный offline-прогон по полному аудиофайлу.

Live-результат нужен для оперативного просмотра. Финальный offline-прогон остаётся источником истины по качеству.

## Что нужно добавить

- Отдельная вкладка `Live` / `Совещание`, не смешанная с текущей file-based транскрибацией.
- Кнопки: `Начать запись`, `Пауза`, `Продолжить`, `Завершить`, `Открыть папку сессии`.
- Отображение текущего статуса сессии, времени записи, очереди чанков, текущего текста и списка спикеров.
- Session model для хранения состояния live-записи.
- Chunk processing queue для последовательной обработки аудиофрагментов.
- Speaker registry для стабильных имён спикеров между чанками.
- Финальный offline pass после завершения записи.

## Что не нужно делать в первой версии

- Не заменять WhisperX/pyannote новым streaming engine.
- Не обещать нулевую задержку.
- Не смешивать live mode с текущим GUI upload/file flow.
- Не трогать web/browser/API часть.
- Не менять существующий формат результатов, кроме добавления совместимых metadata-полей.

## Session model

Каждая live-сессия должна храниться в отдельной папке:

```text
whisperx_results/
  live_sessions/
    live_YYYYMMDD_HHMMSS/
```

Минимальные поля `session.json`:

- `session_id`: стабильный id, например `live_YYYYMMDD_HHMMSS`.
- `started_at`: ISO timestamp старта.
- `finished_at`: ISO timestamp завершения или пусто, пока сессия активна.
- `status`: `recording`, `paused`, `processing`, `finalizing`, `done`, `failed`.
- `profile`: профиль обработки `fast`, `accurate`, `meeting`, `noisy`.
- `chunks`: список аудиочанков.
- `partial_transcript`: текущий черновой текст.
- `speaker_registry`: стабильная карта спикеров.
- `final_output_paths`: итоговые JSON/TXT/quality/journal после offline-прогона.
- `error`: последняя критическая ошибка.

Поля одного чанка:

- `chunk_id`: порядковый номер.
- `started_at`, `finished_at`.
- `audio_path`: путь к WAV-чанку.
- `status`: `queued`, `processing`, `done`, `failed`.
- `asr_json_path`: путь к результату чанка, если сохранён.
- `text`: распознанный текст чанка.
- `segments`: сегменты чанка.
- `global_offset_sec`: смещение чанка относительно начала сессии.
- `speaker_map`: карта локальных спикеров чанка в стабильных спикеров сессии.
- `error`: ошибка чанка, если была.

## Chunk processing queue

Первая версия должна быть простой и надёжной:

- писать микрофон в WAV-чанки по 15-30 секунд;
- рекомендуемый default: 20 секунд;
- использовать небольшой overlap, например 2 секунды;
- закрытый чанк класть в очередь;
- обрабатывать только один чанк одновременно, чтобы не конфликтовать за GPU;
- при ошибке чанка не останавливать всю запись;
- failed chunk сохранять в session folder с ошибкой.

Ожидаемая задержка live-текста: 30-90 секунд, зависит от модели, GPU и длины чанка.

## Speaker registry

Проблема: diarization на каждом чанке может заново выдавать `SPEAKER_00`, `SPEAKER_01`, и эти labels могут меняться местами между чанками.

Нужен слой `speaker_registry`, который:

- создаёт стабильные имена `Спикер 1`, `Спикер 2`, ...;
- сопоставляет локальных спикеров чанка с уже известными спикерами сессии;
- создаёт нового стабильного спикера, если совпадения нет;
- хранит evidence/score сопоставления;
- позволяет в будущем вручную переименовать спикеров.

Возможные сигналы сопоставления:

- voice embeddings, если доступны;
- reference voice embeddings, если задан файл представления;
- fallback по временной непрерывности и доминирующему порядку реплик.

Важно: live speaker labels будут best-effort. Финальный offline pass должен исправлять и уточнять итоговую диаризацию.

## Финальный offline pass

После нажатия `Завершить`:

1. Склеить все чанки в полный WAV.
2. Запустить существующий file-based pipeline по полному WAV.
3. Создать обычные итоговые артефакты:
   - JSON;
   - TXT;
   - quality report;
   - run journal.
4. Сохранить ссылки на них в `session.json`.
5. Оставить partial live transcript как черновик, не как финальный результат.

## Предлагаемая структура хранения

```text
whisperx_results/
  live_sessions/
    live_YYYYMMDD_HHMMSS/
      session.json
      chunks/
        chunk_0001.wav
        chunk_0001.json
        chunk_0002.wav
        chunk_0002.json
      final/
        meeting_YYYYMMDD_HHMMSS.wav
        meeting_YYYYMMDD_HHMMSS.json
        meeting_YYYYMMDD_HHMMSS.txt
        meeting_YYYYMMDD_HHMMSS_quality.txt
        meeting_YYYYMMDD_HHMMSS_run.json
```

Все metadata-файлы писать атомарно через текущие helpers.

## Зависимости

Сейчас в проекте нет зависимости для записи микрофона.

Кандидат для первой версии:

- `sounddevice` для input audio;
- стандартный `wave` для записи WAV-чанков.

Перед реализацией добавить diagnostics check:

- микрофон доступен;
- sample rate поддерживается;
- output folder writable;
- FFmpeg доступен;
- GPU/CUDA доступна или корректно fallback на CPU;
- `HF_TOKEN` установлен, если включена диаризация.

## Обработка ошибок

- Если один чанк упал, сессия продолжает запись.
- Если diarization чанка упала, сохранять ASR и ставить speaker `UNKNOWN`.
- Если GPU OOM, пробовать CPU fallback или отключать diarization для чанка.
- Если финальный offline pass упал, оставлять session folder и показывать кнопку retry final pass.
- Никогда не удалять записанное аудио автоматически при ошибке.

## Acceptance criteria

- GUI может начать, поставить на паузу, продолжить и завершить live-запись.
- WAV-чанки сохраняются в session folder.
- Чанки обрабатываются последовательно.
- Partial transcript обновляется во время совещания.
- Speaker labels стабильны хотя бы best-effort и сохраняются в `speaker_registry`.
- После завершения создаётся финальный WAV и стандартные JSON/TXT/quality/journal.
- Текущие GUI file-based transcription и watcher не ломаются.

## Test plan

Unit tests:

- генерация `session_id` и путей хранения;
- atomic write `session.json`;
- переходы статусов сессии;
- переходы статусов чанков;
- speaker registry mapping;
- failed chunk handling;
- linking final output paths.

Manual smoke:

- короткая live-сессия 1-2 минуты;
- pause/resume;
- отсутствующий микрофон;
- отсутствующий `HF_TOKEN` при diarization;
- CUDA unavailable fallback;
- final offline pass после `Завершить`.

## Рекомендуемый порядок реализации

1. Добавить session model и storage helpers без GUI.
2. Добавить microphone capture и chunk writer.
3. Добавить chunk queue и partial ASR без diarization.
4. Добавить live diarization и speaker registry.
5. Добавить вкладку `Live` / `Совещание`.
6. Подключить final offline pass.
7. Подключить live sessions к History.

