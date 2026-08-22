using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WhisperX.Atom.Voice;

namespace WhisperX.Atom.Voice.Host;

internal static class VoiceAcceptanceRunner
{
    /// <summary>
    /// Validate the installed parser binary without opening a microphone or
    /// sending a command to Recorder.  This is the safe production-gate
    /// half: the installed Voice Host must classify recorder intents before a
    /// separate microphone gate is allowed to exercise audio capture.
    /// </summary>
    public static Task<int> CommandAsync()
    {
        var parser = new VoiceIntentParser(VoiceHostRuntime.LegacyAtomWakeEnabled);
        var cases = new[]
        {
            ("start", "Мифодий, начни запись", VoiceIntent.StartRecording),
            ("start-alias", "Мифодий, запусти запись", VoiceIntent.StartRecording),
            ("pause", "Мифодий, поставь на паузу", VoiceIntent.PauseRecording),
            ("resume", "Мифодий, продолжи запись", VoiceIntent.ResumeRecording),
            ("stop", "Мифодий, останови запись", VoiceIntent.StopRecording),
            ("status-recording", "Мифодий, запись идёт?", VoiceIntent.GetStatus),
            ("status-duration", "Мифодий, сколько идет запись", VoiceIntent.GetStatus),
            ("status-server", "Мифодий, состояние сервера", VoiceIntent.GetServerStatus),
            ("status-pipeline", "Мифодий, состояние обработки", VoiceIntent.GetPipelineStatus),
            ("status-stage", "Мифодий, какая стадия обработки", VoiceIntent.GetPipelineStatus),
            ("status-transcript", "Мифодий, стенограмма готова", VoiceIntent.GetPipelineStatus),
            ("status-storage", "Мифодий, свободное место", VoiceIntent.GetStorageStatus),
            ("status-storage-remaining", "Мифодий, сколько осталось места", VoiceIntent.GetStorageStatus),
            ("farewell", "Мифодий, пока", VoiceIntent.Farewell),
            ("farewell-goodbye", "Мифодий, до свидания", VoiceIntent.Farewell),
            ("repeat", "Мифодий, повтори", VoiceIntent.RepeatAnswer),
            ("shorten", "Мифодий, короче", VoiceIntent.ShortenAnswer),
            ("elaborate", "Мифодий, подробнее", VoiceIntent.ElaborateAnswer),
            ("previous-question", "Мифодий, вернись к предыдущему вопросу", VoiceIntent.PreviousQuestion),
            ("question-repair", "Мифодий, кто отвечал за ремонт?", VoiceIntent.AssistantQuery),
            ("question-deadline", "Мифодий, какой срок назвали?", VoiceIntent.AssistantQuery),
            ("question-pump", "Мифодий, что решили по насосу?", VoiceIntent.AssistantQuery),
        };
        var aliases = new List<string> { "Мифодий, начни запись", "Мефодий, начни запись" };
        if (VoiceHostRuntime.LegacyAtomWakeEnabled)
            aliases.Add("Атом, начни запись");
        var results = cases.Select(item =>
        {
            var command = parser.Parse(item.Item2, 0.95);
            return new
            {
                id = item.Item1,
                text = item.Item2,
                expectedIntent = item.Item3.ToString(),
                actualIntent = command.Intent.ToString(),
                hasWakeWord = parser.HasWakeWord(item.Item2),
                parameterPresent = (item.Item3 is VoiceIntent.AssistantQuery or VoiceIntent.Farewell)
                    && !string.IsNullOrWhiteSpace(command.Parameter),
                accepted = parser.HasWakeWord(item.Item2) && command.Intent == item.Item3,
            };
        }).ToArray();
        var aliasResults = aliases.Select(text =>
        {
            var command = parser.Parse(text, 0.95);
            return new { text, intent = command.Intent.ToString(), accepted = parser.HasWakeWord(text) && command.Intent == VoiceIntent.StartRecording };
        }).ToArray();
        var passed = results.All(item => item.accepted && (item.expectedIntent != VoiceIntent.AssistantQuery.ToString() || item.parameterPresent))
            && aliasResults.All(item => item.accepted);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "command-acceptance",
            passed,
            recorderInvocations = 0,
            brokerInvocations = 0,
            cases = results,
            aliases = aliasResults,
        }));
        return Task.FromResult(passed ? 0 : 4);
    }

    public static async Task<int> ReplayAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
        {
            Console.Error.WriteLine("Replay audio file was not found.");
            return 2;
        }

        var modelPath = ResolveModelPath();
        var assets = VoiceAssetVerifier.Check(Path.GetDirectoryName(modelPath)!, modelPath);
        if (!assets.IntegrityReady)
        {
            Console.Error.WriteLine(assets.ErrorCode ?? "VOICE_MODEL_MISSING");
            return 3;
        }

        using var analyzer = new DryRunAnalyzer(modelPath);
        using var reader = new AudioFileReader(audioPath);
        ISampleProvider source = reader;
        if (reader.WaveFormat.SampleRate != 16000)
            source = new WdlResamplingSampleProvider(source, 16000);

        var channels = source.WaveFormat.Channels;
        var samples = new float[320 * channels];
        var pcm = new byte[640];
        var detections = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = source.Read(samples, 0, samples.Length);
            if (read <= 0) break;
            var frames = read / channels;
            ConvertToMonoPcm16(samples, frames, channels, pcm);
            detections += analyzer.Accept(pcm.AsSpan(0, frames * 2));
            await Task.Yield();
        }
        detections += analyzer.FinalizeStream();
        // Keep replay output machine-readable so the far-field acceptance
        // matrix can compare recall and false activations for each distance /
        // room condition.  The acceptance script deliberately strips the
        // source path before writing its sanitized artifact.
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "replay-dry-run",
            audioPath,
            detections,
            recognizedEndpoints = analyzer.RecognizedEndpoints,
            falseActivations = analyzer.FalseActivations,
            aliases = analyzer.AcceptedByAlias,
        }));
        return analyzer.FalseActivations == 0 ? 0 : 4;
    }

    public static async Task<int> MicrophoneAsync(int targetDetections, TimeSpan timeout, CancellationToken cancellationToken, string? microphoneDeviceId = null)
    {
        var modelPath = ResolveModelPath();
        var assets = VoiceAssetVerifier.Check(Path.GetDirectoryName(modelPath)!, modelPath);
        if (!assets.IntegrityReady)
        {
            Console.Error.WriteLine(assets.ErrorCode ?? "VOICE_MODEL_MISSING");
            return 3;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var analyzer = new DryRunAnalyzer(modelPath);
        using var capture = new VoiceAudioCapture();
        var converter = new AudioPcmConverter();
        var assembler = new PcmFrameAssembler();
        var queue = Channel.CreateBounded<VoiceAudioBlock>(new BoundedChannelOptions(20)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var drops = 0L;
        capture.AudioAvailable += block =>
        {
            if (!queue.Writer.TryWrite(block))
            {
                Interlocked.Increment(ref drops);
                block.Dispose();
            }
        };
        capture.CaptureError += ex =>
        {
            Console.Error.WriteLine("MICROPHONE_CAPTURE_FAILED");
            linked.Cancel();
        };

        var detections = 0;
        string? effectiveDeviceId = null;
        string? effectiveDeviceName = null;
        string? captureErrorCode = null;
        try
        {
            capture.Start(microphoneDeviceId);
            Console.WriteLine($"Microphone dry-run started on '{capture.DeviceName ?? microphoneDeviceId ?? "DEFAULT"}'. Say {targetDetections} commands beginning with 'Мифодий'.");
            while (await queue.Reader.WaitToReadAsync(linked.Token))
            {
                while (queue.Reader.TryRead(out var block))
                {
                    using (block)
                    {
                        var pcm = converter.Convert(block.Buffer, block.Length, block.Format);
                        await assembler.PushAsync(pcm, frame =>
                        {
                            detections += analyzer.Accept(frame);
                            if (detections >= targetDetections) linked.Cancel();
                            return Task.CompletedTask;
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception)
        {
            captureErrorCode = "VOICE_MICROPHONE_UNAVAILABLE";
            Console.Error.WriteLine(captureErrorCode);
        }
        finally
        {
            effectiveDeviceId = capture.DeviceId;
            effectiveDeviceName = capture.DeviceName;
            capture.Stop();
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var block)) block.Dispose();
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "mic-acceptance-dry-run",
            detections,
            targetDetections,
            audioQueueDrops = drops,
            microphoneDeviceId = effectiveDeviceId,
            microphoneName = effectiveDeviceName,
            errorCode = captureErrorCode,
            aliases = analyzer.AcceptedByAlias,
            falseActivations = analyzer.FalseActivations,
            recognizedEndpoints = analyzer.RecognizedEndpoints,
            completed = detections >= targetDetections
        }));
        return captureErrorCode is null && detections >= targetDetections ? 0 : captureErrorCode is null ? 4 : 5;
    }

    private static string ResolveModelPath()
    {
        var assetsRoot = Path.Combine(AppContext.BaseDirectory, "Models", "Voice");
        return Environment.GetEnvironmentVariable("ATOM_VOSK_MODEL")
            ?? Path.Combine(assetsRoot, "vosk-model-small-ru-0.22");
    }

    private static void ConvertToMonoPcm16(float[] source, int frames, int channels, byte[] destination)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
                sum += source[frame * channels + channel];
            var sample = (short)Math.Clamp(sum / channels * short.MaxValue, short.MinValue, short.MaxValue);
            destination[frame * 2] = (byte)(sample & 0xff);
            destination[frame * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }
    }

    private sealed class DryRunAnalyzer : IDisposable
    {
        private readonly VoskRecognizer _recognizer;
        private readonly VoiceIntentParser _parser = new(VoiceHostRuntime.LegacyAtomWakeEnabled);
        public Dictionary<string, int> AcceptedByAlias { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int FalseActivations { get; private set; }
        public int RecognizedEndpoints { get; private set; }

        public DryRunAnalyzer(string modelPath) =>
            _recognizer = new VoskRecognizer(modelPath, grammar: VoiceHostRuntime.WakePhrases);

        public int Accept(ReadOnlySpan<byte> pcm)
        {
            var result = _recognizer.Accept(pcm);
            return result.IsEndpoint ? Report(result) : 0;
        }

        public int FinalizeStream() => Report(_recognizer.FinalizeSessionResult());

        private int Report(VoiceRecognitionResult result)
        {
            if (string.IsNullOrWhiteSpace(result.Text)) return 0;
            RecognizedEndpoints++;
            var hasWake = _parser.HasWakeWord(result.Text);
            var command = _parser.Parse(result.Text, result.Confidence);
            // Keep the microphone acceptance gate aligned with the runtime
            // policy: conversational questions may pass at the recognition
            // floor, while recorder mutations use their action-specific
            // safety thresholds.  A single fixed 0.65 floor made HIGH
            // sensitivity paradoxically reject valid questions and did not
            // model the stricter STOP confirmation boundary.
            var requiredConfidence = command.Intent switch
            {
                VoiceIntent.StopRecording or VoiceIntent.StopSpeaking => 0.70,
                VoiceIntent.StartRecording or VoiceIntent.PauseRecording or VoiceIntent.ResumeRecording => 0.60,
                VoiceIntent.AddMarker or VoiceIntent.MarkDecision or VoiceIntent.MarkActionItem => 0.55,
                VoiceIntent.AssistantQuery => VoiceIntentParser.DefaultMinimumConfidence,
                VoiceIntent.RepeatAnswer or VoiceIntent.ShortenAnswer or VoiceIntent.ElaborateAnswer or VoiceIntent.PreviousQuestion => VoiceIntentParser.DefaultMinimumConfidence,
                _ => VoiceIntentParser.DefaultMinimumConfidence
            };
            var accepted = hasWake && !result.Text.Contains("[unk]", StringComparison.OrdinalIgnoreCase)
                && result.Confidence >= requiredConfidence && command.Intent != VoiceIntent.Unknown;
            if (hasWake && command.Intent == VoiceIntent.Unknown) FalseActivations++;
            if (accepted)
            {
                var alias = result.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim(',', ':')?.ToLowerInvariant() ?? "unknown";
                alias = alias switch
                {
                    "мифодий" => "myfodiy",
                    "мефодий" => "mefodiy",
                    "атом" => "atom",
                    _ => alias
                };
                if (alias is "myfodiy" or "mefodiy" or "atom")
                    AcceptedByAlias[alias] = AcceptedByAlias.TryGetValue(alias, out var count) ? count + 1 : 1;
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                text = result.Text,
                confidence = result.Confidence,
                intent = command.Intent.ToString(),
                accepted,
                dryRun = true
            }));
            return accepted ? 1 : 0;
        }

        public void Dispose() => _recognizer.Dispose();
    }
}
