// Settings, Voice: the crew's TalkForge voice, as Lazarette's Settings, Assistant has it. Pick an installed voice, turn spoken
// answers on or off, and hear a test sentence. The choice is kept in this browser (it is a per-person preference).
import { useEffect, useState, useSyncExternalStore } from 'react';
import { LuVolume2 } from 'react-icons/lu';
import { describeError, get } from '../lib/api';
import type { TalkForgeVoices, VoiceStatus } from '../lib/crew';
import { speech, VOICE_KEY } from '../lib/speech';
import { useLocalState } from '../lib/storage';
import { Button, Checkbox, Field, Notice, Pill, Section, Select } from './ui';

const TEST_SENTENCE = 'This is the Halyard crew. Two blockers need an owner before standup.';

export function VoiceSettings() {
  const [voices, setVoices] = useState<TalkForgeVoices | null>(null);
  const [status, setStatus] = useState<VoiceStatus | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [voice, setVoice] = useLocalState(VOICE_KEY, '');
  const [speakAnswers, setSpeakAnswers] = useLocalState('crew-speak-answers', false);
  const [voiceInput, setVoiceInput] = useLocalState('crew-voice-input', true);
  const [autoSend, setAutoSend] = useLocalState('crew-auto-send', true);
  const speaking = useSyncExternalStore((f) => speech.subscribe(f), () => speech.speaking);
  const preparing = useSyncExternalStore((f) => speech.subscribe(f), () => speech.preparing);
  const speechError = useSyncExternalStore((f) => speech.subscribe(f), () => speech.error);

  const load = async () => {
    try {
      const [v, s] = await Promise.all([get<TalkForgeVoices>('/api/voice/voices'), get<VoiceStatus>('/api/voice/status')]);
      setVoices(v); setStatus(s); setProblem(null);
    } catch (e) { setProblem(describeError(e)); }
  };
  useEffect(() => { void load(); }, []);
  useEffect(() => { if (!preparing) void load(); }, [preparing]);

  const state = status?.speech.state;
  const tone = state === 'ready' ? 'ok' : state === 'starting' ? 'info' : 'warn';
  const label = state === 'ready' ? 'running' : state === 'starting' ? 'starting' : state === 'missing' ? 'not found' : 'starts when needed';
  return (
    <Section title="Voice (TalkForge)" aside={status ? <Pill tone={tone}>{label}</Pill> : null}>
      <div className="flex flex-col gap-3 px-4 py-3 text-[13px]">
        <p className="text-ink-2">The crew speaks with TalkForge's neural voices on this computer, the same voices as Lazarette. Halyard starts its own TalkForge when it needs one and stops it when it closes.</p>
        {voices && voices.voices.length > 0 ? (
          <Field label="Voice">
            <Select value={voice} onChange={(e) => setVoice(e.target.value)} aria-label="TalkForge voice">
              <option value="">TalkForge's default{voices.defaultVoice ? ` (${voices.defaultVoice})` : ''}</option>
              {voices.voices.map((v) => <option key={v.id} value={v.id}>{v.id}{v.language ? ` · ${v.language}` : ''}{v.speakers > 1 ? ` · ${v.speakers} speakers` : ''}</option>)}
            </Select>
          </Field>
        ) : null}
        {voices && voices.message ? <Notice tone="warn">{voices.message}</Notice> : null}
        <Checkbox label="Speak the crew's answers (a question asked by voice is always answered aloud)" checked={speakAnswers} onChange={(e) => setSpeakAnswers(e.target.checked)} />
        <Checkbox label="Voice input: the orb listens when you click or hold it (off: it opens the message box)" checked={voiceInput} onChange={(e) => setVoiceInput(e.target.checked)} />
        <Checkbox label="Send when I stop talking (off: what you said waits in the box for you to send)" checked={autoSend} disabled={!voiceInput} onChange={(e) => setAutoSend(e.target.checked)} />
        <div className="flex flex-wrap items-center gap-2">
          <Button onClick={() => (speaking ? speech.stop() : void speech.say(TEST_SENTENCE))} disabled={!voices?.found || voices.voices.length === 0} busy={preparing}>
            <LuVolume2 size={14} />{speaking ? 'Stop' : preparing ? 'Getting the voice ready' : 'Test voice'}
          </Button>
          {status && state !== 'ready' && state !== 'starting' && !voices?.message ? <span className="text-[12px] text-ink-3">{status.speech.message}</span> : null}
        </div>
        {speechError ? <Notice tone="err">{speechError}</Notice> : null}
        {problem ? <Notice tone="err">{problem}</Notice> : null}
      </div>
    </Section>
  );
}
