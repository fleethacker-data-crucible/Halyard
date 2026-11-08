// The crew: Halyard's agentic product owner and scrum master, reached through the Binnacle orb (vendor/binnacle, the same
// orb Lazarette and Semaphore use, mounted the way they mount it: 130 px, no menu on hover, look, theme and talk mode chosen
// in Settings > Appearance, and it can be turned off there). Click the orb (or press and hold it) to talk: Whisper in the
// page, from TalkForge's files. Or type in the panel; answers are spoken through TalkForge, as in Lazarette. The crew reads
// and proposes; approving and applying stay on the Proposals page, and the orb turns to "approval" while a batch it made
// is waiting.
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { LuMic, LuSend, LuSquare, LuVolume2, LuVolumeX, LuX } from 'react-icons/lu';
import { describeError, get, post } from '../lib/api';
import { CREW_PROMPTS, historyForEngine, orbCaption, orbState, type CrewMessage, type CrewTurn, type ScrumBrief } from '../lib/crew';
import { dictation } from '../lib/dictation';
import { speech } from '../lib/speech';
import { HOLD_MS, orbGesture, useOrbPrefs } from '../lib/orb-prefs';
import { readLocal, useLocalState } from '../lib/storage';
import { takeCrewRequest } from '../lib/welcome';
import { Button, Pill, cn } from './ui';

type BinnacleOrb = { el: HTMLElement; setLook: (l: string) => void; setTheme: (t: string) => void; destroy: () => void };
type BinnacleApi = {
  mount: (o: Record<string, unknown>) => BinnacleOrb;
  voice: { set: (state: string | null, level?: number, text?: string) => void; setLevel: (level: number) => void };
};
declare global { interface Window { Binnacle?: BinnacleApi } }

function useExternal<T>(store: { subscribe: (fn: () => void) => () => void }, read: () => T): T {
  return useSyncExternalStore(store.subscribe.bind(store), read, read);
}

export function Crew() {
  const [open, setOpen] = useState(false);
  const [messages, setMessages] = useState<CrewMessage[]>([]);
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  // Spoken answers are off unless turned on here (as in Lazarette); a question asked by voice is always answered aloud.
  const [speak, setSpeak] = useLocalState('crew-speak-answers', false);
  const [pending, setPending] = useState<string[]>([]);
  const [brief, setBrief] = useState<ScrumBrief | null>(null);
  const [briefError, setBriefError] = useState<string | null>(null);
  const orbRef = useRef<BinnacleOrb | null>(null);
  const [hasOrb, setHasOrb] = useState(false);
  const prefs = useOrbPrefs();
  const talkModeRef = useRef(prefs.talk);
  talkModeRef.current = prefs.talk;
  const [holding, setHolding] = useState(false);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);
  const draftRef = useRef('');
  draftRef.current = draft;

  const micState = useExternal(dictation, () => dictation.state);
  const micLevel = useExternal(dictation, () => dictation.level);
  const micError = useExternal(dictation, () => dictation.error);
  const speaking = useExternal(speech, () => speech.speaking);
  const speechLevel = useExternal(speech, () => speech.level);
  const speechError = useExternal(speech, () => speech.error);
  const voicePreparing = useExternal(speech, () => speech.preparing);

  const loadBrief = useCallback(async () => {
    try { setBrief(await get<ScrumBrief>('/api/scrum/brief')); setBriefError(null); }
    catch (e) { setBrief(null); setBriefError(describeError(e)); }
  }, []);
  useEffect(() => { if (open) void loadBrief(); }, [open, loadBrief]);
  useEffect(() => { if (open && speak) speech.warmUp(); }, [open, speak]);

  const send = useCallback(async (text: string, spoken = false) => {
    const message = text.trim();
    if (!message || busy) return;
    speech.stop();
    setDraft('');
    const before = messages;
    setMessages([...before, { role: 'user', text: message }]);
    setBusy(true);
    try {
      const turn = await post<CrewTurn>('/api/agent/ask', { message, history: historyForEngine(before) });
      setMessages((m) => [...m, { role: 'assistant', text: turn.answer, turn }]);
      if (turn.batches.length) setPending((p) => Array.from(new Set([...p, ...turn.batches])));
      if (speak || spoken) void speech.say(turn.answer);
    } catch (e) {
      setMessages((m) => [...m, { role: 'assistant', text: `I could not answer: ${describeError(e)}` }]);
    } finally {
      setBusy(false);
    }
  }, [busy, messages, speak]);
  const sendRef = useRef(send);
  sendRef.current = send;

  const talk = useCallback(async () => {
    if (dictation.active) { dictation.stop(true); return; }
    speech.stop();
    setOpen(true);
    // Settings, Voice: with voice input off the orb just opens the panel and puts the cursor in the message box.
    if (!readLocal('crew-voice-input', true)) { setTimeout(() => inputRef.current?.focus(), 0); return; }
    speech.warmUp();   // a question asked by voice is answered aloud: start TalkForge while the person talks
    const before = draftRef.current.trim();
    const autoSend = readLocal('crew-auto-send', true);
    const ok = await dictation.start({
      onText: (t) => setDraft(before ? `${before} ${t}` : t),
      onDone: (t) => {
        const text = before ? `${before} ${t}` : t;
        if (autoSend) void sendRef.current(text, true);
        else { setDraft(text); inputRef.current?.focus(); }
      },
    });
    if (!ok) inputRef.current?.focus();
  }, []);
  const talkRef = useRef(talk);
  talkRef.current = talk;

  // The orb: mounted while it is turned on, told only a state, a level and a short caption.
  useEffect(() => {
    const B = window.Binnacle;
    if (!B || !prefs.on) { setHasOrb(false); return; }
    // Left of the panel so it never covers the message box; the person can drag it anywhere (Binnacle remembers).
    const orb = B.mount({
      id: 'halyard-crew', name: 'Halyard crew', look: prefs.look, theme: prefs.theme, size: 130, zIndex: 40,
      picker: false,   // no menu on hover; the choices are in Settings > Appearance
      x: Math.max(120, window.innerWidth - 440 - 110), y: window.innerHeight - 110,
      onActivate: () => {
        // A click (or Enter). With "press and hold" a quick click only says how.
        if (talkModeRef.current !== 'click') { B.voice.set(null, 0, 'Press and hold to talk'); return; }
        void talkRef.current();
      },
    });
    orbRef.current = orb;
    // The orb remembers its own last look; Settings is the one place that decides it.
    orb.setLook(prefs.look);
    orb.setTheme(prefs.theme);
    setHasOrb(true);

    // Press and hold: held still for HOLD_MS starts listening; letting go sends (or fills the box, per Settings).
    const hit = orb.el.querySelector<HTMLElement>('.binnacle-hit');
    let timer: ReturnType<typeof setTimeout> | undefined;
    let start: { x: number; y: number } | null = null;
    let held = false;
    const down = (e: PointerEvent) => {
      if (talkModeRef.current !== 'hold' || e.button !== 0) return;
      start = { x: e.clientX, y: e.clientY };
      timer = setTimeout(() => { held = true; setHolding(true); void talkRef.current(); }, HOLD_MS);
    };
    const move = (e: PointerEvent) => {
      if (start && orbGesture(start, { x: e.clientX, y: e.clientY }, 0) === 'drag') { clearTimeout(timer); start = null; }
    };
    const up = () => {
      clearTimeout(timer);
      start = null;
      if (held) { held = false; setHolding(false); if (dictation.active) dictation.stop(true); else inputRef.current?.focus(); }
    };
    hit?.addEventListener('pointerdown', down);
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', up);
    return () => {
      clearTimeout(timer);
      hit?.removeEventListener('pointerdown', down);
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', up);
      orb.destroy();
      orbRef.current = null;
      B.voice.set('idle', 0);
    };
    // Re-mounted only when the orb is turned on or off; look and theme changes go through the effects below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [prefs.on]);
  useEffect(() => { orbRef.current?.setLook(prefs.look); }, [prefs.look]);
  useEffect(() => { orbRef.current?.setTheme(prefs.theme); }, [prefs.theme]);
  useEffect(() => {
    const B = window.Binnacle;
    if (!B) return;
    // As in Lazarette and Semaphore: the microphone first, with its own captions, then the hold, the voice warming up,
    // speech, and the crew's own states (working, batches waiting for approval).
    if (micState === 'preparing') { B.voice.set('thinking', 0, 'Getting the microphone ready'); return; }
    if (micState === 'transcribing') { B.voice.set('thinking', 0, 'Writing it down'); return; }
    if (holding && micState !== 'listening') { B.voice.set('listening', 0, 'Listening'); return; }
    const state = orbState({ listening: micState === 'listening', transcribing: voicePreparing, busy, speaking, awaitingApproval: pending.length > 0 });
    const level = state === 'listening' ? micLevel : state === 'speaking' ? speechLevel : 0;
    B.voice.set(state, level, (voicePreparing && !busy ? 'Getting the voice ready' : orbCaption(state, pending.length)) || undefined);
  }, [micState, micLevel, busy, speaking, speechLevel, pending.length, voicePreparing, holding, hasOrb]);

  // The welcome page (and anything else) can open the panel, optionally with a message: lib/welcome.ts openCrew.
  useEffect(() => {
    const onOpen = () => {
      const request = takeCrewRequest();
      if (!request) return;
      setOpen(true);
      if (request.talk) setTimeout(() => void talkRef.current(), 0);
      else if (request.message) setTimeout(() => void sendRef.current(request.message!), 0);
    };
    onOpen();   // a request made just before this panel mounted
    window.addEventListener('halyard:crew', onOpen);
    return () => window.removeEventListener('halyard:crew', onOpen);
  }, []);

  // A batch stops waiting once someone opens it on the Proposals page.
  useEffect(() => {
    const onHash = () => {
      const m = /#\/proposals\?.*batch=([^&]+)/.exec(window.location.hash);
      if (m) setPending((p) => p.filter((b) => b !== decodeURIComponent(m[1]!)));
    };
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  if (!open) {
    // The orb is the way in; this button stands in where the orb is turned off or could not load.
    if (hasOrb) return null;
    return (
      <Button variant="quiet" className="fixed right-4 bottom-10 z-30 h-8 border border-line bg-surface text-[12px] text-ink-2" onClick={() => setOpen(true)} title="Open the crew: product owner and scrum master">
        Crew{pending.length ? <Pill tone="warn" className="ml-1">{pending.length}</Pill> : null}
      </Button>
    );
  }

  const problem = micError || speechError;
  return (
    <aside role="dialog" aria-label="Crew: product owner and scrum master" className="fixed right-4 bottom-10 z-30 flex max-h-[min(78vh,720px)] w-[min(440px,calc(100vw-2rem))] flex-col rounded-panel border border-line bg-surface shadow-lg">
      <header className="flex items-center gap-2 border-b border-line-2 px-3 py-2">
        <div className="min-w-0 flex-1">
          <div className="text-[14px] font-medium">Crew</div>
          <div className="text-[12px] text-ink-3">Product owner and scrum master. Reads and proposes; you approve.</div>
        </div>
        <Button variant="quiet" size="sm" onClick={() => { setSpeak(!speak); if (speak) speech.stop(); }} title={speak ? 'Stop speaking answers' : 'Speak answers'} aria-pressed={speak}>
          {speak ? <LuVolume2 size={15} /> : <LuVolumeX size={15} />}
        </Button>
        <Button variant="quiet" size="sm" onClick={() => { setOpen(false); speech.stop(); dictation.stop(false); }} title="Close"><LuX size={15} /></Button>
      </header>

      <div className="border-b border-line-2 px-3 py-2 text-[12.5px]">
        {brief ? (
          <div className="flex flex-col gap-1">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium">{brief.currentSprint}</span>
              <span className="text-ink-2">day {brief.daysElapsed}, {brief.daysLeft} left</span>
              <Pill tone={brief.onPace ? 'ok' : 'warn'}>{brief.pointsDone}/{brief.pointsPlanned} pts{brief.onPace ? '' : ' behind'}</Pill>
              {brief.blockers.length ? <Pill tone="err">{brief.blockers.length} blocked</Pill> : null}
              {brief.stale.length ? <Pill tone="warn">{brief.stale.length} stale</Pill> : null}
            </div>
            {brief.actions[0] ? <div className="text-ink-2">Next: <span className="text-ink">{brief.actions[0].title}</span> — {brief.actions[0].why}</div> : null}
            <div><Button variant="quiet" size="sm" className="h-6 px-1.5 text-[12px]" onClick={() => void speech.say(brief.spoken)}><LuVolume2 size={13} />Speak the brief</Button></div>
          </div>
        ) : <div className="text-ink-3">{briefError ?? 'Reading the sprint…'}</div>}
      </div>

      <div className="scroll-y flex min-h-[120px] flex-1 flex-col gap-2 px-3 py-2" aria-live="polite">
        {messages.length === 0 ? (
          <div className="flex flex-wrap gap-1.5">
            {CREW_PROMPTS.map((p) => <Button key={p} size="sm" variant="quiet" className="h-7 border border-line px-2 text-[12px]" onClick={() => void send(p)}>{p}</Button>)}
          </div>
        ) : null}
        {messages.map((m, i) => (
          <div key={i} className={cn('max-w-[92%] rounded-ctl px-2.5 py-1.5 text-[13px] whitespace-pre-wrap', m.role === 'user' ? 'self-end bg-surface-2' : 'self-start border border-line-2')}>
            {m.text}
            {m.turn && (m.turn.calls.length > 0 || m.turn.batches.length > 0) ? (
              <div className="mt-1.5 flex flex-wrap gap-1 text-[11.5px] text-ink-3">
                {m.turn.calls.map((c, j) => <span key={j} className={cn('mono rounded px-1', c.outcome === 'ran' ? 'bg-surface-2' : 'bg-warn-bg text-warn')} title={c.note}>{c.command.replace(/^halyard /, '')}{c.outcome === 'ran' ? '' : ` (${c.outcome})`}</span>)}
                {m.turn.batches.map((b) => <a key={b} className="rounded bg-info-bg px-1 text-info" href={`#/proposals?batch=${encodeURIComponent(b)}`}>Review batch {b}</a>)}
              </div>
            ) : null}
          </div>
        ))}
        {busy ? <div className="self-start text-[12px] text-ink-3">Working the backlog…</div> : null}
      </div>

      {problem ? <div role="alert" className="px-3 pb-1 text-[12px] text-err">{problem}</div> : null}
      <form className="flex items-end gap-1.5 border-t border-line-2 p-2" onSubmit={(e) => { e.preventDefault(); void send(draft); }}>
        <textarea
          ref={inputRef}
          className="min-h-9 flex-1 resize-none rounded-ctl border border-line bg-surface px-2 py-1.5 text-[13px]"
          rows={2}
          value={draft}
          placeholder={micState === 'listening' ? 'Listening…' : 'Ask the crew, or click the orb and talk'}
          aria-label="Message to the crew"
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void send(draft); } }}
        />
        <Button type="button" variant="quiet" size="sm" onClick={() => void talk()} title={micState === 'listening' ? 'Stop and send' : 'Talk'} aria-pressed={micState === 'listening'}>
          {micState === 'listening' ? <LuSquare size={15} /> : <LuMic size={15} />}
        </Button>
        <Button type="submit" size="sm" busy={busy} disabled={!draft.trim()} title="Send"><LuSend size={15} /></Button>
      </form>
    </aside>
  );
}
