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
            var command = JsonSerializer.Deserialize<AgentCommandEnvelope>(line[6..]);
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

    public async Task<Guid> BindSessionAsync(string localSessionId, Guid meetingId, IReadOnlyList<RecordingTrackInfo> tracks, SpoolStore spool, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("Agent server credentials are not configured.");
        var existingServerSession = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
        var serverSessionId = existingServerSession ?? await CreateServerSessionAsync(meetingId, cancellationToken);
        foreach (var track in tracks)
        {
            var existingBinding = await spool.GetServerBindingAsync(localSessionId, track.TrackId, cancellationToken);
            if (existingBinding is not null) continue;
            var serverTrackId = await CreateServerTrackAsync(serverSessionId, track, cancellationToken);
            await spool.UpsertServerBindingAsync(new ServerBinding(localSessionId, track.TrackId, serverSessionId, serverTrackId), cancellationToken);
        }
        return serverSessionId;
    }

    public async Task<int> UploadPendingChunksAsync(SpoolStore spool, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return 0;
        var uploaded = 0;
        foreach (var chunk in await spool.PendingChunksAsync(50, cancellationToken))
        {
            var binding = await spool.GetServerBindingAsync(chunk.SessionId, chunk.TrackId, cancellationToken);
            if (binding is null || !File.Exists(chunk.LocalPath)) continue;
            await UploadChunkAsync(binding, chunk, cancellationToken);
            await spool.MarkConfirmedAsync(chunk.TrackId, chunk.Sequence, cancellationToken);
            try { File.Delete(chunk.LocalPath); } catch (IOException) { }
            uploaded++;
        }
        return uploaded;
    }

    public async Task FinalizeServerSessionAsync(Guid serverSessionId, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"api/v1/recording-sessions/{serverSessionId}/finalize"));
        AddAuthentication(request);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> CreateServerSessionAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/v1/recording-sessions"));
        AddAuthentication(request);
        request.Content = JsonContent.Create(new { meetingId });
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateServerTrackAsync(Guid serverSessionId, RecordingTrackInfo track, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"api/v1/recording-sessions/{serverSessionId}/tracks"));
        AddAuthentication(request);
        request.Content = JsonContent.Create(new { trackType = track.TrackType, deviceId = (string?)null, sampleRate = track.SampleRate, channels = track.Channels });
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task UploadChunkAsync(ServerBinding binding, RecordingChunk chunk, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_baseUri, $"api/v1/recording-sessions/{binding.ServerSessionId}/tracks/{binding.ServerTrackId}/chunks/{chunk.Sequence}"));
        AddAuthentication(request);
        request.Headers.Add("X-Chunk-SHA256", chunk.Sha256);
        request.Headers.Add("X-Start-Sample", chunk.StartSample.ToString());
        request.Headers.Add("X-Sample-Count", chunk.SampleCount.ToString());
        await using var stream = File.OpenRead(chunk.LocalPath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
        content.Headers.ContentLength = chunk.SizeBytes;
        request.Content = content;
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private void AddAuthentication(HttpRequestMessage request)
    {
        request.Headers.Add("X-Agent-Id", _agentId.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public void Dispose() => _http.Dispose();
}
