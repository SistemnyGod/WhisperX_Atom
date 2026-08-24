using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using WhisperX.Atom.Voice;

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
var expectedIdentity = Environment.GetEnvironmentVariable("WHISPERX_BUILD_IDENTITY") ?? string.Empty;
var modelPath = Environment.GetEnvironmentVariable("VOICE_REFINER_MODEL") ?? string.Empty;
var nativePath = Environment.GetEnvironmentVariable("VOICE_REFINER_NATIVE_LIBRARY") ?? string.Empty;
var modelHash = Environment.GetEnvironmentVariable("VOICE_ASR_REFINER_SHA256") ?? string.Empty;
var nativeHash = Environment.GetEnvironmentVariable("VOICE_ASR_REFINER_NATIVE_SHA256") ?? string.Empty;
using var backend = NativeWhisperBackend.TryCreate(modelPath, nativePath, modelHash, nativeHash, expectedIdentity, out var backendError);
if (backend is null)
{
    await RunUnavailableAsync(backendError ?? "VOICE_REFINER_NATIVE_UNAVAILABLE", expectedIdentity, options, ReadParentPid(args));
    return;
}

var queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(VoiceRefinerProtocol.QueueCapacity)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleReader = true,
    SingleWriter = false,
    AllowSynchronousContinuations = false
});
using var shutdown = new CancellationTokenSource();
var worker = ProcessQueueAsync(queue.Reader, backend, expectedIdentity, options, shutdown.Token);
var accept = AcceptConnectionsAsync(queue.Writer, expectedIdentity, options, shutdown.Token);
var parentMonitor = ParentMonitorAsync(ReadParentPid(args), shutdown.Token);
try { await Task.WhenAny(accept, parentMonitor); }
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
finally
{
    queue.Writer.TryComplete();
    shutdown.Cancel();
    try { await worker.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
}

static async Task AcceptConnectionsAsync(ChannelWriter<WorkItem> writer, string identity, JsonSerializerOptions options, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var pipe = new NamedPipeServerStream(VoiceRefinerProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(cancellationToken);
        _ = HandleConnectionAsync(pipe, writer, identity, options, cancellationToken);
    }
}

static async Task HandleConnectionAsync(NamedPipeServerStream pipe, ChannelWriter<WorkItem> writer, string identity, JsonSerializerOptions options, CancellationToken cancellationToken)
{
    await using (pipe)
    {
        var header = await ReadLineAsync(pipe, cancellationToken);
        var request = JsonSerializer.Deserialize<VoiceRefinerRequest>(header, options);
        if (request is null || request.SchemaVersion != VoiceRefinerProtocol.SchemaVersion || !string.Equals(request.Op, "refine", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(pipe, new(false, RequestId: request?.RequestId, ErrorCode: "VOICE_REFINER_INVALID_REQUEST"), options, cancellationToken);
            return;
        }
        if (!string.IsNullOrWhiteSpace(identity) && !string.Equals(identity, request.BuildIdentity, StringComparison.Ordinal))
        {
            await WriteResponseAsync(pipe, new(false, request.RequestId, ErrorCode: "VOICE_REFINER_BUILD_IDENTITY_MISMATCH"), options, cancellationToken);
            return;
        }
        if (request.PcmBytes is < 1600 or > VoiceRefinerProtocol.MaxPcmBytes)
        {
            await WriteResponseAsync(pipe, new(false, request.RequestId, ErrorCode: "VOICE_REFINER_PCM_SIZE_INVALID"), options, cancellationToken);
            return;
        }
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(pipe, lengthBytes, cancellationToken);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length != request.PcmBytes || length is < 1600 or > VoiceRefinerProtocol.MaxPcmBytes)
        {
            await WriteResponseAsync(pipe, new(false, request.RequestId, ErrorCode: "VOICE_REFINER_PCM_LENGTH_INVALID"), options, cancellationToken);
            return;
        }
        var pcm = new byte[length];
        await ReadExactlyAsync(pipe, pcm, cancellationToken);
        var work = new WorkItem(pipe, request, pcm, DateTimeOffset.UtcNow);
        if (!writer.TryWrite(work))
        {
            await WriteResponseAsync(pipe, new(false, request.RequestId, HostState: VoiceRefinerHostState.Busy.ToString().ToUpperInvariant(), ErrorCode: "VOICE_REFINER_QUEUE_DROPPED"), options, cancellationToken);
            return;
        }
        // The connection is intentionally kept alive until the single worker
        // has written the result. WorkItem owns the stream for that lifetime.
        await work.Completion.Task.WaitAsync(cancellationToken);
    }
}

static async Task ProcessQueueAsync(ChannelReader<WorkItem> reader, NativeWhisperBackend backend, string identity, JsonSerializerOptions options, CancellationToken cancellationToken)
{
    await foreach (var work in reader.ReadAllAsync(cancellationToken))
    {
        try
        {
            var queueWait = (DateTimeOffset.UtcNow - work.AcceptedAtUtc).TotalMilliseconds;
            var result = await backend.TranscribeAsync(work.Pcm, cancellationToken);
            var response = new VoiceRefinerResponse(result.Ok, work.Request.RequestId, result.Ok ? "READY" : "FAILED", VoiceRefinerHostState.Ready.ToString().ToUpperInvariant(), result.Text, null, result.ProcessingMs, queueWait, result.ErrorCode, identity, backend.ModelName, "whisper.cpp-native", result.Ok, VoiceRefinerProtocol.QueueCapacity);
            await WriteResponseAsync(work.Pipe, response, options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { try { await WriteResponseAsync(work.Pipe, new(false, work.Request.RequestId, ErrorCode: "VOICE_REFINER_HOST_FAILED"), options, CancellationToken.None); } catch { } }
        finally
        {
            work.Completion.TrySetResult();
            await work.Pipe.DisposeAsync();
        }
    }
}

static async Task RunUnavailableAsync(string error, string identity, JsonSerializerOptions options, int? parentPid)
{
    // Keep the pipe alive even when assets are absent so a client gets a
    // deterministic UNAVAILABLE response instead of spawning orphan work.
    using var shutdown = new CancellationTokenSource();
    var accept = RunUnavailableAcceptLoopAsync(error, identity, options, shutdown.Token);
    var parentMonitor = ParentMonitorAsync(parentPid, shutdown.Token);
    try
    {
        await Task.WhenAny(accept, parentMonitor);
    }
    finally
    {
        shutdown.Cancel();
        try { await accept; } catch (OperationCanceledException) { }
    }
}

static async Task RunUnavailableAcceptLoopAsync(string error, string identity, JsonSerializerOptions options, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await using var pipe = new NamedPipeServerStream(VoiceRefinerProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            var header = await ReadLineAsync(pipe, cancellationToken);
            var request = JsonSerializer.Deserialize<VoiceRefinerRequest>(header, options);
            await WriteResponseAsync(pipe, new(false, request?.RequestId, HostState: VoiceRefinerHostState.Degraded.ToString().ToUpperInvariant(), ErrorCode: error, BuildIdentity: identity), options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        catch { }
    }
}

static int? ReadParentPid(string[] arguments)
{
    var index = Array.FindIndex(arguments, static value => string.Equals(value, "--parent-pid", StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length && int.TryParse(arguments[index + 1], out var pid) ? pid : null;
}

static async Task ParentMonitorAsync(int? parentPid, CancellationToken cancellationToken)
{
    if (parentPid is not int pid || pid <= 0) return;
    try
    {
        using var parent = Process.GetProcessById(pid);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (parent.HasExited) return;
            await Task.Delay(1000, cancellationToken);
        }
    }
    catch (ArgumentException) { }
    catch (InvalidOperationException) { }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
}

static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
{
    using var buffer = new MemoryStream();
    var one = new byte[1];
    while (true)
    {
        await ReadExactlyAsync(stream, one, cancellationToken);
        if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
        if (buffer.Length > 16 * 1024) throw new InvalidDataException("VOICE_REFINER_HEADER_TOO_LARGE");
        buffer.WriteByte(one[0]);
    }
}

static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
        if (read == 0) throw new EndOfStreamException();
        offset += read;
    }
}

static async Task WriteResponseAsync(Stream stream, VoiceRefinerResponse response, JsonSerializerOptions options, CancellationToken cancellationToken)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, options) + "\n");
    await stream.WriteAsync(bytes, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

sealed record WorkItem(NamedPipeServerStream Pipe, VoiceRefinerRequest Request, byte[] Pcm, DateTimeOffset AcceptedAtUtc)
{
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

sealed class NativeWhisperBackend : IDisposable
{
    private readonly IntPtr _library;
    private readonly IntPtr _context;
    private readonly TranscribeDelegate? _transcribe;
    private readonly FreeDelegate? _free;
    public string ModelName { get; }

    private NativeWhisperBackend(IntPtr library, IntPtr context, TranscribeDelegate transcribe, FreeDelegate free, string modelName)
    {
        _library = library;
        _context = context;
        _transcribe = transcribe;
        _free = free;
        ModelName = modelName;
    }

    public static NativeWhisperBackend? TryCreate(string modelPath, string nativePath, string modelHash, string nativeHash, string identity, out string? error)
    {
        error = null;
        if (!File.Exists(modelPath)) { error = "VOICE_REFINER_MODEL_MISSING"; return null; }
        if (!File.Exists(nativePath)) { error = "VOICE_REFINER_NATIVE_LIBRARY_MISSING"; return null; }
        if (!VerifyHash(modelPath, modelHash) || !VerifyHash(nativePath, nativeHash)) { error = "VOICE_REFINER_ASSET_INTEGRITY_FAILED"; return null; }
        IntPtr library = IntPtr.Zero;
        try
        {
            library = NativeLibrary.Load(nativePath);
            if (!NativeLibrary.TryGetExport(library, "whisperx_refiner_init", out var initSymbol)
                || !NativeLibrary.TryGetExport(library, "whisperx_refiner_transcribe", out var transcribeSymbol)
                || !NativeLibrary.TryGetExport(library, "whisperx_refiner_free", out var freeSymbol))
            {
                NativeLibrary.Free(library); error = "VOICE_REFINER_NATIVE_ABI_MISSING"; return null;
            }
            var init = Marshal.GetDelegateForFunctionPointer<InitDelegate>(initSymbol);
            var transcribe = Marshal.GetDelegateForFunctionPointer<TranscribeDelegate>(transcribeSymbol);
            var free = Marshal.GetDelegateForFunctionPointer<FreeDelegate>(freeSymbol);
            var pathUtf8 = Encoding.UTF8.GetBytes(modelPath + "\0");
            var pathHandle = GCHandle.Alloc(pathUtf8, GCHandleType.Pinned);
            try
            {
                var context = init(pathHandle.AddrOfPinnedObject());
                if (context == IntPtr.Zero) { NativeLibrary.Free(library); error = "VOICE_REFINER_NATIVE_MODEL_LOAD_FAILED"; return null; }
                return new NativeWhisperBackend(library, context, transcribe, free, Path.GetFileName(modelPath));
            }
            finally { pathHandle.Free(); }
        }
        catch
        {
            if (library != IntPtr.Zero)
            {
                try { NativeLibrary.Free(library); } catch { }
            }
            error = "VOICE_REFINER_NATIVE_LOAD_FAILED";
            return null;
        }
    }

    public Task<NativeResult> TranscribeAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The pinned native bridge owns model lifetime and returns UTF-8 text
        // into a caller-provided buffer. No text is logged or persisted.
        var output = new byte[16 * 1024];
        var handle = GCHandle.Alloc(output, GCHandleType.Pinned);
        var input = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var length = _transcribe!(_context, input.AddrOfPinnedObject(), pcm.Length, handle.AddrOfPinnedObject(), output.Length);
            var text = length > 0 ? Encoding.UTF8.GetString(output, 0, Math.Min(length, output.Length)).Trim() : null;
            return Task.FromResult(new NativeResult(length > 0, text, Stopwatch.GetElapsedTime(started).TotalMilliseconds, length > 0 ? null : "VOICE_REFINER_NO_SPEECH"));
        }
        finally { input.Free(); handle.Free(); }
    }

    public void Dispose()
    {
        try { _free?.Invoke(_context); } catch { }
        if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
    }
    private static bool VerifyHash(string path, string expected)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return false;
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr InitDelegate(IntPtr modelPathUtf8);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TranscribeDelegate(IntPtr context, IntPtr pcm16kMono, int pcmBytes, IntPtr utf8Output, int outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeDelegate(IntPtr context);
}

sealed record NativeResult(bool Ok, string? Text, double ProcessingMs, string? ErrorCode);
