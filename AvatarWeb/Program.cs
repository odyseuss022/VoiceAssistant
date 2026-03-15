using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using NAudio.Wave;
using Vosk;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(
    p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// ── Vosk model — auto-detect same as the console app ─────────────────────
Vosk.Vosk.SetLogLevel(0);
var modelPath = FindVoskModel();
Model? voskModel = null;
if (modelPath is not null)
{
    Console.WriteLine($"[AvatarWeb] Vosk model: {modelPath}");
    voskModel = new Model(modelPath);
}
else
{
    Console.WriteLine("[AvatarWeb] WARNING: Vosk model not found — /api/recognize will return empty text.");
}

static string? FindVoskModel()
{
    var candidates = new List<string> { Directory.GetCurrentDirectory() };
    var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── Avatar proxy — bypasses ReadyPlayer Me CORS restrictions ─────────────
// Server-side fetch has no CORS policy; result is cached in memory AND on
// disk so the app works fully offline after the first successful online run.
var avatarCachePath = Path.Combine(AppContext.BaseDirectory, "avatar_cache.glb");
byte[]? _avatarBytes = null;

// Pre-load from disk cache at startup — enables offline use
if (File.Exists(avatarCachePath))
{
    try
    {
        _avatarBytes = File.ReadAllBytes(avatarCachePath);
        Console.WriteLine($"[AvatarWeb] Avatar loaded from disk cache ({_avatarBytes.Length:N0} bytes)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[AvatarWeb] Warning: could not read avatar disk cache: {ex.Message}");
    }
}

app.MapGet("/api/avatar", async (IHttpClientFactory cf) =>
{
    if (_avatarBytes is not null)
        return Results.Bytes(_avatarBytes, "model/gltf-binary");

    const string rpmUrl =
        "https://models.readyplayer.me/64bfa15f0e72c63d7c3934a6.glb" +
        "?morphTargets=ARKit,Oculus+Visemes,mouthOpen,mouthSmile," +
        "eyesClosed,eyesLookUp,eyesLookDown" +
        "&textureSizeLimit=1024&textureFormat=png";

    var http = cf.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

    try
    {
        var resp = await http.GetAsync(rpmUrl);
        resp.EnsureSuccessStatusCode();
        _avatarBytes = await resp.Content.ReadAsByteArrayAsync();
        // Persist to disk so subsequent runs (including offline) use the cache
        try { await File.WriteAllBytesAsync(avatarCachePath, _avatarBytes); }
        catch (Exception cacheEx) { Console.WriteLine($"[AvatarWeb] Warning: could not write avatar cache: {cacheEx.Message}"); }
        return Results.Bytes(_avatarBytes, "model/gltf-binary");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: "Avatar proxy failed: " + ex.Message, statusCode: 502);
    }
});

// ── Shared TTS helper ─────────────────────────────────────────────────────
// BUG FIXED: the previous implementation wrapped WinRT calls in
//   new Thread(async () => { ... })
// which is an async-void thread delegate.  The STA thread started, hit the
// first `await`, and returned immediately — orphaning the COM apartment.
// The async continuation ran on the ThreadPool. On the second call the WinRT
// runtime tried to re-enter the dead STA context and deadlocked.
//
// FIX: SpeechSynthesizer is an agile WinRT class (ThreadingModel.Both) and
// works correctly from any apartment, including MTA ThreadPool threads.
// We just await the WinRT operations directly, with a SemaphoreSlim to
// prevent concurrent calls (WinRT TTS is not re-entrant).
// top-level var — closed over by SynthesizeWavAsync below
var ttsSem = new SemaphoreSlim(1, 1);

// Non-static so it can capture ttsSem (static local functions cannot close over locals)
async Task<byte[]> SynthesizeWavAsync(string text)
{
    await ttsSem.WaitAsync();
    try
    {
        using var synth = new SpeechSynthesizer();
        var female = SpeechSynthesizer.AllVoices
            .FirstOrDefault(v => v.Gender == VoiceGender.Female);
        if (female is not null) synth.Voice = female;

        var stream = await synth.SynthesizeTextToStreamAsync(text);
        var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size  = (uint)stream.Size;
        await reader.LoadAsync(size);
        var bytes  = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
    finally
    {
        ttsSem.Release();
    }
}

// ── /api/gtts synthesis helper — WAV + evenly-spaced mark timepoints ─────
// TalkingHead pre-computes viseme animations client-side (line 2621).
// It then uses data.timepoints from the TTS response to align those
// animations to the actual audio.  Without timepoints the field is
// undefined → data.timepoints[0] → TypeError at talkinghead.mjs:3077.
//
// Approach:
//  1. Extract <mark name='N'/> names from the SSML (preserve order).
//  2. Strip <mark> tags before passing to Windows TTS (avoid unknown-element
//     parsing errors; <break> and <prosody> pass through unchanged).
//  3. Calculate audio duration from the WAV header (bytes 28-31 = avgBytesPerSec).
//  4. Distribute marks evenly: mark[i].timeSeconds = dur * (i+1) / (n+1).
//     This gives each word equal time — not perfect but avoids the crash
//     and produces continuous mouth movement.
async Task<(byte[] wav, object[] timepoints)> SynthesizeGttsAsync(string? ssml, string plainText)
{
    await ttsSem.WaitAsync();
    try
    {
        using var synth = new SpeechSynthesizer();
        var female = SpeechSynthesizer.AllVoices
            .FirstOrDefault(v => v.Gender == VoiceGender.Female);
        if (female is not null) synth.Voice = female;

        // Extract mark names before stripping (they disappear from clean SSML)
        List<string> markNames = new();
        if (ssml is not null)
        {
            markNames = Regex.Matches(ssml, @"<mark\s+name=['""](\w+)['""]")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();
        }

        // Remove <mark> tags so Windows TTS sees clean SSML
        // (Windows TTS supports <break>/<prosody> but may error on <mark>)
        SpeechSynthesisStream stream;
        if (ssml is not null)
        {
            // Strip <mark> elements
            var cleanSsml = Regex.Replace(ssml, @"<mark\s[^>]*/?>", "");

            // Windows TTS requires the SSML 1.0 namespace declaration.
            // TalkingHead sends bare <speak>...</speak> without it, causing
            // SynthesizeSsmlToStreamAsync to throw.  Inject it when absent.
            cleanSsml = Regex.Replace(cleanSsml,
                @"<speak\b(?![^>]*xmlns)",
                "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\"");

            try
            {
                stream = await synth.SynthesizeSsmlToStreamAsync(cleanSsml);
            }
            catch
            {
                // Fallback: synthesize plain text if SSML parse fails for any reason
                stream = await synth.SynthesizeTextToStreamAsync(plainText);
            }
        }
        else
        {
            stream = await synth.SynthesizeTextToStreamAsync(plainText);
        }

        // Read audio bytes
        var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size  = (uint)stream.Size;
        await reader.LoadAsync(size);
        var wav = new byte[size];
        reader.ReadBytes(wav);

        // Build evenly-spaced timepoints
        object[] timepoints = Array.Empty<object>();
        if (markNames.Count > 0)
        {
            // WAV header bytes 28-31: nAvgBytesPerSec (little-endian uint32)
            double durationSec = 3.0;
            if (wav.Length >= 44)
            {
                int avgBytesPerSec = BitConverter.ToInt32(wav, 28);
                if (avgBytesPerSec > 0)
                    durationSec = (double)(wav.Length - 44) / avgBytesPerSec;
            }

            int n = markNames.Count;
            timepoints = markNames.Select((name, i) => (object)new
            {
                markName    = name,
                timeSeconds = durationSec * (i + 1) / (n + 1)
            }).ToArray();
        }

        return (wav, timepoints);
    }
    finally
    {
        ttsSem.Release();
    }
}

// ── /api/tts — plain WAV for raw audio playback ──────────────────────────
app.MapPost("/api/tts", async (TtsRequest req) =>
{
    var wav = await SynthesizeWavAsync(req.Text);
    return Results.Bytes(wav, "audio/wav");
});

// ── /api/gtts — Google Cloud TTS-compatible endpoint ─────────────────────
// TalkingHead v1.3 sends SSML with <mark name='N'/> before each word and
// expects the response to include:
//   { "audioContent": "<base64-WAV>", "timepoints": [{markName, timeSeconds}, ...] }
// Without timepoints, TalkingHead's startSpeaking() crashes at line 3077:
//   data.timepoints[markIndex]  →  undefined[0]  →  TypeError
//
// Fix: strip <mark> tags from the SSML (Windows TTS doesn't need them for
// audio), synthesize the audio, compute evenly-spaced timing from the WAV
// duration, and return the timepoints so animations align to the audio.
app.MapPost("/api/gtts", async (JsonElement body) =>
{
    string? ssml = null;
    string text = "";
    try
    {
        var input = body.GetProperty("input");
        if (input.TryGetProperty("ssml", out var s))
        {
            ssml = s.GetString() ?? "";
            // Derive plain-text fallback (used only when SSML synthesis fails)
            text = Regex.Replace(ssml, "<[^>]+>", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
        }
        else if (input.TryGetProperty("text", out var t))
        {
            text = t.GetString() ?? "";
        }
        else
            return Results.BadRequest(new { error = "Expected input.text or input.ssml" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "Bad request: " + ex.Message });
    }

    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { error = "Empty text" });

    try
    {
        var (wav, timepoints) = await SynthesizeGttsAsync(ssml, text);
        return Results.Ok(new { audioContent = Convert.ToBase64String(wav), timepoints });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: "TTS synthesis failed: " + ex.Message, statusCode: 500);
    }
});

// ── /api/recognize — Vosk STT from raw int16 PCM (16 kHz mono) ───────────
// Browser sends raw Int16Array bytes (no WAV header); server feeds to Vosk.
app.MapPost("/api/recognize", async (HttpContext ctx) =>
{
    if (voskModel is null)
        return Results.Ok(new { text = "" });

    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var pcmBytes = ms.ToArray();

    if (pcmBytes.Length < 2)
        return Results.Ok(new { text = "" });

    // Run recognition synchronously (Vosk is not async)
    var text = await Task.Run(() =>
    {
        using var rec = new VoskRecognizer(voskModel, 16000.0f);
        int offset = 0;
        while (offset < pcmBytes.Length)
        {
            int n = Math.Min(4096, pcmBytes.Length - offset);
            var chunk = new byte[n];
            Array.Copy(pcmBytes, offset, chunk, 0, n);
            rec.AcceptWaveform(chunk, n);
            offset += n;
        }
        try
        {
            using var doc = JsonDocument.Parse(rec.FinalResult());
            if (doc.RootElement.TryGetProperty("text", out var t))
                return t.GetString() ?? "";
        }
        catch { }
        return "";
    });

    return Results.Ok(new { text });
});

// ── /api/config — expose client-facing settings ──────────────────────────
app.MapGet("/api/config", (IConfiguration cfg) =>
{
    return Results.Ok(new
    {
        lmUrl         = cfg["LmUrl"] ?? "http://127.0.0.1:1234",
        showTranscript = bool.Parse(cfg["ShowTranscript"] ?? "true")
    });
});

// ── Chat: proxy to LM Studio (OpenAI-compatible) ─────────────────────────
app.MapPost("/api/chat", async (IHttpClientFactory cf, ChatRequest req) =>
{
    var lmUrl = app.Configuration["LmUrl"] ?? "http://127.0.0.1:1234";
    var uri   = new Uri(new Uri(lmUrl), "/v1/chat/completions");
    var http  = cf.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(120);

    var payload = new
    {
        messages    = req.Messages,
        temperature = 0.7,
        max_tokens  = 512,
        stream      = false
    };
    var body = new StringContent(
        JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    try
    {
        var resp = await http.PostAsync(uri, body);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg) &&
                msg .TryGetProperty("content",  out var content))
                return Results.Ok(new { reply = content.GetString() ?? "" });
            if (first.TryGetProperty("text", out var txt))
                return Results.Ok(new { reply = txt.GetString() ?? "" });
        }
        return Results.Ok(new { reply = raw });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 502);
    }
});

app.Run("http://localhost:5000");

record TtsRequest(string Text);
record ChatRequest(List<Dictionary<string, string>> Messages);