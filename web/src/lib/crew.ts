// The crew (agentic product owner and scrum master): wire types, the orb state it shows, and the text it speaks.
// Pure functions, no DOM and no network, so they are tested without a browser.

export type CrewCall = { command: string; method: string; path: string; status: number; outcome: 'ran' | 'refused' | 'failed' | string; note: string };
export type CrewTurn = { answer: string; calls: CrewCall[]; batches: string[]; source: 'fairlead' | 'brief' | string };
export type CrewMessage = { role: 'user' | 'assistant'; text: string; turn?: CrewTurn };

export type ScrumItem = { key: string; number: number; title: string; owner?: string | null; reason: string; ageDays: number; url?: string | null };
export type NextAction = { title: string; why: string; command: string; priority: number };
export type Ceremony = { id: string; name: string; at: string; agenda: string; command: string };
export type ScrumBrief = {
  currentSprint: string; daysElapsed: number; daysLeft: number; pointsPlanned: number; pointsDone: number; pointsExpected: number; onPace: boolean;
  blockers: ScrumItem[]; stale: ScrumItem[]; unowned: ScrumItem[]; refinement: ScrumItem[];
  risks: string[]; retroPrompts: string[]; ceremonies: Ceremony[]; actions: NextAction[]; spoken: string;
};

export type VoiceStatus = {
  speech: { state: 'ready' | 'starting' | 'offline' | 'missing'; ready: boolean; message: string; port: number; voice?: string | null };
  dictation: { ready: boolean; missing: string[]; message: string };
};

export type TalkForgeVoices = {
  found: boolean;
  defaultVoice: string;
  message: string;
  voices: Array<{ id: string; quality: string; speakers: number; language: string; default: boolean }>;
};

/** Binnacle voice states (vendor/binnacle README). */
export type OrbState = 'idle' | 'listening' | 'thinking' | 'speaking' | 'approval';

/**
 * What the orb shows, most urgent first: the microphone, then the crew working, then speech, then proposals waiting for a
 * person (approval), else idle. The orb is told a state and a level, never the conversation.
 */
export function orbState(s: { listening: boolean; transcribing: boolean; busy: boolean; speaking: boolean; awaitingApproval: boolean }): OrbState {
  if (s.listening) return 'listening';
  if (s.transcribing || s.busy) return 'thinking';
  if (s.speaking) return 'speaking';
  if (s.awaitingApproval) return 'approval';
  return 'idle';
}

export function orbCaption(state: OrbState, batches: number): string {
  switch (state) {
    case 'listening': return 'Listening';
    case 'thinking': return 'Working the backlog';
    case 'approval': return batches === 1 ? '1 batch to review' : `${batches} batches to review`;
    default: return '';
  }
}

/** Longest piece sent in one speech request; a longer run with no sentence end is cut at a space. */
export const MAX_PIECE = 400;

/** Markdown and link noise read out loud is useless: keep the words. */
export function cleanForSpeech(text: string): string {
  return text
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/https?:\/\/\S+/g, 'link')
    .replace(/#(\d+)/g, 'number $1')
    .replace(/^\s{0,3}(#{1,6}|[-*+]|\d+[.)])\s+/gm, '')
    .replace(/[*_~>|]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

const speakable = (s: string) => /[\p{L}\p{N}]/u.test(s);

/** Splits text into speakable pieces: one sentence (or line) each, at most MAX_PIECE characters, never cutting a word. */
export function speechPieces(text: string): string[] {
  const pieces: string[] = [];
  let rest = text.replace(/```[\s\S]*?```/g, ' ');
  const push = (raw: string) => { const c = cleanForSpeech(raw); if (speakable(c)) pieces.push(c); };
  for (;;) {
    const m = /[.!?]+["')\]]*\s|\n/.exec(rest);
    if (m && m.index + m[0].length <= MAX_PIECE) { push(rest.slice(0, m.index + m[0].length)); rest = rest.slice(m.index + m[0].length); continue; }
    if (rest.length > MAX_PIECE) {
      const space = rest.lastIndexOf(' ', MAX_PIECE);
      const cut = space > MAX_PIECE / 2 ? space + 1 : MAX_PIECE;
      push(rest.slice(0, cut)); rest = rest.slice(cut); continue;
    }
    break;
  }
  push(rest);
  return pieces;
}

/** The history the engine is sent back (it keeps none): the last turns, text only. */
export function historyForEngine(messages: CrewMessage[], max = 10): Array<{ role: string; text: string }> {
  return messages.slice(-max).map((m) => ({ role: m.role, text: m.text }));
}

/** Quick prompts the panel offers; each is plain words, so they work the same typed or spoken. */
export const CREW_PROMPTS = [
  'Run standup',
  'What should I do next?',
  'Get the backlog ready',
  'Plan the next sprint',
  'What are the risks?',
  'Prepare the retro',
] as const;
