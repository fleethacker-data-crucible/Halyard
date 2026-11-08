// Spoken answers. Each piece of text goes to POST /api/voice/speak on this origin; the engine sends it to Fairlead's
// /v1/audio/speech, which routes to TalkForge on this computer, and returns WAV audio that plays from a blob: URL. One
// piece is fetched ahead while the current one plays, and the real audio level drives the orb. When TalkForge cannot be
// reached the browser's own installed (local) voice is used instead; a cloud voice never is.
import { sessionToken } from './api';
import { speechPieces } from './crew';
import { pickLocalVoice } from './voice';

type Listener = () => void;

export class SpeechPlayer {
  speaking = false;
  /** Loudness of the audio now playing, 0 to about 0.3 (speech RMS). */
  level = 0;
  error = '';
  /** talkforge once TalkForge spoke, local when the browser voice stood in. */
  engine: 'talkforge' | 'local' | '' = '';

  private listeners = new Set<Listener>();
  private gen = 0;
  private audio: HTMLAudioElement | null = null;
  private url = '';
  private ctx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private samples: Float32Array<ArrayBuffer> | null = null;
  private frame = 0;
  private abort: AbortController | null = null;
  private useLocal = false;

  subscribe(fn: Listener) { this.listeners.add(fn); return () => { this.listeners.delete(fn); }; }
  private emit() { for (const fn of this.listeners) fn(); }

  stop() {
    this.gen++;
    this.abort?.abort();
    this.abort = null;
    this.audio?.pause();
    if (typeof window !== 'undefined') window.speechSynthesis?.cancel();
    if (this.url) URL.revokeObjectURL(this.url);
    this.url = '';
    cancelAnimationFrame(this.frame);
    this.speaking = false;
    this.level = 0;
    this.emit();
  }

  /** Speaks the text from the start, a sentence at a time. */
  async say(text: string) {
    this.stop();
    const gen = this.gen;
    const pieces = speechPieces(text);
    if (pieces.length === 0) return;
    this.speaking = true;
    this.error = '';
    this.emit();
    if (this.useLocal) { this.sayLocal(pieces, gen); return; }
    this.abort = new AbortController();
    let next: Promise<Blob | null> = this.fetchPiece(pieces[0]!, this.abort.signal);
    for (let i = 0; i < pieces.length; i++) {
      const blob = await next;
      if (gen !== this.gen) return;
      if (!blob) {
        // TalkForge is not reachable: say the rest with the browser's installed voice, and keep using it this session.
        this.useLocal = true;
        this.sayLocal(pieces.slice(i), gen);
        return;
      }
      next = i + 1 < pieces.length ? this.fetchPiece(pieces[i + 1]!, this.abort.signal) : Promise.resolve(null);
      this.engine = 'talkforge';
      await this.play(blob, gen);
      if (gen !== this.gen) return;
    }
    this.speaking = false;
    this.level = 0;
    this.emit();
  }

  /** Tries TalkForge again (Settings, or after starting it). */
  retryTalkForge() { this.useLocal = false; }

  private async fetchPiece(text: string, signal: AbortSignal): Promise<Blob | null> {
    try {
      const headers = new Headers({ 'Content-Type': 'application/json', Accept: 'audio/wav' });
      const token = sessionToken();
      if (token) headers.set('X-Halyard-Session', token);
      const r = await fetch('/api/voice/speak', { method: 'POST', headers, body: JSON.stringify({ text }), signal, cache: 'no-store' });
      if (!r.ok) {
        try { this.error = ((await r.json()) as { error?: string }).error ?? `Speech failed with HTTP ${r.status}`; } catch { this.error = `Speech failed with HTTP ${r.status}`; }
        return null;
      }
      return await r.blob();
    } catch (e) {
      if (!signal.aborted) this.error = e instanceof Error ? e.message : String(e);
      return null;
    }
  }

  private play(blob: Blob, gen: number): Promise<void> {
    return new Promise((resolve) => {
      if (this.url) URL.revokeObjectURL(this.url);
      this.url = URL.createObjectURL(blob);
      const audio = (this.audio ??= new Audio());
      this.connectMeter(audio);
      const done = () => { audio.removeEventListener('ended', done); audio.removeEventListener('error', done); cancelAnimationFrame(this.frame); this.level = 0; resolve(); };
      audio.addEventListener('ended', done);
      audio.addEventListener('error', done);
      audio.src = this.url;
      void this.ctx?.resume().catch(() => undefined);
      audio.play().then(() => this.meter(gen)).catch(() => done());
    });
  }

  private connectMeter(audio: HTMLAudioElement) {
    if (this.ctx || typeof AudioContext === 'undefined') return;
    try {
      this.ctx = new AudioContext();
      const source = this.ctx.createMediaElementSource(audio);
      this.analyser = this.ctx.createAnalyser();
      this.analyser.fftSize = 1024;
      source.connect(this.analyser);
      this.analyser.connect(this.ctx.destination);
      this.samples = new Float32Array(this.analyser.fftSize);
    } catch { this.ctx = null; this.analyser = null; }
  }

  private meter(gen: number) {
    if (gen !== this.gen || !this.analyser || !this.samples) return;
    this.analyser.getFloatTimeDomainData(this.samples);
    let sum = 0;
    for (const s of this.samples) sum += s * s;
    this.level = Math.min(0.3, Math.sqrt(sum / this.samples.length));
    this.emit();
    this.frame = requestAnimationFrame(() => this.meter(gen));
  }

  private sayLocal(pieces: string[], gen: number) {
    const synth = typeof window !== 'undefined' ? window.speechSynthesis : undefined;
    const voice = synth ? pickLocalVoice(synth.getVoices(), navigator.language || 'en') : undefined;
    if (!synth || !voice) {
      this.error = this.error || 'TalkForge is not reachable and no voice is installed on this computer; the answer is shown as text.';
      this.speaking = false; this.emit();
      return;
    }
    this.engine = 'local';
    pieces.forEach((text, i) => {
      const u = new SpeechSynthesisUtterance(text);
      u.voice = voice as SpeechSynthesisVoice;
      u.lang = voice.lang;
      u.onstart = () => { if (gen === this.gen) { this.level = 0.18; this.emit(); } };
      if (i === pieces.length - 1) u.onend = u.onerror = () => { if (gen === this.gen) { this.speaking = false; this.level = 0; this.emit(); } };
      synth.speak(u);
    });
  }
}

export const speech = new SpeechPlayer();
