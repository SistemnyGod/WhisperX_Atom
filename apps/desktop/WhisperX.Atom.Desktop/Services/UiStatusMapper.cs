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
            ["RECORDING"] = new("RECORDING", "Идёт запись", UiStatusKind.Processing),
            ["CHECKING"] = new("CHECKING", "Проверка устройств", UiStatusKind.Processing),
            ["PREPARING"] = new("PREPARING", "Подготовка", UiStatusKind.Processing),
            ["UPLOADING"] = new("UPLOADING", "Синхронизация", UiStatusKind.Processing),
            ["MEDIA_PROCESSING"] = new("MEDIA_PROCESSING", "Подготовка медиа", UiStatusKind.Processing),
            ["TRANSCRIBING"] = new("TRANSCRIBING", "Транскрибация", UiStatusKind.Processing),
            ["ALIGNING"] = new("ALIGNING", "Выравнивание", UiStatusKind.Processing),
            ["DIARIZING"] = new("DIARIZING", "Диаризация", UiStatusKind.Processing),
            ["SUMMARIZING"] = new("SUMMARIZING", "Формируется саммари", UiStatusKind.Processing),
            ["TRANSCRIPT_READY"] = new("TRANSCRIPT_READY", "Стенограмма готова", UiStatusKind.Success),
            ["READY"] = new("READY", "Готово", UiStatusKind.Success),
            ["PARTIAL_READY"] = new("PARTIAL_READY", "Готово с предупреждениями", UiStatusKind.Warning),
            ["FAILED"] = new("FAILED", "Ошибка обработки", UiStatusKind.Error),
            ["CANCELLED"] = new("CANCELLED", "Отменено", UiStatusKind.Neutral),
            ["OFFLINE"] = new("OFFLINE", "Сервер недоступен", UiStatusKind.Warning),
            ["CONNECTED"] = new("CONNECTED", "Подключено", UiStatusKind.Success),
            ["NOT_CONFIGURED"] = new("NOT_CONFIGURED", "Не настроено", UiStatusKind.Warning),
            ["AUTH_REJECTED"] = new("AUTH_REJECTED", "Требуется переподключение", UiStatusKind.Warning)
        });

    public static UiStatus Map(string? code)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? "UNKNOWN" : code.Trim();
        return Statuses.TryGetValue(normalized, out var status)
            ? status
            : new UiStatus(normalized.ToUpperInvariant(), "Состояние не определено", UiStatusKind.Neutral);
    }

    public static string Text(string? code) => Map(code).Text;
}

public static class UiErrorFormatter
{
    public static string Format(Exception exception, string fallback = "Не удалось выполнить операцию.") => exception switch
    {
        DesktopApiException { StatusCode: 401 } => "Сеанс API истёк. Войдите повторно.",
        DesktopApiException { StatusCode: 403 } => "Недостаточно прав для выполнения операции.",
        DesktopApiException { StatusCode: 404 } => "Запрошенные данные не найдены.",
        DesktopApiException { ErrorCode: "UPLOAD_CONNECTION_LOST" } => "Соединение потеряно. Локальная запись сохранена, повторите отправку.",
        DesktopApiException { ErrorCode: "MEDIA_NO_AUDIO" } => "В выбранном файле не найден аудиосигнал.",
        DesktopApiException { ErrorCode: "MODEL_ACCESS_ERROR" } => "WhisperX не получил доступ к модели.",
        DesktopApiException { ErrorCode: "CUDA_UNAVAILABLE" } => "GPU недоступен. Проверьте CUDA или дождитесь CPU-обработки.",
        DesktopApiException { ErrorCode: "TRANSCRIPT_EMPTY" } => "Стенограмма не содержит распознанной речи.",
        HttpRequestException => "Не удалось подключиться к API. Проверьте backend и соединение.",
        TimeoutException => "Сервис не ответил вовремя. Повторите попытку.",
        OperationCanceledException => "Операция отменена.",
        _ => fallback
    };
}
