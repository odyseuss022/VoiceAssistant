using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;
using Vosk;

class Program
{
    static readonly Windows.Media.SpeechSynthesis.SpeechSynthesizer WinSynth = new();
    static WaveOutEvent?           _currentWaveOut;
    static CancellationTokenSource _speakCts = new();
    static volatile bool           _isSpeaking;

    static async Task<int> Main(string[] args)
    {
        // Verify mode: dotnet run -- --verify <modelPath> [testWav] [lmUrl]
        if (args.Length > 0 && args[0] == "--verify")
            return await RunVerify(args);

        // Auto-detect or accept model path / LM URL from args
        string modelPath = (args.Length > 0 ? args[0] : FindModelPath()) ?? string.Empty;
        string lmUrl     = args.Length > 1 ? args[1] : "http://127.0.0.1:1234";

        if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
        {
            Console.WriteLine("Vosk model not found.");
            Console.WriteLine("Usage : dotnet run -- <modelPath> [lmUrl]");
            Console.WriteLine("  Or place a vosk-model-* folder next to the executable for auto-detection.");
            return 1;
        }

        Console.WriteLine($"Model  : {modelPath}");
        Console.WriteLine($"LM     : {lmUrl}");
        Console.WriteLine("Ready — just speak. Ctrl+C to quit.");
        Console.WriteLine();

        InitVoice();
        AvatarForm.Launch();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Vosk.Vosk.SetLogLevel(0);
        using var model = new Model(modelPath);

        // Unbounded channel: VAD writer → main loop reader
        var channel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });

        using var waveIn = new WaveInEvent
        {
            WaveFormat         = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50   // 50 ms chunks → fine-grained VAD
        };
        AttachVad(waveIn, channel.Writer, cts.Token);
        waveIn.StartRecording();

        // Greeting — immediately demonstrates the avatar mouth and TTS work
        // regardless of whether LM Studio is running.
        AvatarForm.SetStatus("● Ready  —  speak to start");
        _ = SpeakAsync("Hello! I am your voice assistant and I am ready to help.");

        // Conversation history sent with every request
        var history = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "system",
                    ["content"] =
                        "You are a helpful voice assistant. " +
                        "Your responses will be converted directly to speech, so write exactly as you would speak. " +
                        "Do not use any special characters, symbols, or formatting of any kind: " +
                        "no asterisks, no pound signs, no dashes used as bullets, no numbered lists with dots, " +
                        "no parentheses for emphasis, no ellipses, no ALL-CAPS for emphasis, and no markdown. " +
                        "Use plain, natural spoken sentences only. Keep responses concise and conversational." }
        };

        try
        {
            await foreach (var audio in channel.Reader.ReadAllAsync(cts.Token))
            {
                var text = RecognizeWithVosk(model, audio);
                if (string.IsNullOrWhiteSpace(text))
                {
                    AvatarForm.SetStatus("● Ready  —  speak to start");
                    continue;
                }

                Console.WriteLine("\nYou: " + text);
                history.Add(new() { ["role"] = "user", ["content"] = text });

                AvatarForm.SetStatus("● Thinking...");
                string reply;
                try
                {
                    reply = await SendToLmStudioAsync(lmUrl, history, cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine("LM error: " + ex.Message);
                    AvatarForm.SetStatus("● LM error  —  check console");
                    history.RemoveAt(history.Count - 1); // drop unsent user turn
                    continue;
                }

                Console.WriteLine("Bot: " + reply);
                history.Add(new() { ["role"] = "assistant", ["content"] = reply });
                AvatarForm.SetStatus("● Ready  —  speak to start");
                _ = SpeakAsync(reply);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            waveIn.StopRecording();
            CancelSpeech();
            WinSynth.Dispose();
        }

        return 0;
    }

    // ── Voice Activity Detection ─────────────────────────────────────────────

    /// <summary>
    /// RMS threshold that triggers speech detection.
    /// Lower = more sensitive. Typical quiet mic: 50-150. Normal speech: 200-800.
    /// Raise this value if background noise causes false triggers.
    /// </summary>
    const double EnergyThreshold  = 500.0;

    /// <summary>Number of consecutive silent 50-ms chunks that end an utterance (~1 second).</summary>
    const int    SilenceChunksEnd = 20;

    static void AttachVad(WaveInEvent waveIn, ChannelWriter<byte[]> writer, CancellationToken ct)
    {
        var    ms          = new MemoryStream(256 * 1024);
        bool   active      = false;
        int    silCount    = 0;
        int    displayTick = 0;
        double peakInWin   = 0;

        waveIn.DataAvailable += (_, a) =>
        {
            if (ct.IsCancellationRequested) return;

            double energy = RmsEnergy(a.Buffer, a.BytesRecorded);

            if (energy > EnergyThreshold)
            {
                // Interrupt TTS the moment the user starts speaking
                if (_isSpeaking)
                    CancelSpeech();

                if (!active)
                {
                    active = true;
                    ms.SetLength(0);
                    Console.Write("\r[Listening]   ");
                    AvatarForm.SetStatus("● Listening...");
                }

                silCount    = 0;
                displayTick = 0;
                peakInWin   = 0;
                ms.Write(a.Buffer, 0, a.BytesRecorded);
            }
            else if (active)
            {
                // Buffer trailing silence so Vosk sees a natural end
                ms.Write(a.Buffer, 0, a.BytesRecorded);

                if (++silCount >= SilenceChunksEnd)
                {
                    active   = false;
                    silCount = 0;
                    Console.Write("\r[Processing]  ");
                    AvatarForm.SetStatus("● Processing speech...");
                    writer.TryWrite(ms.ToArray());
                    ms.SetLength(0);
                }
            }
            else
            {
                // Idle — show rolling mic level every ~1 s so user can calibrate
                peakInWin = Math.Max(peakInWin, energy);
                if (++displayTick >= 20)
                {
                    int peak = (int)peakInWin;
                    AvatarForm.SetStatus(
                        peak < (int)EnergyThreshold
                            ? $"● Mic: {peak}  (speak louder, need >{(int)EnergyThreshold})"
                            : $"● Mic: {peak}  ✓");
                    Console.Write($"\r[Idle] mic RMS peak={peak,-5} threshold={EnergyThreshold}   ");
                    displayTick = 0;
                    peakInWin   = 0;
                }
            }
        };
    }

    /// <summary>Compute RMS energy of a 16-bit PCM buffer.</summary>
    static double RmsEnergy(byte[] buf, int count)
    {
        if (count < 2) return 0;
        double sum = 0;
        for (int i = 0; i + 1 < count; i += 2)
        {
            short s = (short)(buf[i] | (buf[i + 1] << 8));
            sum += (double)s * s;
        }
        return Math.Sqrt(sum / (count / 2));
    }

    // ── Vosk STT ─────────────────────────────────────────────────────────────

    static string RecognizeWithVosk(Model model, byte[] audioData)
    {
        using var rec = new VoskRecognizer(model, 16000.0f);
        int offset = 0;
        while (offset < audioData.Length)
        {
            int n = Math.Min(4096, audioData.Length - offset);
            var chunk = new byte[n];
            Array.Copy(audioData, offset, chunk, 0, n);
            rec.AcceptWaveform(chunk, n);
            offset += n;
        }
        try
        {
            using var doc = JsonDocument.Parse(rec.FinalResult());
            if (doc.RootElement.TryGetProperty("text", out var t))
                return t.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    // ── LM Studio (OpenAI-compatible) ─────────────────────────────────────────

    static async Task<string> SendToLmStudioAsync(
        string lmUrl,
        List<Dictionary<string, string>> messages,
        CancellationToken ct = default)
    {
        using var http = new HttpClient();
        Uri? uri;
        if (Uri.TryCreate(lmUrl, UriKind.Absolute, out uri) &&
            string.IsNullOrEmpty(uri.PathAndQuery.Trim('/')))
            uri = new Uri(uri, "/v1/chat/completions");
        else if (!Uri.TryCreate(lmUrl, UriKind.Absolute, out uri))
            throw new ArgumentException("Invalid LM URL");

        var payload = new
        {
            messages,
            temperature = 0.7,
            max_tokens  = 512,
            stream      = false
        };
        var body = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await http.PostAsync(uri, body, ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content))
                    return content.GetString() ?? raw;
                if (first.TryGetProperty("text", out var txt))
                    return txt.GetString() ?? raw;
            }
            if (doc.RootElement.TryGetProperty("text",     out var t)) return t.GetString() ?? raw;
            if (doc.RootElement.TryGetProperty("response", out var r)) return r.GetString() ?? raw;
        }
        catch { }
        return raw;
    }

    // ── TTS helpers ──────────────────────────────────────────────────────────

    /// <summary>Select the best available female voice installed on this machine.</summary>
    static void InitVoice()
    {
        var female = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
            .FirstOrDefault(v => v.Gender == Windows.Media.SpeechSynthesis.VoiceGender.Female);
        if (female != null)
            WinSynth.Voice = female;
    }

    /// <summary>Stop any in-progress speech immediately.</summary>
    static void CancelSpeech()
    {
        _speakCts.Cancel();
        _currentWaveOut?.Stop();
        _isSpeaking = false;
        AvatarForm.SetSpeaking(false);
    }

    /// <summary>
    /// Synthesise <paramref name="text"/> with the Windows neural TTS engine
    /// and play it via NAudio. Safe to fire-and-forget; all exceptions caught internally.
    /// </summary>
    static async Task SpeakAsync(string text)
    {
        CancelSpeech();
        var cts = new CancellationTokenSource();
        _speakCts = cts;

        try
        {
            _isSpeaking = true;

            // Windows neural TTS → WAV stream
            var synthStream = await WinSynth.SynthesizeTextToStreamAsync(text);
            if (cts.Token.IsCancellationRequested) return;

            // Copy WinRT IRandomAccessStream → byte[]
            using var dataReader = new Windows.Storage.Streams.DataReader(
                synthStream.GetInputStreamAt(0));
            uint size = (uint)synthStream.Size;
            await dataReader.LoadAsync(size).AsTask(cts.Token);
            var bytes = new byte[size];
            dataReader.ReadBytes(bytes);
            if (cts.Token.IsCancellationRequested) return;

            // Play with NAudio
            using var ms         = new MemoryStream(bytes);
            using var waveReader = new WaveFileReader(ms);
            using var waveOut    = new WaveOutEvent();
            _currentWaveOut = waveOut;

            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waveOut.PlaybackStopped += (_, _) => tcs.TrySetResult(true);
            waveOut.Init(waveReader);
            AvatarForm.SetSpeaking(true);
            waveOut.Play();

            using var reg = cts.Token.Register(() =>
            {
                waveOut.Stop();
                tcs.TrySetResult(false);
            });
            await tcs.Task;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine("TTS error: " + ex.Message); }
        finally
        {
            if (ReferenceEquals(_speakCts, cts))
            {
                _isSpeaking = false;
                AvatarForm.SetSpeaking(false);
            }
            _currentWaveOut = null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static string? FindModelPath()
    {
        // Collect candidate directories: cwd, then walk up from the executable
        // (covers both 'dotnet run' and VS debugger which sets cwd to bin/Debug/...)
        var candidates = new List<string> { Directory.GetCurrentDirectory() };

        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar,
                                                    Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(dir))
        {
            candidates.Add(dir);
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        foreach (var d in candidates)
        {
            if (!Directory.Exists(d)) continue;
            var found = Directory.GetDirectories(d, "vosk-model*");
            if (found.Length > 0) return found[0];
        }
        return null;
    }

    static byte[] LoadWavAsPcm16Mono16k(string wavPath)
    {
        using var reader    = new WaveFileReader(wavPath);
        var       fmt       = new WaveFormat(16000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, fmt) { ResamplerQuality = 60 };
        using var ms        = new MemoryStream();
        var       buf       = new byte[16384];
        int       read;
        while ((read = resampler.Read(buf, 0, buf.Length)) > 0)
            ms.Write(buf, 0, read);
        return ms.ToArray();
    }

    // ── Verify mode ──────────────────────────────────────────────────────────

    static async Task<int> RunVerify(string[] args)
    {
        string modelPath = args.Length > 1 ? args[1] : string.Empty;
        string testWav   = args.Length > 2 ? args[2] : string.Empty;
        string lmUrl     = args.Length > 3 ? args[3] : "http://127.0.0.1:1234";

        if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
        {
            Console.WriteLine("[VERIFY] Vosk model path invalid or missing.");
            return 2;
        }

        Vosk.Vosk.SetLogLevel(0);
        using var model = new Model(modelPath);
        Console.WriteLine("[VERIFY] Loaded Vosk model from: " + modelPath);

        if (!string.IsNullOrWhiteSpace(testWav) && File.Exists(testWav))
        {
            Console.WriteLine("[VERIFY] Loading WAV: " + testWav);
            try
            {
                var bytes = LoadWavAsPcm16Mono16k(testWav);
                Console.WriteLine("[VERIFY] Vosk recognized: " + RecognizeWithVosk(model, bytes));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[VERIFY] Vosk recognition failed: " + ex.Message);
            }
        }

        Console.WriteLine("[VERIFY] Testing LM endpoint: " + lmUrl);
        try
        {
            var history = new List<Dictionary<string, string>>
            {
                new() { ["role"] = "user", ["content"] = "Hello from verification" }
            };
            var resp = await SendToLmStudioAsync(lmUrl, history);
            Console.WriteLine("[VERIFY] LM response: " + resp);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[VERIFY] LM request failed: " + ex.Message);
            return 3;
        }
        return 0;
    }
}