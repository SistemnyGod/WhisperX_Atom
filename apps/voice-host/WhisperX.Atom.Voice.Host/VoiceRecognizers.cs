using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WhisperX.Atom.Voice.Host;

public sealed record VoiceRecognitionResult(string? Text, string? Partial, bool IsEndpoint, double Confidence);

public sealed class VoskModel : IDisposable
{
    internal IntPtr Native { get; private set; }
    public VoskModel(string modelPath)
    {
        if (!Directory.Exists(modelPath)) throw new DirectoryNotFoundException($"Vosk model not found: {modelPath}");
        NativeVosk.SetLogLevel(-1);
        Native = NativeVosk.ModelNewUtf8(modelPath);
        if (Native == IntPtr.Zero) throw new InvalidOperationException("Vosk native model could not be loaded.");
    }
    public void Dispose()
    {
        var handle = Native;
        Native = IntPtr.Zero;
        if (handle != IntPtr.Zero) NativeVosk.ModelFree(handle);
    }
}

public sealed class VoskRecognizer : IDisposable
{
    private readonly VoskModel _model;
    private readonly float _sampleRate;
    private readonly string? _grammar;
    private readonly bool _ownsModel;
    private IntPtr _recognizer;

    public VoskRecognizer(string modelPath, float sampleRate = 16000, IEnumerable<string>? grammar = null)
        : this(new VoskModel(modelPath), sampleRate, grammar, true) { }

    private VoskRecognizer(VoskModel model, float sampleRate, IEnumerable<string>? grammar, bool ownsModel)
    {
        _model = model;
        _sampleRate = sampleRate;
        _grammar = grammar is null ? null : JsonSerializer.Serialize(grammar.Distinct(StringComparer.Ordinal).ToArray(), new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        _ownsModel = ownsModel;
        _recognizer = CreateRecognizer();
    }

    public VoskRecognizer CreateSession(IEnumerable<string> grammar) => new(_model, _sampleRate, grammar, false);

    /// <summary>
    /// Creates a recognizer with the full model vocabulary.  The wake-word
    /// recognizer remains grammar constrained; this session is used only for
    /// the short, in-memory phrase that follows a confirmed wake word.
    /// </summary>
    public VoskRecognizer CreateUnrestrictedSession() => new(_model, _sampleRate, null, false);
    public VoiceRecognitionResult Accept(ReadOnlySpan<byte> pcm16kMono) => pcm16kMono.IsEmpty ? new(null, null, false, 0) : Accept(pcm16kMono.ToArray());
    public VoiceRecognitionResult Accept(byte[] pcm16kMono)
    {
        if (pcm16kMono.Length == 0) return new(null, null, false, 0);
        EnsureReady();
        var endpoint = NativeVosk.RecognizerAcceptWaveform(_recognizer, pcm16kMono, pcm16kMono.Length) != 0;
        var json = NativeVosk.ReadUtf8(endpoint ? NativeVosk.RecognizerResult(_recognizer) : NativeVosk.RecognizerPartialResult(_recognizer));
        return ParseResult(json, endpoint);
    }
    public VoiceRecognitionResult FinalizeSessionResult()
    {
        EnsureReady();
        var result = ParseResult(NativeVosk.ReadUtf8(NativeVosk.RecognizerFinalResult(_recognizer)), true);
        ResetSession();
        return result;
    }
    public string? FinalizeSession() => FinalizeSessionResult().Text;
    public void ResetSession()
    {
        var previous = _recognizer;
        _recognizer = IntPtr.Zero;
        if (previous != IntPtr.Zero) NativeVosk.RecognizerFree(previous);
        _recognizer = CreateRecognizer();
    }
    public VoiceRecognitionResult RecognizeBatchResult(ReadOnlyMemory<byte> pcm16kMono)
    {
        ResetSession();
        Accept(pcm16kMono.Span);
        return FinalizeSessionResult();
    }
    public string? RecognizeBatch(ReadOnlyMemory<byte> pcm16kMono) => RecognizeBatchResult(pcm16kMono).Text;
    private IntPtr CreateRecognizer()
    {
        var recognizer = string.IsNullOrWhiteSpace(_grammar) ? NativeVosk.RecognizerNew(_model.Native, _sampleRate) : NativeVosk.RecognizerNewGrammarUtf8(_model.Native, _sampleRate, _grammar);
        if (recognizer == IntPtr.Zero) throw new InvalidOperationException("Vosk native recognizer could not be created.");
        NativeVosk.RecognizerSetWords(recognizer, 1);
        return recognizer;
    }
    internal static VoiceRecognitionResult ParseResult(string json, bool endpoint)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var text = endpoint && root.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
            var partial = !endpoint && root.TryGetProperty("partial", out var partialElement) ? partialElement.GetString() : null;
            var confidence = 0d;
            var count = 0;
            if (root.TryGetProperty("result", out var words) && words.ValueKind == JsonValueKind.Array)
                foreach (var word in words.EnumerateArray())
                    if (word.TryGetProperty("conf", out var value) && value.TryGetDouble(out var current)) { confidence += current; count++; }
            return new(text, partial, endpoint, count == 0 ? 0d : confidence / count);
        }
        catch (JsonException) { return new(null, null, endpoint, 0); }
    }
    private void EnsureReady() { if (_recognizer == IntPtr.Zero) throw new ObjectDisposedException(nameof(VoskRecognizer)); }
    public void Dispose()
    {
        var recognizer = _recognizer;
        _recognizer = IntPtr.Zero;
        if (recognizer != IntPtr.Zero) NativeVosk.RecognizerFree(recognizer);
        if (_ownsModel) _model.Dispose();
    }
}

internal static class NativeVosk
{
    private const string Library = "libvosk";
    [DllImport(Library, EntryPoint = "vosk_set_log_level", CallingConvention = CallingConvention.Cdecl)] internal static extern void SetLogLevel(int level);
    [DllImport(Library, EntryPoint = "vosk_model_new", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ModelNew(IntPtr modelPathUtf8);
    [DllImport(Library, EntryPoint = "vosk_model_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void ModelFree(IntPtr model);
    [DllImport(Library, EntryPoint = "vosk_recognizer_new", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr RecognizerNew(IntPtr model, float sampleRate);
    [DllImport(Library, EntryPoint = "vosk_recognizer_new_grm", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr RecognizerNewGrammar(IntPtr model, float sampleRate, IntPtr grammarUtf8);
    [DllImport(Library, EntryPoint = "vosk_recognizer_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void RecognizerFree(IntPtr recognizer);
    [DllImport(Library, EntryPoint = "vosk_recognizer_set_words", CallingConvention = CallingConvention.Cdecl)] internal static extern void RecognizerSetWords(IntPtr recognizer, int words);
    [DllImport(Library, EntryPoint = "vosk_recognizer_accept_waveform", CallingConvention = CallingConvention.Cdecl)] internal static extern int RecognizerAcceptWaveform(IntPtr recognizer, byte[] data, int length);
    [DllImport(Library, EntryPoint = "vosk_recognizer_result", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr RecognizerResult(IntPtr recognizer);
    [DllImport(Library, EntryPoint = "vosk_recognizer_partial_result", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr RecognizerPartialResult(IntPtr recognizer);
    [DllImport(Library, EntryPoint = "vosk_recognizer_final_result", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr RecognizerFinalResult(IntPtr recognizer);
    internal static IntPtr ModelNewUtf8(string path) => WithUtf8(path, ModelNew);
    internal static IntPtr RecognizerNewGrammarUtf8(IntPtr model, float sampleRate, string grammar) => WithUtf8(grammar, value => RecognizerNewGrammar(model, sampleRate, value));
    internal static string ReadUtf8(IntPtr value) => value == IntPtr.Zero ? "{}" : Marshal.PtrToStringUTF8(value) ?? "{}";
    private static T WithUtf8<T>(string value, Func<IntPtr, T> action)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        try { return action(pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
}
