// Minimal VAD AudioWorkletProcessor for RMS and PCM forwarding

class VADProcessor extends AudioWorkletProcessor {
  process(inputs, outputs, parameters) {
    const input = inputs[0];
    if (!input || !input[0]) return true;
    const f32 = input[0];
    let sum = 0;
    for (let i = 0; i < f32.length; i++) sum += f32[i] * f32[i];
    const rms = Math.sqrt(sum / f32.length);
    // Copy Float32Array to transferable ArrayBuffer for main thread
    this.port.postMessage({ rms, f32: Array.from(f32) });
    return true;
  }
}

registerProcessor('vad-processor', VADProcessor);