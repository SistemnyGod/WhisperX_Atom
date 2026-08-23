using System.Collections.ObjectModel;
using System.Net.Http;
using WhisperX.Atom.Desktop;

namespace WhisperX_Atom_Desktop.Services;

public enum UiStatusKind
{
    Neutral,
    Processing,
    Success,
    Warning,
    Error
}

public sealed record UiStatus(string Code, string Text, UiStatusKind Kind);

public static class UiStatusMapper
{
    private static readonly IReadOnlyDictionary<string, UiStatus> Statuses =
        new ReadOnlyDictionary<string, UiStatus>(new Dictionary<string, UiStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["CREATED"] = new("CREATED", "Создано", UiStatusKind.Neutral),
            ["ACTIVE"] = new("ACTIVE", "Активен", UiStatusKind.Success),
            ["ONLINE"] = new("ONLINE", "Онлайн", UiStatusKind.Success),
            ["OPEN"] = new("OPEN", "Открыто", UiStatusKind.Processing),
            ["NEEDS_REVIEW"] = new("NEEDS_REVIEW", "На проверке", UiStatusKind.Warning),
            ["ANSWERED"] = new("ANSWERED", "Ответ готов", UiStatusKind.Success),
            ["ANSWERED_WITH_WARNING"] = new("ANSWERED_WITH_WARNING", "Ответ готов с предупреждением", UiStatusKind.Warning),
            ["NO_EVIDENCE"] = new("NO_EVIDENCE", "Нет подтверждённых источников", UiStatusKind.Warning),
            ["LIVE_MEETING_NOT_READY"] = new("LIVE_MEETING_NOT_READY", "Свежий live-фрагмент ещё не готов", UiStatusKind.Warning),
            ["GROUNDING_REJECTED"] = new("GROUNDING_REJECTED", "Ответ не прошёл проверку источников", UiStatusKind.Error),
            ["LLM_UNAVAILABLE"] = new("LLM_UNAVAILABLE", "ИИ-помощник недоступен", UiStatusKind.Error),
            ["CLARIFICATION_REQUIRED"] = new("CLARIFICATION_REQUIRED", "Нужно уточнение контекста", UiStatusKind.Warning),
            ["TRANSCRIPT_NOT_READY"] = new("TRANSCRIPT_NOT_READY", "Стенограмма ещё не готова", UiStatusKind.Warning),
            ["DONE"] = new("DONE", "Готово", UiStatusKind.Success),
            ["QUEUED"] = new("QUEUED", "В очереди", UiStatusKind.Processing),
            ["SCHEDULED"] = new("SCHEDULED", "Запланировано", UiStatusKind.Processing),
            ["WAITING_FOR_OUTBOX"] = new("WAITING_FOR_OUTBOX", "Ожидает передачи серверу", UiStatusKind.Processing),
            ["PROCESSING"] = new("PROCESSING", "В обработке", UiStatusKind.Processing),
            ["ASSISTANT_WAITING_FOR_GPU"] = new("ASSISTANT_WAITING_FOR_GPU", "Мифодий ждёт освобождения GPU", UiStatusKind.Warning),
            ["ASSISTANT_GPU_BUSY_TIMEOUT"] = new("ASSISTANT_GPU_BUSY_TIMEOUT", "GPU занят слишком долго — повторите вопрос позже", UiStatusKind.Warning),
            ["ASSISTANT_QUEUE_TIMEOUT"] = new("ASSISTANT_QUEUE_TIMEOUT", "Вопрос слишком долго ожидает обработки", UiStatusKind.Warning),
            ["ASSISTANT_RETRY_EXHAUSTED"] = new("ASSISTANT_RETRY_EXHAUSTED", "Сервер исчерпал попытки обработки", UiStatusKind.Error),
            ["LOCAL_COMMAND_REQUIRED"] = new("LOCAL_COMMAND_REQUIRED", "Нужна явная команда записи", UiStatusKind.Warning),
            ["VOICE_ASSISTANT_SERVER_UNREACHABLE"] = new("VOICE_ASSISTANT_SERVER_UNREACHABLE", "Сервер недоступен по сети", UiStatusKind.Warning),
            ["VOICE_ASSISTANT_ACCEPTANCE_TIMEOUT"] = new("VOICE_ASSISTANT_ACCEPTANCE_TIMEOUT", "Сервер принимает запрос дольше обычного", UiStatusKind.Warning),
            ["VOICE_ASSISTANT_SERVER_ERROR"] = new("VOICE_ASSISTANT_SERVER_ERROR", "Сервер не смог обработать вопрос", UiStatusKind.Error),
            ["VOICE_ASSISTANT_AUTH_REQUIRED"] = new("VOICE_ASSISTANT_AUTH_REQUIRED", "Требуется повторный вход", UiStatusKind.Warning),
            ["PROCESSING_STAGE_WAITING_FOR_GPU"] = new("PROCESSING_STAGE_WAITING_FOR_GPU", "Мифодий ждёт освобождения GPU", UiStatusKind.Warning),
            ["PROCESSING_STAGE_LOADING_MODEL"] = new("PROCESSING_STAGE_LOADING_MODEL", "Загрузка языковой модели", UiStatusKind.Processing),
            ["PROCESSING_STAGE_GENERATING"] = new("PROCESSING_STAGE_GENERATING", "Мифодий формирует ответ", UiStatusKind.Processing),
            ["PROCESSING_STAGE_GROUNDING"] = new("PROCESSING_STAGE_GROUNDING", "Проверка ответа по источникам", UiStatusKind.Processing),
            ["PROCESSING_STAGE_DELIVERING_TTS"] = new("PROCESSING_STAGE_DELIVERING_TTS", "Подготовка голосового ответа", UiStatusKind.Processing),
            ["GPU_JOB_ORPHANED"] = new("GPU_JOB_ORPHANED", "ИИ-подсистема требует восстановления", UiStatusKind.Error),
            ["RUNNING"] = new("RUNNING", "В обработке", UiStatusKind.Processing),
            ["RECORDING"] = new("RECORDING", "Идёт запись", UiStatusKind.Processing),
            ["RECORDING_INTERRUPTED"] = new("RECORDING_INTERRUPTED", "Запись прервана — ожидается Recorder Agent", UiStatusKind.Warning),
            ["ADMIN_REVIEW"] = new("ADMIN_REVIEW", "Требуется проверка записи", UiStatusKind.Warning),
            ["CHECKING"] = new("CHECKING", "Проверка устройств", UiStatusKind.Processing),
            ["PREPARING"] = new("PREPARING", "Подготовка", UiStatusKind.Processing),
            ["UPLOADING"] = new("UPLOADING", "Синхронизация", UiStatusKind.Processing),
            ["MEDIA_PROCESSING"] = new("MEDIA_PROCESSING", "Подготовка медиа", UiStatusKind.Processing),
            ["TRANSCRIBING"] = new("TRANSCRIBING", "Транскрибация", UiStatusKind.Processing),
            ["READY_FOR_ASR"] = new("READY_FOR_ASR", "Ожидает транскрибации", UiStatusKind.Processing),
            ["WAITING_FOR_GPU"] = new("WAITING_FOR_GPU", "Ожидает GPU", UiStatusKind.Warning),
            ["GPU_PRIORITY_WAIT_RETRY_PENDING"] = new("GPU_PRIORITY_WAIT_RETRY_PENDING", "V2 ожидает освобождения GPU", UiStatusKind.Warning),
            ["ALIGNING"] = new("ALIGNING", "Выравнивание", UiStatusKind.Processing),
            ["DIARIZING"] = new("DIARIZING", "Диаризация", UiStatusKind.Processing),
            ["SUMMARIZING"] = new("SUMMARIZING", "Стенограмма готова · формируется саммари", UiStatusKind.Processing),
            ["SUMMARY_NOT_READY"] = new("SUMMARY_NOT_READY", "Саммари ещё не готово", UiStatusKind.Warning),
            ["TRANSCRIPT_READY"] = new("TRANSCRIPT_READY", "Стенограмма готова", UiStatusKind.Success),
            ["READY"] = new("READY", "Готово", UiStatusKind.Success),
            ["PARTIAL_READY"] = new("PARTIAL_READY", "Готово с предупреждениями", UiStatusKind.Warning),
            ["DEGRADED"] = new("DEGRADED", "Ожидает восстановления сервиса", UiStatusKind.Warning),
            ["SUMMARY_REVIEW"] = new("SUMMARY_REVIEW", "Саммари готово · требуется проверка", UiStatusKind.Warning),
            ["NO_SPEECH_DETECTED"] = new("NO_SPEECH_DETECTED", "Речь не обнаружена", UiStatusKind.Warning),
            ["TRANSCRIPT_EMPTY"] = new("TRANSCRIPT_EMPTY", "Стенограмма пустая", UiStatusKind.Warning),
            ["TRANSCRIPT_INVALID_TIMECODE"] = new("TRANSCRIPT_INVALID_TIMECODE", "Некорректные таймкоды", UiStatusKind.Warning),
            ["ASR_INPUT_MISMATCH"] = new("ASR_INPUT_MISMATCH", "Canonical ASR-файл не совпадает", UiStatusKind.Warning),
            ["MEDIA_INVALID"] = new("MEDIA_INVALID", "Некорректный медиафайл", UiStatusKind.Error),
            ["AUDIO_PROCESSING_ERROR"] = new("AUDIO_PROCESSING_ERROR", "Ошибка обработки аудио", UiStatusKind.Error),
            ["GPU_PROCESSING_FAILED"] = new("GPU_PROCESSING_FAILED", "Ошибка обработки на GPU", UiStatusKind.Error),
            ["GPU_RESIDENT_LLM_CONFLICT"] = new("GPU_RESIDENT_LLM_CONFLICT", "GPU занята другой моделью", UiStatusKind.Warning),
            ["CUDA_OOM"] = new("CUDA_OOM", "Недостаточно памяти GPU", UiStatusKind.Error),
            ["WORKER_RESTART_RECOVERY"] = new("WORKER_RESTART_RECOVERY", "Worker перезапущен · задача возвращена в очередь", UiStatusKind.Warning),
            ["UPLOADED"] = new("UPLOADED", "Файл загружен", UiStatusKind.Success),
            ["VALIDATING"] = new("VALIDATING", "Проверка медиа", UiStatusKind.Processing),
            ["NORMALIZING"] = new("NORMALIZING", "Нормализация аудио", UiStatusKind.Processing),
            ["QUALITY_CHECK"] = new("QUALITY_CHECK", "Проверка качества", UiStatusKind.Processing),
            ["PERSISTING"] = new("PERSISTING", "Сохранение результата", UiStatusKind.Processing),
            ["PREPARING_CONTEXT"] = new("PREPARING_CONTEXT", "Подготовка контекста", UiStatusKind.Processing),
            ["EXTRACTING_FACTS"] = new("EXTRACTING_FACTS", "Извлечение фактов", UiStatusKind.Processing),
            ["VALIDATING_EVIDENCE"] = new("VALIDATING_EVIDENCE", "Проверка фактов", UiStatusKind.Processing),
            ["FAILED"] = new("FAILED", "Ошибка обработки", UiStatusKind.Error),
            ["CANCELLED"] = new("CANCELLED", "Отменено", UiStatusKind.Neutral),
            ["OFFLINE"] = new("OFFLINE", "Сервер недоступен", UiStatusKind.Warning),
            ["DISCONNECTED"] = new("DISCONNECTED", "Нет связи", UiStatusKind.Warning),
            ["REVOKED"] = new("REVOKED", "Отозван", UiStatusKind.Error),
            ["UNKNOWN"] = new("UNKNOWN", "Нет данных", UiStatusKind.Neutral),
            ["CONNECTED"] = new("CONNECTED", "Подключено", UiStatusKind.Success),
            ["NOT_CONFIGURED"] = new("NOT_CONFIGURED", "Не настроено", UiStatusKind.Warning),
            ["AUTH_REJECTED"] = new("AUTH_REJECTED", "Требуется переподключение", UiStatusKind.Warning),
            ["SERVER_NETWORK_UNREACHABLE"] = new("SERVER_NETWORK_UNREACHABLE", "Сервер недоступен по сети", UiStatusKind.Warning),
            ["SERVER_TIMEOUT"] = new("SERVER_TIMEOUT", "Сервер не ответил вовремя", UiStatusKind.Warning),
            ["SERVER_NOT_CONFIGURED"] = new("SERVER_NOT_CONFIGURED", "Сервер не настроен", UiStatusKind.Warning),
            ["UPLOAD_RETRY_PENDING"] = new("UPLOAD_RETRY_PENDING", "Ожидается повторная отправка", UiStatusKind.Warning),
            ["PROCESSING_UNAVAILABLE"] = new("PROCESSING_UNAVAILABLE", "Обработка WhisperX недоступна", UiStatusKind.Warning),
            ["RECORDER_HOST_INIT_FAILED"] = new("RECORDER_HOST_INIT_FAILED", "Recorder Host не завершил инициализацию", UiStatusKind.Error),
            ["SPOOL_READONLY"] = new("SPOOL_READONLY", "Локальное хранилище доступно только для чтения", UiStatusKind.Error),
            ["RECORDER_RUNTIME_LEASE_HELD"] = new("RECORDER_RUNTIME_LEASE_HELD", "Другой Recorder уже владеет хранилищем", UiStatusKind.Warning)
        });

    public static UiStatus Map(string? code)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? "UNKNOWN" : code.Trim();
        return Statuses.TryGetValue(normalized, out var status)
            ? status
            : new UiStatus(normalized.ToUpperInvariant(), "Состояние не определено", UiStatusKind.Neutral);
    }

    public static string Text(string? code) => Map(code).Text;

    public static bool IsQwenDisabled(DesktopProcessingReadiness? readiness)
    {
        return string.Equals(ComponentStatus(readiness, "qwen"), "DISABLED", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ComponentStatus(DesktopProcessingReadiness? readiness, string component)
    {
        if (readiness is null || readiness.Components.ValueKind != System.Text.Json.JsonValueKind.Object
            || !readiness.Components.TryGetProperty(component, out var value)
            || value.ValueKind != System.Text.Json.JsonValueKind.Object
            || !value.TryGetProperty("status", out var status)
            || status.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;
        return status.GetString()?.Trim().ToUpperInvariant();
    }

    public static string? ComponentReason(DesktopProcessingReadiness? readiness, string component)
    {
        if (readiness is null || readiness.Components.ValueKind != System.Text.Json.JsonValueKind.Object
            || !readiness.Components.TryGetProperty(component, out var value)
            || value.ValueKind != System.Text.Json.JsonValueKind.Object
            || !value.TryGetProperty("reason", out var reason)
            || reason.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;
        return reason.GetString()?.Trim().ToLowerInvariant();
    }

    public const string SummaryDisabledMessage =
        "Саммари отключено настройками сервера. Включите AUTO_SUMMARY_ENABLED и Summary Worker, чтобы пересборка стала доступна.";
}

public static class UiErrorFormatter
{
    public static string Format(Exception exception, string fallback = "Не удалось выполнить операцию.") => exception switch
    {
        DesktopApiException { StatusCode: 401 } => "Сеанс API истёк. Войдите повторно.",
        DesktopApiException { StatusCode: 403 } => "Недостаточно прав для выполнения операции.",
        DesktopApiException { StatusCode: 404 } => "Запрошенные данные не найдены.",
        DesktopApiException { ErrorCode: "UPLOAD_CONNECTION_LOST" } => "Соединение потеряно. Локальная запись сохранена, повторите отправку.",
        DesktopApiException { ErrorCode: "backend_timeout" } => "Сервер не ответил вовремя. Проверьте LAN-подключение и повторите попытку.",
        DesktopApiException { ErrorCode: "MEDIA_NO_AUDIO" } => "В выбранном файле не найден аудиосигнал.",
        DesktopApiException { ErrorCode: "MEDIA_INVALID" } => "Файл повреждён или имеет неподдерживаемый формат. Выберите другой файл.",
        DesktopApiException { ErrorCode: "AUDIO_PROCESSING_ERROR" } => "Не удалось подготовить аудио. Проверьте файл и повторите обработку.",
        DesktopApiException { ErrorCode: "MODEL_ACCESS_ERROR" } => "WhisperX не получил доступ к модели.",
        DesktopApiException { ErrorCode: "CUDA_UNAVAILABLE" } => "GPU недоступен. Проверьте CUDA или дождитесь CPU-обработки.",
        DesktopApiException { ErrorCode: "CUDA_OOM" } => "На GPU не хватило памяти. Закройте лишние GPU-процессы и повторите обработку.",
        DesktopApiException { ErrorCode: "GPU_RESIDENT_LLM_CONFLICT" } => "GPU занят LLM-сервисом. Остановите его перед транскрибацией WhisperX.",
        DesktopApiException { ErrorCode: "GPU_PROCESSING_FAILED" } => "WhisperX не завершил обработку на GPU. Повторите задачу или проверьте worker.",
        DesktopApiException { ErrorCode: "ASSISTANT_GPU_BUSY_TIMEOUT" } => "Мифодий слишком долго ждал GPU. Повторите вопрос после завершения транскрибации.",
        DesktopApiException { ErrorCode: "ASSISTANT_WAITING_FOR_GPU" } => "Мифодий ждёт освобождения GPU и продолжит обработку автоматически.",
        DesktopApiException { ErrorCode: "ASSISTANT_QUEUE_TIMEOUT" } => "Вопрос слишком долго ожидает обработки. Повторите его позже.",
        DesktopApiException { ErrorCode: "LOCAL_COMMAND_REQUIRED" } => "Нужна явная команда записи: «Мифодий, начни запись».",
        DesktopApiException { ErrorCode: "assistant_context_not_ready" } => "Для этого вопроса пока не готов подтверждённый контекст.",
        DesktopApiException { ErrorCode: "GROUNDING_REJECTED" } => "Ответ не прошёл проверку по источникам.",
        DesktopApiException { ErrorCode: "NO_EVIDENCE" } => "В источниках не найден подтверждённый ответ.",
        DesktopApiException { ErrorCode: "GPU_JOB_OWNERSHIP_CONFLICT" } => "ИИ-подсистема требует восстановления GPU Worker.",
        DesktopApiException { ErrorCode: "WORKER_RESTART_RECOVERY" } => "Worker был перезапущен. Задача возвращена в очередь; обновите статус через несколько секунд.",
        DesktopApiException { ErrorCode: "job_not_retryable" } => "Задача уже выполняется или завершена успешно; повторная обработка сейчас недоступна.",
        DesktopApiException { ErrorCode: "job_retry_conflict" } => "Состояние задачи изменилось. Обновите карточку совещания и повторите действие.",
        DesktopApiException { ErrorCode: "TRANSCRIPT_EMPTY" } => "Стенограмма не содержит распознанной речи.",
        DesktopApiException { ErrorCode: "NO_SPEECH_DETECTED" } => "Аудио сохранено, но речь не обнаружена. Проверьте выбранный микрофон и уровень сигнала.",
        DesktopApiException { ErrorCode: "ASR_INPUT_MISMATCH" } => "Версия V1 сохранена, но enrichment получил другой аудиофайл. Проверьте media asset и повторите enrichment.",
        DesktopApiException { ErrorCode: "RECORDING_SESSION_NOT_FOUND" } => "Серверная сессия этой локальной записи больше не существует. Локальный архив сохранён; автоматическая отправка остановлена.",
        DesktopApiException apiException when apiException.StatusCode >= 500 => "Сервер доступен, но не смог обработать запрос. Повторите попытку; если ошибка сохранится, проверьте журнал API.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "Сеанс API истёк. Войдите повторно.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "Недостаточно прав для выполнения операции.",
        HttpRequestException requestException when requestException.StatusCode.HasValue && (int)requestException.StatusCode.Value >= 500 => "Сервер доступен, но не смог обработать запрос. Повторите попытку; если ошибка сохранится, проверьте журнал API.",
        HttpRequestException => "Не удалось подключиться к API. Проверьте backend и соединение.",
        TimeoutException => "Сервис не ответил вовремя. Повторите попытку.",
        OperationCanceledException => "Операция отменена.",
        _ => fallback
    };
}
