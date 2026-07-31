using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WhisperX.Atom.Recorder;

public sealed record AgentCommandEnvelope(Guid Id, string CommandType, JsonElement Payload, long Cursor, string Status);

public sealed class AgentApiClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly Uri _baseUri;
    private readonly Guid _agentId;
    private readonly string _token;

    public AgentApiClient()
    {
        var baseUrl = (Environment.GetEnvironmentVariable("ATOM_AGENT_SERVER_URL") ?? "http://localhost:8000").TrimEnd('/') + "/";
        _baseUri = new Uri(baseUrl, UriKind.Absolute);
        Guid.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_ID"), out _agentId);
        _token = Environment.GetEnvironmentVariable("ATOM_AGENT_TOKEN") ?? string.Empty;
    }

    public bool IsConfigured => _agentId != Guid.Empty && !string.IsNullOrWhiteSpace(_token);

    public async Task<IReadOnlyList<AgentCommandEnvelope>> ReadCommandsAsync(long afterCursor, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return Array.Empty<AgentCommandEnvelope>();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, $"api/v1/agents/{_agentId}/commands/events?after={afterCursor}"));
        AddAuthentication(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var result = new List<AgentCommandEnvelope>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var json = line[6..];
            var command = JsonSerializer.Deserialize<AgentCommandEnvelope>(json);
            if (command is not null) result.Add(command);
        }
        return result;
    }

    public async Task<bool> CompleteCommandAsync(Guid commandId, string status, object result, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return false;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"api/v1/agents/{_agentId}/commands/{commandId}/result"));
        AddAuthentication(request);
        request.Content = JsonContent.Create(new { status, result });
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private void AddAuthentication(HttpRequestMessage request)
    {
        request.Headers.Add("X-Agent-Id", _agentId.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public void Dispose() => _http.Dispose();
}
