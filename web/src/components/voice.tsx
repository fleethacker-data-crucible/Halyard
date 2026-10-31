import { useEffect, useRef, useState } from 'react';
import { LuMic, LuSquare, LuVolume2 } from 'react-icons/lu';
import { Button, Field, Notice, Section, useToast } from './ui';
import { describeError, post } from '../lib/api';
import { useJob } from '../lib/jobs';
import { briefText, chunkForSpeech, dictationSupport, handoffFromDictation, pickLocalVoice } from '../lib/voice';
import type { Leadership } from '../lib/schemas';
import { invalidate } from '../lib/useResource';

/** Reads the leadership brief aloud with a voice installed on this machine. A cloud voice is never used; without a local voice the button says why. */
export function ReadAloud({ data }: { data: Leadership | undefined }) {
  const [speaking, setSpeaking] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const synth = typeof window !== 'undefined' ? window.speechSynthesis : undefined;
  useEffect(() => () => synth?.cancel(), [synth]);
  if (!synth) return null;

  const stop = () => { synth.cancel(); setSpeaking(false); };
  const start = () => {
    if (!data) return;
    const voice = pickLocalVoice(synth.getVoices(), navigator.language || 'en');
    if (!voice) { setProblem('No voice is installed on this computer for your language, and Halyard does not use online voices. Add one in Windows Settings > Time & language > Speech.'); return; }
    setProblem(null);
    synth.cancel();
    const chunks = chunkForSpeech(briefText(data));
    chunks.forEach((text, i) => {
      const u = new SpeechSynthesisUtterance(text);
      u.voice = voice as SpeechSynthesisVoice;
      u.lang = voice.lang;
      if (i === chunks.length - 1) { u.onend = () => setSpeaking(false); u.onerror = () => setSpeaking(false); }
      synth.speak(u);
    });
    setSpeaking(true);
  };
  return (
    <>
      <Button onClick={speaking ? stop : start} disabled={!data} title="Read the leadership brief aloud with a voice installed on this computer">
        {speaking ? <><LuSquare size={14} />Stop</> : <><LuVolume2 size={14} />Read aloud</>}
      </Button>
      {problem ? <span role="alert" className="text-[12px] text-err">{problem}</span> : null}
    </>
  );
}

type Recognition = { start: () => void; stop: () => void; lang: string; continuous: boolean; interimResults: boolean; processLocally?: boolean; onresult: ((e: { results: ArrayLike<ArrayLike<{ transcript: string }>> }) => void) | null; onerror: ((e: { error: string }) => void) | null; onend: (() => void) | null };

/**
 * Dictate a backlog note. Typing always works. The microphone is offered only when the browser can recognise speech on this computer;
 * otherwise the panel says so. The note goes through the same preview and copy-into-sandbox path as any other hand-over.
 */
export function DictationPanel({ onCopied }: { onCopied: () => void }) {
  const toast = useToast();
  const [text, setText] = useState('');
  const [listening, setListening] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ count: number; alreadyCopied: number } | null>(null);
  const recognition = useRef<Recognition | null>(null);
  const support = typeof window === 'undefined' ? { supported: false, reason: '' } : dictationSupport(window as unknown as { SpeechRecognition?: unknown; webkitSpeechRecognition?: unknown });
  const job = useJob((j) => {
    if (j.state === 'succeeded') { toast.push('ok', 'Copied into the sandbox for review.'); setText(''); setPreview(null); invalidate('/api/snapshot'); onCopied(); }
    else if (j.state === 'failed') toast.push('err', j.error ?? 'Copy failed');
  });
  useEffect(() => () => recognition.current?.stop(), []);

  const listen = () => {
    const w = window as unknown as { SpeechRecognition?: new () => Recognition; webkitSpeechRecognition?: new () => Recognition };
    const Ctor = w.SpeechRecognition ?? w.webkitSpeechRecognition;
    if (!Ctor) return;
    const r = new Ctor();
    r.lang = navigator.language || 'en-US';
    r.continuous = true;
    r.interimResults = false;
    r.processLocally = true;
    r.onresult = (e) => setText((t) => (t ? t + '\n' : '') + Array.from(e.results).map((x) => x[0]?.transcript ?? "").join(' ').trim());
    r.onerror = (e) => { setNote(e.error === 'language-not-supported' || e.error === 'service-not-allowed' ? 'Speech recognition is not available on this computer for your language; type the note instead.' : `Dictation stopped: ${e.error}.`); setListening(false); };
    r.onend = () => setListening(false);
    recognition.current = r;
    setNote(null);
    try { r.start(); setListening(true); } catch (e) { setNote(describeError(e)); }
  };
  const stop = () => { recognition.current?.stop(); setListening(false); };

  const check = async () => {
    try {
      const r = await post<{ count: number; alreadyCopied: number }>('/api/intake/handoff/preview', { document: handoffFromDictation(text) });
      setPreview({ count: r.count, alreadyCopied: r.alreadyCopied });
    } catch (e) { toast.push('err', describeError(e)); }
  };

  return (
    <Section title="Dictate or type a backlog note" aside={support.supported ? <Button size="sm" variant="quiet" onClick={listening ? stop : listen}>{listening ? <><LuSquare size={13} />Stop</> : <><LuMic size={13} />Dictate</>}</Button> : null}>
      <div className="flex flex-col gap-2 px-4 py-3">
        {!support.supported ? <Notice tone="info">{support.reason}</Notice> : null}
        {note ? <Notice tone="warn">{note}</Notice> : null}
        <Field label="Note" hint="One item per line; the first sentence is the title.">
          <textarea className="min-h-24 w-full rounded-ctl border border-line bg-surface p-2" value={text} onChange={(e) => { setText(e.target.value); setPreview(null); }} spellCheck />
        </Field>
        <div className="flex items-center gap-2">
          <Button onClick={() => void check()} disabled={!text.trim()}>Preview drafts</Button>
          {preview ? <Button variant="primary" busy={job.running} onClick={() => void job.start('/api/intake/handoff/run', { document: handoffFromDictation(text) })} disabled={preview.count === 0}>Copy {preview.count} into the sandbox</Button> : null}
          {preview ? <span className="text-[12px] text-ink-3">{preview.alreadyCopied} already copied</span> : null}
        </div>
        {job.error ? <Notice tone="err">{job.error}</Notice> : null}
      </div>
    </Section>
  );
}
