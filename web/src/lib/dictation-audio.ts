// Pure audio helpers for voice input (the same rules as Lazarette's): downsampling to Whisper's 16 kHz and an
// energy-based "has the person stopped talking" detector. No DOM, no network.

export const STT_RATE = 16000;

/** Averages the input down to outRate (Whisper wants 16 kHz mono). */
export function downsample(input: Float32Array, inRate: number, outRate = STT_RATE): Float32Array {
  if (inRate === outRate) return input.slice();
  const ratio = inRate / outRate;
  const out = new Float32Array(Math.floor(input.length / ratio));
  for (let i = 0; i < out.length; i++) {
    const start = Math.floor(i * ratio), end = Math.min(input.length, Math.floor((i + 1) * ratio));
    let sum = 0;
    for (let j = start; j < end; j++) sum += input[j]!;
    out[i] = sum / Math.max(1, end - start);
  }
  return out;
}

export function rms(chunk: Float32Array): number {
  let s = 0;
  for (const v of chunk) s += v * v;
  return Math.sqrt(s / (chunk.length || 1));
}

/**
 * Collects 16 kHz chunks from the first loud one on. push() returns 'done' once speech was heard and then endSilenceMs of
 * quiet followed (or maxMs passed); audio() returns everything kept.
 */
export function createUtterance(o: { threshold?: number; minSpeechMs?: number; endSilenceMs?: number; maxMs?: number; rate?: number } = {}) {
  const rate = o.rate ?? STT_RATE;
  const threshold = o.threshold ?? 0.012;
  const minSpeech = ((o.minSpeechMs ?? 250) * rate) / 1000;
  const endSilence = ((o.endSilenceMs ?? 1500) * rate) / 1000;
  const maxLen = ((o.maxMs ?? 30000) * rate) / 1000;
  const chunks: Float32Array[] = [];
  let length = 0, speech = 0, silence = 0, started = false;
  return {
    push(chunk: Float32Array): 'waiting' | 'speaking' | 'done' {
      const loud = rms(chunk) >= threshold;
      if (!started && !loud) return 'waiting';
      started = true;
      chunks.push(chunk);
      length += chunk.length;
      if (loud) { speech += chunk.length; silence = 0; } else silence += chunk.length;
      if ((speech >= minSpeech && silence >= endSilence) || length >= maxLen) return 'done';
      return 'speaking';
    },
    get heard() { return speech >= minSpeech; },
    audio(): Float32Array {
      const out = new Float32Array(length);
      let at = 0;
      for (const c of chunks) { out.set(c, at); at += c.length; }
      return out;
    },
  };
}

/** Whisper writes "[BLANK_AUDIO]", "(music)" and the like for silence: those are not words. */
export function cleanTranscript(text: string): string {
  return text.replace(/\[[^\]]*\]|\([^)]*\)/g, ' ').replace(/\s+/g, ' ').trim();
}
