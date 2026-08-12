using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WhisperX.Atom.Recorder;

public sealed record AgentCommandEnvelope(Guid Id, string CommandType, JsonElement Payload, long Cursor, string Status);
public sealed record ServerFinalizeReceipt(
    bool Accepted,
    Guid ServerSessionId,
    Guid? MeetingId,
    Guid? JobId,
    Guid? MediaAssetId,
    string? TraceId,
    string? ErrorCode = null,
    bool Retryable = true,
    IReadOnlyList<int>? MissingChunks = null);
public sealed record ServerMediaStatus(
    bool Ready,
    bool TerminalFailure,
    string? MediaStatus,
    string? JobStatus,
    string? JobStage,
    string? ErrorCode = null);

public sealed class AgentApiClient : IDisposable
{
    private const string FallbackLanServerUrl = "http://192.168.2.194:8080";
    private readonly HttpClient _http = new();
    private Uri _baseUri;
    private Guid _agentId;
    private Guid _installationId;
    private string _token;
    private string _serverConnectionState = "NOT_CONFIGURED";
    private DateTimeOffset? _lastHeartbeatAtUtc;
    private string? _lastServerError;
    private int _heartbeatFailures;
    private DateTimeOffset _nextHeartbeatAtUtc = DateTimeOffset.UtcNow;
    private readonly string _configPath;
    private readonly object _configurationGate = new();
    private readonly SemaphoreSlim _bindingGate = new(1, 1);
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
        var baseUrl = ResolveServerUrl(config);
        _baseUri = new Uri(baseUrl, UriKind.Absolute);
        Guid.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_ID") ?? config?.AgentId, out _agentId);
        Guid.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_INSTALLATION_ID") ?? config?.InstallationId.ToString(), out _installationId);
        if (_installationId == Guid.Empty) _installationId = Guid.NewGuid();
        _token = Environment.GetEnvironmentVariable("ATOM_AGENT_TOKEN") ?? config?.Token ?? string.Empty;
        if (config is not null && !string.Equals(config.ServerUrl.TrimEnd('/'), _baseUri.ToString().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            TryPersistMigratedConfiguration(config, _baseUri);
        _serverConnectionState = IsConfigured ? "UNKNOWN" : "NOT_CONFIGURED";
    }

    public bool IsConfigured => _agentId != Guid.Empty && !string.IsNullOrWhiteSpace(_token);
    public Guid InstallationId => _installationId;
    public Guid? AgentId => _agentId == Guid.Empty ? null : _agentId;
    public string ServerConnectionState => _serverConnectionState;
    public DateTimeOffset? LastHeartbeatAtUtc => _lastHeartbeatAtUtc;
    public string? LastServerError => _lastServerError;
    public DateTimeOffset NextHeartbeatAtUtc => _nextHeartbeatAtUtc;

    public async Task ConfigureAsync(string serverUrl, Guid agentId, string token, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(serverUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(token) || agentId == Guid.Empty)
            throw new InvalidOperationException("agent_configuration_invalid");
        lock (_configurationGate)
        {
            _baseUri = uri;
            _agentId = agentId;
            _token = token;
            _serverConnectionState = "UNKNOWN";
            _lastServerError = null;
            _nextHeartbeatAtUtc = DateTimeOffset.UtcNow;
        }
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);
        var temporary = _configPath + ".part";
        await PersistConfigurationAsync(temporary, new AgentConfiguration(uri.ToString().TrimEnd('/'), agentId.ToString(), ProtectToken(token), true,
            _installationId,
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
            _installationId,
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
            _installationId,
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
        if (!IsConfigured)
        {
            _serverConnectionState = "NOT_CONFIGURED";
            return false;
        }
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
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _serverConnectionState = "CONNECTED";
                _lastHeartbeatAtUtc = DateTimeOffset.UtcNow;
                _lastServerError = null;
                _heartbeatFailures = 0;
                _nextHeartbeatAtUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                return true;
            }

            _lastServerError = response.StatusCode == HttpStatusCode.Unauthorized ? "agent_authentication_required" : $"http_{(int)response.StatusCode}";
            _serverConnectionState = response.StatusCode == HttpStatusCode.Unauthorized ? "AUTH_REJECTED" : "SERVER_ERROR";
            ScheduleHeartbeatRetry();
            return false;
        }
        catch (HttpRequestException ex)
        {
            _lastServerError = ex.GetType().Name;
            _serverConnectionState = "SERVER_UNAVAILABLE";
            ScheduleHeartbeatRetry();
            return false;
        }
    }

    private void ScheduleHeartbeatRetry()
    {
        _heartbeatFailures = Math.Min(_heartbeatFailures + 1, 8);
        var baseSeconds = Math.Min(300, 30 * Math.Pow(2, _heartbeatFailures - 1));
        var jitter = Random.Shared.NextDouble() * Math.Max(1, baseSeconds * 0.2);
        _nextHeartbeatAtUtc = DateTimeOffset.UtcNow.AddSeconds(baseSeconds + jitter);
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
        await _bindingGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsConfigured) throw new InvalidOperationException("Agent server credentials are not configured.");
            var existingServerSession = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
            var correlationId = (await spool.GetSessionInfoAsync(localSessionId, cancellationToken))?.PipelineCorrelationId;
            var created = existingServerSession is null ? await CreateServerSessionAsync(meetingId, title, localSessionId, correlationId, cancellationToken) : (existingServerSession.Value, meetingId ?? await spool.GetMeetingIdAsync(localSessionId, cancellationToken) ?? Guid.Empty);
            var serverSessionId = created.Item1;
            if (created.Item2 != Guid.Empty) await spool.SetMeetingIdAsync(localSessionId, created.Item2, cancellationToken);
            foreach (var track in tracks)
            {
                var existingBinding = await spool.GetServerBindingAsync(localSessionId, track.TrackId, cancellationToken);
                if (existingBinding is not null) continue;
                var serverTrackId = await CreateServerTrackAsync(serverSessionId, track, correlationId, cancellationToken);
                await spool.UpsertServerBindingAsync(new ServerBinding(localSessionId, track.TrackId, serverSessionId, serverTrackId), cancellationToken);
            }
            return serverSessionId;
        }
        finally
        {
            _bindingGate.Release();
        }
    }

    public async Task<int> UploadPendingChunksAsync(SpoolStore spool, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return 0;
        var pending = await spool.PendingChunksAsync(200, cancellationToken);
        var configuredConcurrency = int.TryParse(Environment.GetEnvironmentVariable("ATOM_AGENT_UPLOAD_CONCURRENCY"), out var parsedConcurrency)
            ? parsedConcurrency
            : 3;
        using var gate = new SemaphoreSlim(Math.Clamp(configuredConcurrency, 1, 4));
        var confirmed = 0;
        var uploads = pending.Select(async chunk =>
        {
            await gate.WaitAsync(cancellationToken);
            var claimed = false;
            try
            {
                var binding = await spool.GetServerBindingAsync(chunk.SessionId, chunk.TrackId, cancellationToken);
                if (binding is null) return;
                if (!File.Exists(chunk.LocalPath))
                {
                    await spool.MarkUploadFailedAsync(chunk, "LOCAL_CHUNK_MISSING", cancellationToken);
                    return;
                }
                if (!await spool.TryBeginChunkUploadAsync(chunk, cancellationToken)) return;
                claimed = true;
                try
                {
                    var correlationId = (await spool.GetSessionInfoAsync(chunk.SessionId, cancellationToken))?.PipelineCorrelationId;
                    await UploadChunkAsync(binding, chunk, correlationId, cancellationToken);
                    await spool.MarkConfirmedAsync(chunk.TrackId, chunk.Sequence, cancellationToken);
                    Interlocked.Increment(ref confirmed);
                }
                catch (Exception ex)
                {
                    await spool.MarkUploadFailedAsync(chunk, ClassifyChunkUploadError(ex), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (claimed)
                {
                    try { await spool.MarkUploadFailedAsync(chunk, ClassifyChunkUploadError(ex), cancellationToken); }
                    catch (Exception stateError) { _ = stateError; }
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(uploads);
        return confirmed;
    }

    private static string ClassifyChunkUploadError(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            return "AGENT_AUTH_REJECTED";
        if (exception is FileNotFoundException or DirectoryNotFoundException)
            return "LOCAL_CHUNK_MISSING";
        return "CHUNK_UPLOAD_FAILED";
    }

    public async Task<int> UploadPendingEventsAsync(SpoolStore spool, string localSessionId, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return 0;
        var serverSessionId = await spool.GetServerSessionIdAsync(localSessionId, cancellationToken);
        if (serverSessionId is not Guid session) return 0;
        var events = await spool.PendingEventsAsync(localSessionId, 200, cancellationToken);
        if (events.Count == 0) return 0;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"api/v1/recording-sessions/{session}/events/batch"));
        AddAuthentication(request, (await spool.GetSessionInfoAsync(localSessionId, cancellationToken))?.PipelineCorrelationId);
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
    public async Task<ServerFinalizeReceipt> FinalizeServerSessionAsync(Guid serverSessionId, string localSessionId, SpoolStore spool, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return new ServerFinalizeReceipt(false, serverSessionId, null, null, null, null, "SERVER_UNAVAILABLE");
        var manifest = await spool.BuildManifestAsync(localSessionId, cancellationToken);
        if (manifest is null || manifest.ServerSessionId != serverSessionId) return new ServerFinalizeReceipt(false, serverSessionId, null, null, null, null, "LOCAL_MANIFEST_MISSING", false);
        foreach (var localTrack in await spool.GetTrackInfosAsync(localSessionId, cancellationToken))
            if (await spool.GetServerBindingAsync(localSessionId, localTrack.TrackId, cancellationToken) is null)
                return new ServerFinalizeReceipt(false, serverSessionId, null, null, null, null, "SERVER_BINDING_FAILED", true);
        if (!await ReconcileMissingChunksAsync(serverSessionId, localSessionId, manifest, spool, cancellationToken))
            return new ServerFinalizeReceipt(false, serverSessionId, null, null, null, null, "SERVER_CHUNKS_MISSING", true);

        using var response = await SendWithRetryAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"api/v1/recording-sessions/{serverSessionId}/finalize"));
            AddAuthentication(request, (await spool.GetSessionInfoAsync(localSessionId, cancellationToken))?.PipelineCorrelationId);
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
        var traceId = response.Headers.TryGetValues("X-Trace-Id", out var traceValues) ? traceValues.FirstOrDefault() : null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            IReadOnlyList<int>? missing = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var errorElement)) error = errorElement.GetString();
                if (document.RootElement.TryGetProperty("missing", out var missingElement) && missingElement.ValueKind == JsonValueKind.Array)
                    missing = missingElement.EnumerateArray().SelectMany(item =>
                    {
                        // The API uses camelCase `sequences`; accept the older
                        // `missingSequences` spelling while rolling agents forward.
                        if (!item.TryGetProperty("sequences", out var sequences)) item.TryGetProperty("missingSequences", out sequences);
                        return sequences.ValueKind == JsonValueKind.Array
                            ? sequences.EnumerateArray().Where(value => value.TryGetInt32(out _)).Select(value => value.GetInt32())
                            : Enumerable.Empty<int>();
                    }).Distinct().OrderBy(value => value).ToArray();
            }
            catch (JsonException) { }
            var code = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "AGENT_AUTH_REJECTED" : error ?? "SERVER_FINALIZE_REJECTED";
            return new ServerFinalizeReceipt(false, serverSessionId, null, null, null, traceId, code, IsRetryableFinalizeError(code, response.StatusCode), missing);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var meetingId = root.TryGetProperty("meetingId", out var meeting) && meeting.TryGetGuid(out var parsedMeetingId) ? parsedMeetingId : (Guid?)null;
            var jobId = root.TryGetProperty("jobId", out var job) && job.TryGetGuid(out var parsedJobId) ? parsedJobId : (Guid?)null;
            var mediaAssetId = root.TryGetProperty("mediaAssetId", out var asset) && asset.TryGetGuid(out var parsedAssetId) ? parsedAssetId : (Guid?)null;
            await spool.SetServerReceiptAsync(localSessionId, meetingId, mediaAssetId, jobId, traceId, cancellationToken);
            return new ServerFinalizeReceipt(true, serverSessionId, meetingId, jobId, mediaAssetId, traceId);
        }
        catch (JsonException)
        {
            return new ServerFinalizeReceipt(false, serverSessionId, null, null, null, traceId, "SERVER_FINALIZE_REJECTED", true);
        }
    }

    public async Task<ServerMediaStatus> GetServerMediaStatusAsync(Guid serverSessionId, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return new ServerMediaStatus(false, false, null, null, null, "SERVER_UNAVAILABLE");
        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, $"api/v1/recording-sessions/{serverSessionId}/status"));
                AddAuthentication(request);
                return _http.SendAsync(request, cancellationToken);
            }, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new ServerMediaStatus(false, false, null, null, null, "SERVER_UNAVAILABLE");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var terminal = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                return new ServerMediaStatus(false, terminal, null, null, null, terminal ? "AGENT_AUTH_REJECTED" : "SERVER_UNAVAILABLE");
            }

            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = document.RootElement;
                var mediaStatus = root.TryGetProperty("mediaStatus", out var media) && media.ValueKind == JsonValueKind.String ? media.GetString() : null;
                var jobStatus = root.TryGetProperty("jobStatus", out var job) && job.ValueKind == JsonValueKind.String ? job.GetString() : null;
                var jobStage = root.TryGetProperty("jobStage", out var stage) && stage.ValueKind == JsonValueKind.String ? stage.GetString() : null;
                var terminal = string.Equals(mediaStatus, "FAILED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mediaStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(jobStatus, "FAILED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(jobStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase);
                return new ServerMediaStatus(
                    string.Equals(mediaStatus, "READY", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(jobStatus, "FAILED", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(jobStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase),
                    terminal,
                    mediaStatus,
                    jobStatus,
                    jobStage,
                    terminal ? "SERVER_ASSEMBLY_FAILED" : null);
            }
            catch (JsonException)
            {
                return new ServerMediaStatus(false, false, null, null, null, "SERVER_UNAVAILABLE");
            }
        }
    }

    public async Task<bool> IsServerMediaReadyAsync(Guid serverSessionId, CancellationToken cancellationToken)
        => (await GetServerMediaStatusAsync(serverSessionId, cancellationToken)).Ready;

    private async Task<(Guid SessionId, Guid MeetingId)> CreateServerSessionAsync(Guid? meetingId, string? title, string localSessionId, string? pipelineCorrelationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/v1/recording-sessions"));
        AddAuthentication(request, pipelineCorrelationId);
        request.Content = JsonContent.Create(new { meetingId, title, startedAt = DateTimeOffset.UtcNow, localSessionId, pipelineCorrelationId });
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return (document.RootElement.GetProperty("id").GetGuid(), document.RootElement.GetProperty("meetingId").GetGuid());
    }

    private async Task<Guid> CreateServerTrackAsync(Guid serverSessionId, RecordingTrackInfo track, string? pipelineCorrelationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"api/v1/recording-sessions/{serverSessionId}/tracks"));
        AddAuthentication(request, pipelineCorrelationId);
        request.Content = JsonContent.Create(new
        {
            trackType = track.TrackType,
            deviceId = track.EndpointId,
            deviceName = track.DeviceFriendlyName,
            selectionMode = track.SelectionMode,
            recordingProfile = track.Profile,
            sampleRate = track.SampleRate,
            channels = track.Channels,
            encoding = track.Encoding,
            bitsPerSample = track.BitsPerSample
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task UploadChunkAsync(ServerBinding binding, RecordingChunk chunk, string? pipelineCorrelationId, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_baseUri, $"api/v1/recording-sessions/{binding.ServerSessionId}/tracks/{binding.ServerTrackId}/chunks/{chunk.Sequence}"));
            AddAuthentication(request, pipelineCorrelationId);
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
                var correlationId = (await spool.GetSessionInfoAsync(localSessionId, cancellationToken))?.PipelineCorrelationId;
                await UploadChunkAsync(binding, chunk, correlationId, cancellationToken);
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

    private static bool IsRetryableFinalizeError(string code, HttpStatusCode statusCode) =>
        code is "recording_chunks_incomplete" or "SERVER_CHUNKS_MISSING" or "SERVER_UNAVAILABLE" or "SERVER_FINALIZE_REJECTED"
        || statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static string ResolveServerUrl(AgentConfiguration? config)
    {
        var explicitUrl = Environment.GetEnvironmentVariable("ATOM_AGENT_SERVER_URL")?.Trim();
        if (IsHttpUrl(explicitUrl)) return explicitUrl!.TrimEnd('/') + "/";

        var configuredUrl = config?.ServerUrl?.Trim();
        if (IsHttpUrl(configuredUrl) && !IsLoopbackUrl(configuredUrl)) return configuredUrl!.TrimEnd('/') + "/";

        var runtimeUrl = Environment.GetEnvironmentVariable("WHISPERX_API_URL")?.Trim();
        if (IsHttpUrl(runtimeUrl)) return runtimeUrl!.TrimEnd('/') + "/";

        return FallbackLanServerUrl + "/";
    }

    private void TryPersistMigratedConfiguration(AgentConfiguration config, Uri serverUri)
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(directory);
            var temporary = _configPath + ".part";
            var migrated = config with
            {
                ServerUrl = serverUri.ToString().TrimEnd('/'),
                Token = ProtectToken(config.Token),
                Encrypted = true
            };
            File.WriteAllText(temporary, JsonSerializer.Serialize(migrated));
            File.Move(temporary, _configPath, true);
        }
        catch
        {
            // A failed migration must not prevent the Agent from reconnecting.
        }
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsLoopbackUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private void AddAuthentication(HttpRequestMessage request, string? pipelineCorrelationId = null)
    {
        request.Headers.Add("X-Agent-Id", _agentId.ToString());
        // Keep transport correlation explicit without putting secrets or audio
        // content into logs. Server responses return the authoritative trace id
        // which is persisted in the local receipt for the whole recording chain.
        request.Headers.TryAddWithoutValidation("X-Trace-Id", Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrWhiteSpace(pipelineCorrelationId)) request.Headers.TryAddWithoutValidation("X-Correlation-Id", pipelineCorrelationId);
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
        Guid InstallationId = default,
        string? ArchiveRoot = null,
        string? MicrophoneDeviceId = null,
        string? SystemAudioDeviceId = null);

    private static string ProtectToken(string token) => Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(token),
        Encoding.UTF8.GetBytes("WhisperXAtom.AgentToken.v1"),
        DataProtectionScope.LocalMachine));

    private static Task PersistConfigurationAsync(string path, AgentConfiguration configuration, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration), cancellationToken);

    public void Dispose()
    {
        _bindingGate.Dispose();
        _http.Dispose();
    }
}
