// Voice helpers that work without any service: speech out with the browser's installed (local) voices only, speech in only when the
// browser can recognise locally. Nothing here sends audio or text anywhere. Pure functions so they can be tested without a browser.
import type { Leadership } from './schemas';

export type VoiceLike = { name: string; lang: string; localService: boolean; default?: boolean };

/** The best installed voice for the language, or undefined. A voice that is not local (a cloud voice) is never chosen. */
export function pickLocalVoice<T extends VoiceLike>(voices: readonly T[], lang = 'en'): T | undefined {
  const local = voices.filter((v) => v.localService);
  const base = lang.toLowerCase().split('-')[0];
  const sameLanguage = local.filter((v) => v.lang.toLowerCase().split(/[-_]/)[0] === base);
  return sameLanguage.find((v) => v.default) ?? sameLanguage[0];
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/** The leadership brief as plain sentences for a voice: counts and the oldest blockers, no symbols or tables. */
export function briefText(l: Leadership): string {
  const parts: string[] = [];
  parts.push(`Leadership brief for sprint ${l.currentSprint}.`);
  parts.push(`${l.featuresOnTrack} of ${plural(l.features.length, 'feature')} on track or done, ${l.featuresActive} still active.`);
  parts.push(`Average velocity is ${l.averageVelocity} points per sprint. The ready backlog holds ${plural(l.ready.stories, 'story', 'stories')}, ${plural(l.ready.points, 'point')}, which is ${l.ready.healthy ? 'healthy' : 'thin'}.`);
  if (l.capacity.configured) parts.push(`Capacity used is ${l.capacity.usedPercent} percent: ${l.capacity.plannedPoints} of ${l.capacity.teamCapacity} points planned.`);
  if (l.blockers.length === 0) parts.push('There are no open blockers.');
  else {
    const escalated = l.blockers.filter((b) => b.escalated).length;
    parts.push(`There ${l.blockers.length === 1 ? 'is' : 'are'} ${plural(l.blockers.length, 'open blocker')}${escalated ? `, ${escalated} escalated` : ''}.`);
    for (const b of [...l.blockers].sort((a, c) => c.ageDays - a.ageDays).slice(0, 3)) parts.push(`${b.title}, ${plural(b.ageDays, 'day')} old.`);
  }
  for (const n of l.notes) parts.push(n);
  return parts.join(' ');
}

/** Splits text into pieces a speech engine handles reliably (some stop on long utterances). Never cuts a word. */
export function chunkForSpeech(text: string, max = 220): string[] {
  const sentences = text.replace(/\s+/g, ' ').trim().match(/[^.!?]+[.!?]*/g) ?? [];
  const chunks: string[] = [];
  let current = '';
  for (const raw of sentences) {
    const s = raw.trim();
    if (!s) continue;
    if (current && (current + ' ' + s).length > max) { chunks.push(current); current = ''; }
    if (s.length > max) {
      for (const word of s.split(' ')) {
        if (current && (current + ' ' + word).length > max) { chunks.push(current); current = word; } else current = current ? current + ' ' + word : word;
      }
    } else current = current ? current + ' ' + s : s;
  }
  if (current) chunks.push(current);
  return chunks;
}

export type Dictation = { supported: boolean; reason: string };

/**
 * Speech in is offered only when the browser can run recognition on this machine (`processLocally`). The usual browser recogniser
 * sends audio to a cloud service, so without that capability dictation stays off and typing is the way in.
 */
export function dictationSupport(scope: { SpeechRecognition?: unknown; webkitSpeechRecognition?: unknown }): Dictation {
  const Ctor = (scope.SpeechRecognition ?? scope.webkitSpeechRecognition) as { prototype?: Record<string, unknown> } | undefined;
  if (!Ctor) return { supported: false, reason: 'This browser has no speech recognition. Type the note instead, or use Windows voice typing (Win+H) if your organisation allows it.' };
  if (!Ctor.prototype || !('processLocally' in Ctor.prototype)) return { supported: false, reason: 'This browser can only recognise speech through an online service, which Halyard does not use. Type the note instead, or use Windows voice typing (Win+H) if your organisation allows it.' };
  return { supported: true, reason: '' };
}

/** A dictated note as a fleet-handoff document (see docs/HANDOFF.md): one item per non-empty line, the first sentence as the title. */
export function handoffFromDictation(text: string, now = new Date()) {
  const lines = text.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  const stamp = now.toISOString().replace(/[-:.TZ]/g, '').slice(0, 14);
  return {
    format: 'fleet-handoff',
    version: 1,
    source: { app: 'dictation', exportedAt: now.toISOString() },
    items: lines.slice(0, 50).map((line, i) => {
      const firstStop = line.search(/[.!?](\s|$)/);
      const title = (firstStop > 0 && firstStop < 200 ? line.slice(0, firstStop) : line).slice(0, 200);
      const body = line.length > title.length ? line.slice(title.length).replace(/^[.!?\s]+/, '') : '';
      return { ref: `dictation-${stamp}-${i + 1}`, title, body };
    }),
  };
}
