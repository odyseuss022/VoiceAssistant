/**
 * Voice Assistant — always-on conversational mode
 *
 * Flow:
 *  1. TalkingHead library pre-loads (no AudioContext yet)
 *  2. User clicks "▶ Start" → TalkingHead instantiated SYNCHRONOUSLY
 *     (AudioContext created inside gesture frame = "running")
 *  3. Avatar GLB loads, mic permission requested, VAD loop starts
 *  4. Avatar greets the user
 *
 * Conversational VAD loop (always running):
 *  • Amplitude above VAD_SPEECH_THRESHOLD  → user speaking → start recording
 *  • Amplitude below threshold for VAD_SILENCE_MS → end of utterance → recognise
 *  • Voice detected while avatar is speaking → interrupt TTS immediately
 */

window.addEventListener('unhandledrejection', (ev) => {
  const msg = ev.reason?.message ?? String(ev.reason);
  console.error('Unhandled rejection:', msg, ev.reason);
  setStatus('⚠ ' + msg);
});

// ── Configuration ────────────────────────────────────────────────────────
const AVATAR_URL = '/api/avatar';

const SYSTEM_PROMPT =
  'You are a helpful voice assistant. Keep answers concise and conversational. ' +
  'No markdown, bullet points, or special characters — write only natural spoken sentences.';

// VAD tuning — adjust if too sensitive or too slow to trigger
const VAD_SPEECH_THRESHOLD        = 0.030;  // RMS amplitude → speech detected
const VAD_INTERRUPT_THRESHOLD     = 0.060;  // Higher threshold required to interrupt TTS
const VAD_INTERRUPT_CONFIRM_FRAMES = 6;      // Consecutive loud frames needed to interrupt
const VAD_SILENCE_MS              = 900;    // ms quiet after speech → submit
const VAD_MIN_SPEECH_MS           = 600;    // ignore utterances shorter than this

// ── DOM ───────────────────────────────────────────────────────────────────
const elStatus     = document.getElementById('status');
const elTranscript = document.getElementById('transcript');
const elStartScreen = document.getElementById('start-screen');
const btnStart     = document.getElementById('btn-start');

// ── App state ─────────────────────────────────────────────────────────────
let head           = null;
let TalkingHeadClass = null;
const history      = [{ role: 'system', content: SYSTEM_PROMPT }];
let appConfig      = { lmUrl: 'http://127.0.0.1:1234', showTranscript: true };

// VAD state machine: 'idle' | 'speaking' | 'processing'
let vadState   = 'idle';
let pcmChunks  = [];
let speechStart = 0;
let silenceTimer = null;
let ttsInterrupted = false;
let ttsShouldStop = false;
let interruptFrameCount = 0; // consecutive loud frames while TTS is playing
let chatGeneration = 0; // incremented on each new chat cycle; stale cycles abort themselves
let chatAbortController = null; // AbortController for in-flight LLM request

// ── Status helpers ────────────────────────────────────────────────────────
function setStatus(msg, listening = false) {
  elStatus.textContent = msg;
  elStatus.classList.toggle('listening', listening);
}
function setTranscript(msg) {
  if (!appConfig.showTranscript) return;
  elTranscript.textContent = msg;
}

// ── Interrupt current TTS ─────────────────────────────────────────────────
let fallbackAudio = null;
function interruptSpeech() {
  ttsShouldStop = true;
  window._interrupted = true;
  vadState = 'idle';
  console.log('[INTERRUPT] Calling head.stopSpeaking()');
  try { head?.stopSpeaking?.(); } catch (e) { console.log('[INTERRUPT] Error in stopSpeaking:', e); }
  if (fallbackAudio) {
    try {
      fallbackAudio.pause();
      fallbackAudio.currentTime = 0;
      fallbackAudio.src = "";
      fallbackAudio = null;
    } catch (_) {}
  }
}

// ── Speak via TalkingHead ─────────────────────────────────────────────────
async function speakReply(text) {
  setStatus('Speaking…');
  ttsShouldStop = false;
  if (window._interrupted) return;
  try {
    try { if (head?.audioCtx?.state !== 'running') await head.audioCtx.resume(); } catch (_) {}
    if (head) {
      // speakText() is not async — it queues speech and returns immediately.
      // Poll head.isSpeaking until TTS finishes or user interrupts.
      head.speakText(text);
      // Wait a tick for isSpeaking to become true
      await new Promise(r => setTimeout(r, 100));
      await new Promise(resolve => {
        const check = () => {
          if (ttsShouldStop || window._interrupted) {
            try { head.stopSpeaking(); } catch (_) {}
            resolve();
          } else if (!head.isSpeaking) {
            resolve();
          } else {
            setTimeout(check, 50);
          }
        };
        check();
      });
    } else {
      // No avatar — fallback to <audio>
      const resp = await fetch('/api/tts', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text }),
      });
      if (!resp.ok) throw new Error(`TTS ${resp.status}`);
      const blob = new Blob([await resp.arrayBuffer()], { type: 'audio/wav' });
      const url  = URL.createObjectURL(blob);
      await new Promise(resolve => {
        fallbackAudio = new Audio(url);
        fallbackAudio.onended = fallbackAudio.onerror = () => { URL.revokeObjectURL(url); resolve(); fallbackAudio = null; };
        setTimeout(() => { resolve(); fallbackAudio = null; }, 30_000);
        fallbackAudio.play().catch(() => { resolve(); fallbackAudio = null; });
      });
    }
  } catch (err) {
    console.error('[speakReply]', err);
    setStatus('Audio error: ' + (err.message ?? err));
  }
}

// ── Chat ──────────────────────────────────────────────────────────────────
async function chat(userText, myGeneration) {
  // Abort any previous in-flight LLM request
  if (chatAbortController) { try { chatAbortController.abort(); } catch (_) {} }
  chatAbortController = new AbortController();
  const signal = chatAbortController.signal;

  window._interrupted = false;
  setTranscript('You: ' + userText);
  setStatus('Thinking…');

  // Guard: only proceed if we are still the latest generation
  if (myGeneration !== chatGeneration) {
    if (vadState !== 'speaking') { vadState = 'idle'; setStatus('Listening…', true); }
    return;
  }

  // Build context — add user message to a local copy first, only commit if we win
  const requestHistory = [...history, { role: 'user', content: userText }];

  let reply = '';
  try {
    const resp = await fetch('/api/chat', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ messages: requestHistory }),
      signal,
    });
    if (resp.ok) reply = ((await resp.json()).reply ?? '').trim();
    else         reply = 'I could not reach the language model. Is LM Studio running?';
  } catch (e) {
    if (e.name === 'AbortError') {
      if (vadState !== 'speaking') { vadState = 'idle'; setStatus('Listening…', true); }
      return;
    }
    reply = 'Connection error: ' + e.message;
  }

  if (!reply) reply = 'I have no response right now.';

  // Abort if a newer cycle won while we were waiting for the LLM
  if (window._interrupted || myGeneration !== chatGeneration) {
    if (vadState !== 'speaking') { vadState = 'idle'; setStatus('Listening…', true); }
    return;
  }

  // We won — commit both messages to shared history
  history.push({ role: 'user', content: userText });
  history.push({ role: 'assistant', content: reply });

  vadState = 'processing';
  ttsInterrupted = false;
  await speakReply(reply);

  // TTS finished — always return to idle unless VAD already moved us to 'speaking'
  if (vadState !== 'speaking') {
    vadState = 'idle';
    setStatus('Listening…', true);
  }
}

// ── Process a completed utterance ─────────────────────────────────────────
async function processUtterance() {
  window._interrupted = false;
  const myGeneration = ++chatGeneration;
  const speechMs = Date.now() - speechStart;
  const chunks   = pcmChunks;
  pcmChunks = [];

  if (speechMs < VAD_MIN_SPEECH_MS || chunks.length === 0) {
    console.log('[VAD] utterance too short (' + speechMs + ' ms), ignored');
    if (vadState !== 'speaking') { vadState = 'idle'; setStatus('Listening…', true); }
    return;
  }

  vadState = 'processing';
  setStatus('Recognising…');

  // Flatten Int16 chunks into one buffer
  const total  = chunks.reduce((s, c) => s + c.length, 0);
  const allPcm = new Int16Array(total);
  let off = 0;
  for (const c of chunks) { allPcm.set(c, off); off += c.length; }

  try {
    const resp = await fetch('/api/recognize', {
      method: 'POST', headers: { 'Content-Type': 'application/octet-stream' },
      body: allPcm.buffer,
    });
    const { text } = await resp.json();
    if (text?.trim()) {
      await chat(text.trim(), myGeneration);
    } else {
      console.log('[VAD] empty transcript');
      if (vadState !== 'speaking') { vadState = 'idle'; setStatus('Listening…', true); }
    }
  } catch (e) {
    console.error('[recognize]', e);
    if (vadState !== 'speaking') { vadState = 'idle'; setStatus('Listening…', true); }
  }
}

// ── VAD loop — continuous mic monitoring ───────────────────────────────────
async function startVAD() {
  let micStream;
  try {
    micStream = await navigator.mediaDevices.getUserMedia({
      audio: { sampleRate: 16000, channelCount: 1,
               echoCancellation: true, noiseSuppression: true },
    });
  } catch (e) {
    setStatus('Mic denied: ' + e.message);
    return;
  }

  const mCtx      = new AudioContext({ sampleRate: 16000 });
  const source    = mCtx.createMediaStreamSource(micStream);

  // AudioWorkletNode for VAD (modern, low-latency)
  try {
    await mCtx.audioWorklet.addModule('/js/vad-worklet.js');
    const vadNode = new AudioWorkletNode(mCtx, 'vad-processor');
    vadNode.port.onmessage = (event) => {
      const { rms, f32 } = event.data;

      const isSpeech = rms > VAD_SPEECH_THRESHOLD;

      // ── Convert float32 → int16 helper ──
      const toI16 = (buf) => {
        const out = new Int16Array(buf.length);
        for (let i = 0; i < buf.length; i++)
          out[i] = Math.max(-32768, Math.min(32767, buf[i] * 32767));
        return out;
      };

      if (isSpeech) {
        // Cancel pending silence timer
        if (silenceTimer) { clearTimeout(silenceTimer); silenceTimer = null; }

        if (vadState === 'idle') {
          // ── User started speaking ──
          vadState    = 'speaking';
          speechStart = Date.now();
          pcmChunks   = [];
          interruptFrameCount = 0;
          setStatus('Listening…', true);
          console.log('[VAD] speech start');
        } else if (vadState === 'processing') {
          // ── Potential interrupt — require sustained loud audio above higher threshold ──
          if (rms > VAD_INTERRUPT_THRESHOLD) {
            interruptFrameCount++;
          } else {
            interruptFrameCount = 0;
          }
          if (interruptFrameCount >= VAD_INTERRUPT_CONFIRM_FRAMES) {
            interruptFrameCount = 0;
            ttsInterrupted = true;
            console.log('[VAD] interrupt confirmed');
            interruptSpeech();
            vadState    = 'speaking';
            speechStart = Date.now();
            pcmChunks   = [];
            setStatus('Listening…', true);
          }
        }

        // Accumulate PCM while speaking
        if (vadState === 'speaking') pcmChunks.push(toI16(f32));

      } else {
        interruptFrameCount = 0;
        if (vadState === 'speaking') {
          // Silence while we were recording — keep collecting (trailing audio matters)
          pcmChunks.push(toI16(f32));

          if (!silenceTimer) {
            silenceTimer = setTimeout(() => {
              silenceTimer = null;
              console.log('[VAD] silence end → process');
              processUtterance();
            }, VAD_SILENCE_MS);
          }
        }
      }
      // state === 'idle' + silence  → do nothing (passive monitoring)
      // state === 'processing' + silence → do nothing (waiting for TTS/LLM)
    };
    source.connect(vadNode).connect(mCtx.destination);
    console.log('[VAD] loop started (AudioWorkletNode)');
  } catch (e) {
    setStatus('AudioWorkletNode failed, falling back to ScriptProcessorNode');
    // Fallback to ScriptProcessorNode if AudioWorkletNode fails
    const processor = mCtx.createScriptProcessor(2048, 1, 1);

    processor.onaudioprocess = (ev) => {
      const f32 = ev.inputBuffer.getChannelData(0);

      // ── RMS amplitude ──
      let sum = 0;
      for (let i = 0; i < f32.length; i++) sum += f32[i] * f32[i];
      const rms = Math.sqrt(sum / f32.length);

      const isSpeech = rms > VAD_SPEECH_THRESHOLD;

      // ── Convert float32 → int16 helper ──
      const toI16 = (buf) => {
        const out = new Int16Array(buf.length);
        for (let i = 0; i < buf.length; i++)
          out[i] = Math.max(-32768, Math.min(32767, buf[i] * 32767));
        return out;
      };

      if (isSpeech) {
        // Cancel pending silence timer
        if (silenceTimer) { clearTimeout(silenceTimer); silenceTimer = null; }

        if (vadState === 'idle') {
          // ── User started speaking ──
          vadState    = 'speaking';
          speechStart = Date.now();
          pcmChunks   = [];
          interruptFrameCount = 0;
          setStatus('Listening…', true);
          console.log('[VAD] speech start');
        } else if (vadState === 'processing') {
          // ── Potential interrupt — require sustained loud audio above higher threshold ──
          if (rms > VAD_INTERRUPT_THRESHOLD) {
            interruptFrameCount++;
          } else {
            interruptFrameCount = 0;
          }
          if (interruptFrameCount >= VAD_INTERRUPT_CONFIRM_FRAMES) {
            interruptFrameCount = 0;
            ttsInterrupted = true;
            console.log('[VAD] interrupt confirmed (fallback)');
            interruptSpeech();
            vadState    = 'speaking';
            speechStart = Date.now();
            pcmChunks   = [];
            setStatus('Listening…', true);
          }
        }

        // Accumulate PCM while speaking
        if (vadState === 'speaking') pcmChunks.push(toI16(f32));

      } else {
        interruptFrameCount = 0;
        if (vadState === 'speaking') {
          // Silence while we were recording — keep collecting (trailing audio matters)
          pcmChunks.push(toI16(f32));

          if (!silenceTimer) {
            silenceTimer = setTimeout(() => {
              silenceTimer = null;
              console.log('[VAD] silence end → process');
              processUtterance();
            }, VAD_SILENCE_MS);
          }
        }
      }
      // state === 'idle' + silence  → do nothing (passive monitoring)
      // state === 'processing' + silence → do nothing (waiting for TTS/LLM)
    };

    source.connect(processor);
    processor.connect(mCtx.destination);
    console.log('[VAD] loop started (ScriptProcessorNode fallback)');
  }
}

// ── "▶ Start" button — only interaction needed ────────────────────────────
btnStart.addEventListener('click', () => {
  if (!TalkingHeadClass) { setStatus('Still loading…'); return; }

  // Fade out start screen
  elStartScreen.classList.add('hidden');

  // *** SYNCHRONOUS — must be before any await ***
  // new AudioContext() called inside user-gesture = state "running".
  head = new TalkingHeadClass(document.getElementById('avatar'), {
    ttsEndpoint:    '/api/gtts',
    cameraView:     'upper',
    cameraDistance:  0.5,
    cameraRotateX:  -0.2,
    cameraRotateY:   0,
    avatarMood:     'neutral',
  });

  (async () => {
    setStatus('Loading avatar…');
    try {
      await Promise.race([
        head.showAvatar(
          { url: AVATAR_URL, body: 'F', lipsyncLang: 'en' },
          (ev) => {
            if (ev.lengthComputable)
              setStatus(`Loading… ${Math.round(100 * ev.loaded / ev.total)}%`);
          }
        ),
        new Promise((_, rej) =>
          setTimeout(() => rej(new Error('Avatar load timed out')), 25_000)),
      ]);
      try { if (head.audioCtx.state !== 'running') await head.audioCtx.resume(); } catch (_) {}
    } catch (err) {
      console.warn('Avatar load error (continuing without avatar):', err);
      head = null;  // showAvatar failed — TalkingHead internal state is uninitialised;
                    // null head makes speakReply fall back to the /api/tts audio path
      setStatus('⚠ Avatar unavailable — voice only');
    }

    // Start always-on VAD (requests mic here)
    await startVAD();

    // Greet — spoken only, NOT added to history.
    // Gemma-3 (and many other models) require strict user/assistant alternation;
    // an assistant message before the first user turn causes a jinja template error.
    vadState = 'processing';
    const greeting = "Hello! I'm your voice assistant. How can I help you?";
    await speakReply(greeting);
    if (vadState === 'processing') {
      vadState = 'idle';
      setStatus('Listening…', true);
    }
  })();
});

// ── Load config, then pre-load TalkingHead library ────────────────────────
(async () => {
  // Fetch server-side config first
  try {
    const cfgResp = await fetch('/api/config');
    if (cfgResp.ok) {
      const cfg = await cfgResp.json();
      appConfig = { ...appConfig, ...cfg };
    }
  } catch (_) { /* use defaults */ }

  // Apply showTranscript immediately
  if (!appConfig.showTranscript) {
    elTranscript.style.display = 'none';
  }

  setStatus('Loading 3D engine…');
  try {
    const mod = await import(
      '/js/talkinghead.mjs'
    );
    TalkingHeadClass = mod.TalkingHead;
    btnStart.textContent = '▶  Start';
    btnStart.disabled    = false;
    setStatus('Click Start to begin');
  } catch (err) {
    console.error('TalkingHead load failed:', err);
    // Allow voice-only mode even without 3D engine
    TalkingHeadClass = null;
    btnStart.textContent = '▶  Start (voice only)';
    btnStart.disabled    = false;
    setStatus('3D engine unavailable — voice only');
  }
})();