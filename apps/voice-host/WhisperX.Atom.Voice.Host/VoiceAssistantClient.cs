using System.Net.Http.Json;
using System.Text.Json;

namespace WhisperX.Atom.Voice.Host;

public sealed record VoiceAssistantResult(
    Guid QueryId,
    string Status,
    string? Answer,
    string? VoiceAnswer,
    string? ErrorCode,
    IReadOnlyList<Guid> Evidence);

/// <summary>
/// Local-only client for the asynchronous assistant API. The API is bound to
/// loopback in the desktop deployment and additionally requires a shared
/// VoiceHost token, so the SessionHost never uses the user's web cookie.
/// </summary>
internal sealed class VoiceAssistantClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _token;
    private readonly string _baseUrl;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public VoiceAssistantClient()
    {
        _baseUrl = (Environment.GetEnvironmentVariable("ATOM_API_URL") ?? "http://127.0.0.1:8080").TrimEnd('/');
        _token = Environment.GetEnvironmentVariable("ATOM_VOICE_HOST_TOKEN") ?? Environment.GetEnvironmentVariable("VOICE_HOST_TOKEN") ?? string.Empty;
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl + "/", UriKind.Absolute), Timeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrWhiteSpace(_token)) _http.DefaultRequestHeaders.Add("X-Voice-Host-Token", _token);
    }

    public async Task<VoiceAssistantResult> EnqueueAsync(string question, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token))
            return new VoiceAssistantResult(Guid.Empty, "FAILED", null, null, "VOICE_HOST_TOKEN_NOT_CONFIGURED", []);

        using var response = await _http.PostAsJsonAsync("api/assistant/queries", new { query = question, meetingId = (Guid?)null }, _json, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new VoiceAssistantResult(Guid.Empty, "FAILED", null, null, "ASSISTANT_API_" + (int)response.StatusCode, []);
        var accepted = await response.Content.ReadFromJsonAsync<AcceptedQuery>(_json, cancellationToken)
            ?? throw new InvalidOperationException("Assistant API returned an empty query.");
        return new VoiceAssistantResult(accepted.Id, accepted.Status ?? "QUEUED", null, "\u0417\u0430\u043f\u0440\u043e\u0441 \u043f\u0440\u0438\u043d\u044f\u0442, \u043e\u0442\u0432\u0435\u0447\u0443 \u043f\u043e\u0441\u043b\u0435 \u043e\u0431\u0440\u0430\u0431\u043e\u0442\u043a\u0438.", null, []);
    }

    public async Task<VoiceAssistantResult> WaitAsync(Guid id, CancellationToken cancellationToken)
    {
        // Interactive requests are durable. Polling is intentionally bounded;
        // the query remains in PostgreSQL and can be resumed after restart.
        for (var attempt = 0; attempt < 90; attempt++)
        {
            var current = await GetAsync(id, cancellationToken);
            if (current.Status is "READY" or "FAILED" or "NEEDS_REVIEW") return current;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        return new VoiceAssistantResult(id, "QUEUED", null, "\u0417\u0430\u043f\u0440\u043e\u0441 \u043f\u0440\u0438\u043d\u044f\u0442, \u043e\u0442\u0432\u0435\u0447\u0443 \u043f\u043e\u0441\u043b\u0435 \u043e\u0431\u0440\u0430\u0431\u043e\u0442\u043a\u0438.", null, []);
    }

    public async Task<VoiceAssistantResult> AskAsync(string question, CancellationToken cancellationToken)
    {
        var queued = await EnqueueAsync(question, cancellationToken);
        return queued.Status == "FAILED" ? queued : await WaitAsync(queued.QueryId, cancellationToken);
    }

    public async Task<VoiceAssistantResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"api/assistant/queries/{id}", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new VoiceAssistantResult(id, "FAILED", null, null, "ASSISTANT_API_" + (int)response.StatusCode, []);
        var result = await response.Content.ReadFromJsonAsync<AssistantQueryDto>(_json, cancellationToken)
            ?? throw new InvalidOperationException("Assistant API returned an empty result.");
        return new VoiceAssistantResult(result.Id, result.Status ?? "QUEUED", result.Answer, result.VoiceAnswer, result.ErrorCode, ParseEvidence(result.Evidence));
    }

    private static IReadOnlyList<Guid> ParseEvidence(JsonElement? evidence)
    {
        if (evidence is not JsonElement value || value.ValueKind != JsonValueKind.Array) return [];
        var ids = new List<Guid>();
        foreach (var item in value.EnumerateArray())
        {
            var raw = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.TryGetProperty("segmentId", out var segment) ? segment.GetString() : null;
            if (Guid.TryParse(raw, out var id)) ids.Add(id);
        }
        return ids;
    }

    public void Dispose() => _http.Dispose();

    private sealed record AcceptedQuery(Guid Id, string? Status);
    private sealed record AssistantQueryDto(Guid Id, string? Status, string? Answer, string? VoiceAnswer, string? ErrorCode, JsonElement? Evidence);
}
