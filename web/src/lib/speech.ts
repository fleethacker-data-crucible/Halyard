// Spoken answers, the way Lazarette does them. Each piece of text goes to POST /api/voice/speak on this origin; the engine
// relays it to TalkForge on this computer (Piper neural voices) and returns WAV audio that plays from a blob: URL. When
// TalkForge is not running the engine starts it ("Getting the voice ready") and the piece is tried once more. One piece is
// fetched ahead while the current one plays, and the real audio level drives the orb. TalkForge or nothing: when it
// cannot start the answer stays text and the panel says why. No browser or cloud voice.
import { get, post, sessionToken } from './api';
import { speechPieces, type VoiceStatus } from './crew';
import { readLocal } from './storage';

/** The voice chosen in Settings (a TalkForge voice id), or '' for TalkForge's default. */
export const VOICE_KEY = 'crew-voice';
const START_WAIT_MS = 45_000;

type StartState = { state: string; message: string };
class Offline extends Error {}

type Listener = () => void;

export class SpeechPlayer {
  speaking = false;
  /** Loudness of the audio now playing, 0 to about 0.3 (speech RMS). */
  level = 0;
  error = '';
  /** True while TalkForge is being started; the orb says "Getting the voice ready". */
  preparing = false;

  private readying: Promise<boolean> | null = null;
  private listeners = new Set<Listener>();
  private gen = 0;
  private audio: HTMLAudioElement | null = null;
  private url = '';
  private ctx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private samples: Float32Array<ArrayBuffer> | null = null;
  private frame = 0;
  private abort: AbortController | null = null;

  subscribe(fn: Listener) { this.listeners.add(fn); return () => { this.listeners.delete(fn); }; }
  private emit() { for (const fn of this.listeners) fn(); }

  stop() {
    this.gen++;
    this.abort?.abort();
    this.abort = null;
    this.audio?.pause();
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
    this.abort = new AbortController();
    let next: Promise<Blob | null> = this.fetchPiece(pieces[0]!, this.abort.signal);
    for (let i = 0; i < pieces.length; i++) {
      const blob = await next;
      if (gen !== this.gen) return;
      if (!blob) {
        // TalkForge is not reachable: the answer stays text; this.error says why.
        this.speaking = false;
        this.level = 0;
        this.emit();
        return;
      }
      next = i + 1 < pieces.length ? this.fetchPiece(pieces[i + 1]!, this.abort.signal) : Promise.resolve(null);
      await this.play(blob, gen);
      if (gen !== this.gen) return;
    }
    this.speaking = false;
    this.level = 0;
    this.emit();
  }

  /** Starts TalkForge in the background so the first answer speaks sooner. Safe to call often: one start at a time. */
  warmUp() { void this.ensureReady(); }

  ensureReady(): Promise<boolean> {
    this.readying ??= this.ready().finally(() => { this.readying = null; this.preparing = false; this.emit(); });
    return this.readying;
  }

  private async ready(): Promise<boolean> {
    try {
      const status = await get<VoiceStatus>('/api/voice/status');
      if (status.speech.state === 'ready') return true;
      if (status.speech.state === 'missing') { this.error = status.speech.message; return false; }
      let s: StartState = status.speech.state === 'starting' ? status.speech : await post<StartState>('/api/voice/start', {});
      const until = Date.now() + START_WAIT_MS;
      while (s.state === 'starting' && Date.now() < until) {
        if (!this.preparing) { this.preparing = true; this.emit(); }
        await new Promise((r) => setTimeout(r, 600));
        s = (await get<VoiceStatus>('/api/voice/status')).speech;
      }
      if (s.state === 'ready') { this.error = ''; return true; }
      this.error = s.message || 'TalkForge is not running.';
      return false;
    } catch (e) {
      this.error = e instanceof Error ? e.message : String(e);
      return false;
    }
  }

  private async fetchPiece(text: string, signal: AbortSignal): Promise<Blob | null> {
    try {
      return await this.request(text, signal);
    } catch (e) {
      // TalkForge not running: start it, then try this piece once more.
      if (e instanceof Offline && !signal.aborted && (await this.ensureReady()) && !signal.aborted) {
        try { return await this.request(text, signal); } catch (e2) { e = e2; }
      }
      if (!signal.aborted && !this.error) this.error = e instanceof Error ? e.message : String(e);
      return null;
    }
  }

  private async request(text: string, signal: AbortSignal): Promise<Blob> {
    {
      const headers = new Headers({ 'Content-Type': 'application/json', Accept: 'audio/wav' });
      const token = sessionToken();
      if (token) headers.set('X-Halyard-Session', token);
      const voice = readLocal<string>(VOICE_KEY, '');
      const r = await fetch('/api/voice/speak', { method: 'POST', headers, body: JSON.stringify(voice ? { text, voice } : { text }), signal, cache: 'no-store' });
      if (r.status === 409) throw new Offline('TalkForge is not running.');
      if (!r.ok) {
        let message = `Speech failed with HTTP ${r.status}`;
        try { message = ((await r.json()) as { error?: string }).error ?? message; } catch { /* not json */ }
        throw new Error(message);
      }
      return await r.blob();
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
}


export const speech = new SpeechPlayer();
