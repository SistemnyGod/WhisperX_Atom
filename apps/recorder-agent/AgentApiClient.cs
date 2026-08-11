using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WhisperX.Atom.Recorder;

public sealed record AgentCommandEnvelope(Guid Id, string CommandType, JsonElement Payload, long Cursor, string Status);

public sealed class AgentApiClient : IDisposable
{
    private readonly HttpClient _http = new();
    private Uri _baseUri;
    private Guid _agentId;
    private string _token;
    private readonly string _configPath;
    private readonly object _configurationGate = new();
    private readonly AgentStorageSettings _storage;

    public AgentApiClient(AgentStorageSettings storage)
    {
        _storage = storage;
        _configPath = Environment.GetEnvironmentVariable("ATOM_AGENT_CONFIG_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent", "agent-config.json");
        var config = ReadConfig(_configPath);
        var configuredArchiveRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_ARCHIVE_ROOT") ?? config?.ArchiveRoot;
        if (!string.IsNullOrWhiteSpace(configuredArchiveRoot))
        {
            try { _storage.SetArchiveRoot(configuredArchiveRoot); }
            catch (Exception) { /* Invalid user path is reported when a recording is started. */ }
        }
        _storage.SetAudioDevices(
            Environment.GetEnvironmentVariable("ATOM_AGENT_MICROPHONE_DEVICE_ID") ?? config?.MicrophoneDeviceId,
            Environment.GetEnvironmentVariable("ATOM_AGENT_SYSTEM_AUDIO_DEVICE_ID") ?? config?.SystemAudioDeviceId);
        var baseUrl = (Environment.GetEnvironmentVariable("ATOM_AGENT_SERVER_URL") ?? config?.ServerUrl ?? "http://localhost:8080").TrimEnd('/') + "/";
        _baseUri = new Uri(baseUrl, UriKind.Absolute);
        Guid.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_ID") ?? config?.AgentId, out _agentId);
        _token = Environment.GetEnvironmentVariable("ATOM_AGENT_TOKEN") ?? config?.Token ?? string.Empty;
    }

    public bool IsConfigured => _agentId != Guid.Empty && !string.IsNullOrWhiteSpace(_token);

    public async Task ConfigureAsync(string serverUrl, Guid agentId, string token, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(serverUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(token) || agentId == Guid.Empty)
            throw new InvalidOperationException("agent_configuration_invalid");
        lock (_configurationGate)
        {
            _baseUri = uri;
            _agentId = agentId;
            _token = token;
        }
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);
        var temporary = _configPath + ".part";
        await PersistConfigurationAsync(temporary, new AgentConfiguration(uri.ToString().TrimEnd('/'), agentId.ToString(), ProtectToken(token), true,
            _storage.ArchiveRoot, _storage.MicrophoneDeviceId, _storage.SystemAudioDeviceId), cancellationToken);
        File.Move(temporary, _configPath, true);
    }

    public async Task SetArchiveRootAsync(string archiveRoot, CancellationToken cancellationToken = default)
    {
        var normalized = _storage.SetArchiveRoot(archiveRoot);
        if (!IsConfigured) return;
        var temporary = _configPath + ".part";
        var configuration = new AgentConfiguration(
            _baseUri.ToString().TrimEnd('/'),
            _agentId.ToString(),
            ProtectToken(_token),
            true,
            normalized,
            _storage.MicrophoneDeviceId,
            _storage.SystemAudioDeviceId);
        await PersistConfigurationAsync(temporary, configuration, cancellationToken);
        File.Move(temporary, _configPath, true);
    }

    public async Task SetAudioDevicesAsync(string? microphoneDeviceId, string? systemAudioDeviceId, CancellationToken cancellationToken = default)
    {
        _storage.SetAudioDevices(microphoneDeviceId, systemAudioDeviceId);
        if (!IsConfigured) return;
        var temporary = _configPath + ".part";
        var configuration = new AgentConfiguration(
            _baseUri.ToString().TrimEnd('/'),
            _agentId.ToString(),
            ProtectToken(_token),
            true,
            _storage.ArchiveRoot,
            _storage.MicrophoneDeviceId,
            _storage.SystemAudioDeviceId);
        await PersistConfigurationAsync(temporary, configuration, cancellationToken);
        File.Move(temporary, _configPath, true);
    }

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

    public async Task<bool> HeartbeatAsync(DeviceHealthSnapshot health, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return false;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"api/v1/agents/{_agentId}/heartbeat"));
        AddAuthentication(request);
        var version = Environment.GetEnvironmentVariable("ATOM_AGENT_VERSION") ?? "0.1.0";
        request.Content = JsonContent.Create(new
        {
            status = "ONLINE",
            version,
            capabilities = new
            {
                microphone = health.Microphone,
                systemAudio = health.SystemAudio,
                manifest = true,
                spool = true,
                deviceHealth = health
            }
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
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

    public async Task<Guid> BindSessionAsync(string localSessionId, Guid? meetingId, string? title, IReadOnlyList<RecordingTrackInfo> tracks, SpoolStore spool, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("Agent server credentials are not configured.");
        var existingServerSession = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
        var created = existingServerSession is null ? await CreateServerSessionAsync(meetingId, title, cancellationToken) : (existingServerSession.Value, meetingId ?? await spool.GetMeetingIdAsync(localSessionId, cancellationToken) ?? Guid.Empty);
        var serverSessionId = created.Item1;
        if (created.Item2 != Guid.Empty) await spool.SetMeetingIdAsync(localSessionId, created.Item2, cancellationToken);
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
        var pending = await spool.PendingChunksAsync(200, cancellationToken);
        var configuredConcurrency = int.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_UPLOAD_CONCURRENCY"), out var parsedConcurrency)
            ? parsedConcurrency
            : 3;
        using var gate = new SemaphoreSlim(Math.Clamp(configuredConcurrency, 1, 4));
        var uploads = pending.Select(async chunk =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var binding = await spool.GetServerBindingAsync(chunk.SessionId, chunk.TrackId, cancellationToken);
                if (binding is null || !File.Exists(chunk.LocalPath)) return false;
                await UploadChunkAsync(binding, chunk, cancellationToken);
                await spool.MarkConfirmedAsync(chunk.TrackId, chunk.Sequence, cancellationToken);
                return true;
            }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(uploads)).Count(uploaded => uploaded);
    }

    public async Task<int> UploadPendingEventsAsync(SpoolStore spool, string localSessionId, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return 0;
        var serverSessionId = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
        if (serverSessionId is not Guid session) return 0;
        var events = await spool.PendingEventsAsync(localSessionId, 200, cancellationToken);
        if (events.Count == 0) return 0;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"api/v1/recording-sessions/{session}/events/batch"));
        AddAuthentication(request);
        request.Content = JsonContent.Create(new
        {
            events = events.Select(item => new
            {
                id = Guid.TryParse(item.Id, out var id) ? id : Guid.NewGuid(),
                eventType = item.EventType,
                mediaTimeMs = item.MediaTimeMs,
                payload = JsonDocument.Parse(string.IsNullOrWhiteSpace(item.PayloadJson) ? "{}" : item.PayloadJson),
                createdAt = item.CreatedAt
            })
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await spool.MarkEventsSyncedAsync(events.Select(item => item.Id), cancellationToken);
        return events.Count;
    }
    public async Task<bool> FinalizeServerSessionAsync(Guid serverSessionId, string localSessionId, SpoolStore spool, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return false;
        var manifest = await spool.BuildManifestAsync(localSessionId, cancellationToken);
        if (manifest is null || manifest.ServerSessionId != serverSessionId) return false;
        foreach (var localTrack in await spool.GetTrackInfosAsync(localSessionId, cancellationToken))
            if (await spool.GetServerBindingAsync(localSessionId, localTrack.TrackId, cancellationToken) is null)
                return false;
        if (!await ReconcileMissingChunksAsync(serverSessionId, localSessionId, manifest, spool, cancellationToken))
            return false;

        using var response = await SendWithRetryAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"api/v1/recording-sessions/{serverSessionId}/finalize"));
            AddAuthentication(request);
            request.Content = JsonContent.Create(new
            {
                manifest = new
                {
                    session_id = serverSessionId,
                    tracks = manifest.Tracks.Select(track => new
                    {
                        track_id = track.ServerTrackId,
                        track_type = track.TrackType,
                        sample_rate = track.SampleRate,
                        channels = track.Channels,
                        expected_chunk_count = track.ExpectedChunkCount,
                        start_sample = track.StartSample,
                        total_samples = track.TotalSamples
                    })
                }
            });
            return await _http.SendAsync(request, cancellationToken);
        }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<(Guid SessionId, Guid MeetingId)> CreateServerSessionAsync(Guid? meetingId, string? title, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/v1/recording-sessions"));
        AddAuthentication(request);
        request.Content = JsonContent.Create(new { meetingId, title, startedAt = DateTimeOffset.UtcNow });
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return (document.RootElement.GetProperty("id").GetGuid(), document.RootElement.GetProperty("meetingId").GetGuid());
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
        using var response = await SendWithRetryAsync(async () =>
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
            return await _http.SendAsync(request, cancellationToken);
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<bool> ReconcileMissingChunksAsync(Guid serverSessionId, string localSessionId, RecordingManifest manifest, SpoolStore spool, CancellationToken cancellationToken)
    {
        foreach (var track in manifest.Tracks)
        {
            var missing = await ReadMissingChunksAsync(serverSessionId, track.ServerTrackId, track.ExpectedChunkCount, cancellationToken);
            if (missing is null || missing.Count == 0) continue;

            var localTrackId = await spool.GetLocalTrackIdAsync(localSessionId, track.ServerTrackId, cancellationToken);
            if (localTrackId is null) return false;
            var localChunks = await spool.GetChunksAsync(localSessionId, localTrackId, missing, cancellationToken);
            var bySequence = localChunks.ToDictionary(chunk => chunk.Sequence);
            var binding = new ServerBinding(localSessionId, localTrackId, serverSessionId, track.ServerTrackId);
            foreach (var sequence in missing)
            {
                if (!bySequence.TryGetValue(sequence, out var chunk) || !File.Exists(chunk.LocalPath)) return false;
                await UploadChunkAsync(binding, chunk, cancellationToken);
                await spool.MarkConfirmedAsync(chunk.TrackId, chunk.Sequence, cancellationToken);
            }
        }
        return true;
    }

    private async Task<IReadOnlyList<int>?> ReadMissingChunksAsync(Guid serverSessionId, Guid serverTrackId, int expectedCount, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, $"api/v1/recording-sessions/{serverSessionId}/tracks/{serverTrackId}/missing-chunks?expectedCount={Math.Clamp(expectedCount, 0, 100_000)}"));
            AddAuthentication(request);
            return await _http.SendAsync(request, cancellationToken);
        }, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("missing", out var missing) || missing.ValueKind != JsonValueKind.Array)
            return Array.Empty<int>();
        return missing.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _)).Select(item => item.GetInt32()).Where(sequence => sequence >= 0).Distinct().OrderBy(sequence => sequence).ToArray();
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var response = await send();
                if (!IsTransient(response.StatusCode)) return response;
                lastError = new HttpRequestException($"Transient recorder API response: {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }

            if (attempt == 2) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
        }
        throw lastError ?? new HttpRequestException("Recorder API request failed.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private void AddAuthentication(HttpRequestMessage request)
    {
        request.Headers.Add("X-Agent-Id", _agentId.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    private static AgentConfiguration? ReadConfig(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var config = JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(path));
            if (config is null || !config.Encrypted) return config;
            var token = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(config.Token),
                Encoding.UTF8.GetBytes("WhisperXAtom.AgentToken.v1"),
                DataProtectionScope.LocalMachine));
            return config with { Token = token };
        }
        catch
        {
            return null;
        }
    }

    private sealed record AgentConfiguration(
        string ServerUrl,
        string AgentId,
        string Token,
        bool Encrypted = false,
        string? ArchiveRoot = null,
        string? MicrophoneDeviceId = null,
        string? SystemAudioDeviceId = null);

    private static string ProtectToken(string token) => Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(token),
        Encoding.UTF8.GetBytes("WhisperXAtom.AgentToken.v1"),
        DataProtectionScope.LocalMachine));

    private static Task PersistConfigurationAsync(string path, AgentConfiguration configuration, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration), cancellationToken);

    public void Dispose() => _http.Dispose();
}
