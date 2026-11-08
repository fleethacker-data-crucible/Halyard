// Voice input for the crew. First choice: Whisper in a worker (workers/stt.worker.ts) with TalkForge's files, so the
// microphone audio never leaves this tab. Second: the browser's recogniser, but only when it runs on this computer
// (processLocally); a recogniser that streams audio to a cloud service is never used. Otherwise typing is the way in.
import { get } from './api';
import { cleanTranscript, createUtterance, downsample, rms } from './dictation-audio';
import type { VoiceStatus } from './crew';
import { dictationSupport } from './voice';

export type DictationState = 'idle' | 'preparing' | 'listening' | 'transcribing';
type Handlers = { onText: (text: string, final: boolean) => void; onDone: (text: string) => void };
type Listener = () => void;
type Recognition = { start: () => void; stop: () => void; abort: () => void; lang: string; continuous: boolean; interimResults: boolean; processLocally?: boolean; onresult: ((e: { results: ArrayLike<ArrayLike<{ transcript: string }> & { isFinal?: boolean }> }) => void) | null; onerror: ((e: { error: string }) => void) | null; onend: (() => void) | null };

export class Dictation {
  state: DictationState = 'idle';
  level = 0;
  error = '';
  engine: 'whisper' | 'browser' | '' = '';

  private listeners = new Set<Listener>();
  private worker: Worker | null = null;
  private seq = 0;
  private pending = new Map<number, { resolve: (t: string) => void; reject: (e: Error) => void }>();
  private stream: MediaStream | null = null;
  private ctx: AudioContext | null = null;
  private node: ScriptProcessorNode | null = null;
  private utterance: ReturnType<typeof createUtterance> | null = null;
  private handlers: Handlers | null = null;
  private recognition: Recognition | null = null;
  private run = 0;

  get active() { return this.state !== 'idle'; }
  subscribe(fn: Listener) { this.listeners.add(fn); return () => { this.listeners.delete(fn); }; }
  private set(patch: Partial<Pick<Dictation, 'state' | 'level' | 'error' | 'engine'>>) { Object.assign(this, patch); for (const fn of this.listeners) fn(); }

  async start(handlers: Handlers): Promise<boolean> {
    if (this.active) return true;
    this.handlers = handlers;
    const run = ++this.run;
    this.set({ state: 'preparing', error: '' });
    let status: VoiceStatus | null = null;
    try { status = await get<VoiceStatus>('/api/voice/status'); } catch { status = null; }
    if (run !== this.run) return false;
    if (status?.dictation.ready) return this.startWhisper(run);
    const local = dictationSupport(window as unknown as { SpeechRecognition?: unknown; webkitSpeechRecognition?: unknown });
    if (local.supported) return this.startBrowser(run);
    this.set({ state: 'idle', error: `${status?.dictation.message ?? 'Voice input is not available.'} ${local.reason}`.trim() });
    return false;
  }

  /** Ends listening: with send, what was said is transcribed and handed on; without, it is dropped. */
  stop(send: boolean) {
    if (this.recognition) { if (send) this.recognition.stop(); else { this.run++; this.recognition.abort(); this.reset(); } return; }
    if (this.state === 'listening') void this.finish(send);
    else if (this.state === 'preparing') { this.run++; this.reset(); }
  }

  private async startWhisper(run: number): Promise<boolean> {
    try {
      this.ensureWorker();
      void this.call({ type: 'load' }).catch(() => undefined);   // warm Whisper up while the person starts talking
      if (!navigator.mediaDevices?.getUserMedia) throw new Error('This browser does not offer a microphone to this page.');
      this.stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
      if (run !== this.run) { this.closeMic(); return false; }
      this.ctx = new AudioContext();
      const source = this.ctx.createMediaStreamSource(this.stream);
      this.node = this.ctx.createScriptProcessor(4096, 1, 1);
      this.utterance = createUtterance();
      const rate = this.ctx.sampleRate;
      this.node.onaudioprocess = (ev) => {
        if (run !== this.run || this.state !== 'listening') return;
        const raw = ev.inputBuffer.getChannelData(0);
        this.set({ level: Math.min(0.3, rms(raw) * 2) });
        if (this.utterance!.push(downsample(raw, rate)) === 'done') void this.finish(true);
      };
      source.connect(this.node);
      this.node.connect(this.ctx.destination);   // a ScriptProcessor only runs while connected; it outputs silence
      this.set({ state: 'listening', engine: 'whisper' });
      return true;
    } catch (e) {
      const blocked = e instanceof DOMException && e.name === 'NotAllowedError';
      this.reset();
      this.set({ error: blocked ? 'The microphone is blocked for Halyard. Allow it in the browser (the icon in the address bar), then try again.' : e instanceof Error ? e.message : String(e) });
      return false;
    }
  }

  private startBrowser(run: number): boolean {
    const w = window as unknown as { SpeechRecognition?: new () => Recognition; webkitSpeechRecognition?: new () => Recognition };
    const Ctor = (w.SpeechRecognition ?? w.webkitSpeechRecognition)!;
    const r = new Ctor();
    r.lang = navigator.language || 'en-US';
    r.continuous = false;
    r.interimResults = true;
    r.processLocally = true;
    let text = '';
    r.onresult = (e) => { text = Array.from(e.results).map((x) => x[0]?.transcript ?? '').join(' ').trim(); this.handlers?.onText(text, false); };
    r.onerror = (e) => { if (run === this.run) this.set({ error: e.error === 'not-allowed' ? 'The microphone is blocked for Halyard.' : `Voice input stopped: ${e.error}.` }); };
    r.onend = () => {
      if (run !== this.run) return;
      this.recognition = null;
      this.reset();
      if (text) { this.handlers?.onText(text, true); this.handlers?.onDone(text); }
    };
    this.recognition = r;
    try { r.start(); this.set({ state: 'listening', engine: 'browser' }); return true; } catch (e) { this.recognition = null; this.reset(); this.set({ error: e instanceof Error ? e.message : String(e) }); return false; }
  }

  private async finish(send: boolean) {
    const run = this.run;
    const u = this.utterance;
    this.closeMic();
    if (!send || !u || !u.heard) { this.reset(); return; }
    this.set({ state: 'transcribing' });
    try {
      const text = cleanTranscript(await this.call({ type: 'transcribe', audio: u.audio() }));
      if (run !== this.run) return;
      this.handlers?.onText(text, true);
      if (text) this.handlers?.onDone(text);
    } catch (e) {
      if (run === this.run) this.set({ error: e instanceof Error ? e.message : String(e) });
    } finally {
      if (run === this.run) this.reset();
    }
  }

  private ensureWorker() {
    if (this.worker) return;
    this.worker = new Worker('/assets/stt.worker.js', { type: 'module', name: 'halyard-stt' });
    this.worker.onmessage = (e: MessageEvent<{ type: string; id?: number; text?: string; message?: string }>) => {
      const m = e.data;
      const p = m.id !== undefined ? this.pending.get(m.id) : undefined;
      if (!p) return;
      this.pending.delete(m.id!);
      if (m.type === 'error') p.reject(new Error(`Speech-to-text failed: ${m.message}`));
      else p.resolve(m.text ?? '');
    };
  }

  private call(msg: { type: string; audio?: Float32Array }): Promise<string> {
    this.ensureWorker();
    const id = ++this.seq;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.worker!.postMessage({ ...msg, id }, msg.audio ? [msg.audio.buffer] : []);
    });
  }

  private closeMic() {
    if (this.node) { this.node.onaudioprocess = null; try { this.node.disconnect(); } catch { /* gone */ } }
    this.stream?.getTracks().forEach((t) => t.stop());
    void this.ctx?.close().catch(() => undefined);
    this.node = null; this.stream = null; this.ctx = null;
  }

  private reset() {
    this.closeMic();
    this.utterance = null;
    this.set({ state: 'idle', level: 0 });
  }
}

export const dictation = new Dictation();
