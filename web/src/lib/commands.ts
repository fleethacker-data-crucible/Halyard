// Commands for the Ctrl+K palette: fuzzy matching and local recency. Pure apart from the recency store, so it is tested.
import { readLocal, writeLocal } from './storage';

export type CommandGroup = 'Crew' | 'Go to' | 'Backlog' | 'Session' | 'View';
export const GROUP_ORDER: CommandGroup[] = ['Crew', 'Go to', 'Backlog', 'View', 'Session'];

export interface Command {
  id: string;
  label: string;
  group: CommandGroup;
  /** Extra words that should find it ("sync", "pull"). */
  keywords?: string;
  keys?: string[];
  run: () => void;
}

/**
 * A score for how well the query matches: every query character must appear in order. Matches at the start of a word and runs
 * of consecutive characters score higher. -1 when it does not match.
 */
export function fuzzyScore(query: string, text: string): number {
  const q = query.trim().toLowerCase();
  if (!q) return 0;
  const t = text.toLowerCase();
  if (t.includes(q)) return 100 - t.indexOf(q) + (t.startsWith(q) ? 50 : 0) + q.length * 2;
  let score = 0, at = 0, run = 0;
  for (const ch of q) {
    if (ch === ' ') continue;
    const i = t.indexOf(ch, at);
    if (i < 0) return -1;
    const wordStart = i === 0 || /[\s\-:/]/.test(t[i - 1] ?? '');
    run = i === at ? run + 1 : 0;
    score += 1 + (wordStart ? 6 : 0) + run * 2;
    at = i + 1;
  }
  return score;
}

const RECENT_KEY = 'palette-recent';

export function recentCommands(): string[] {
  const r = readLocal<unknown>(RECENT_KEY, []);
  return Array.isArray(r) ? r.filter((x): x is string => typeof x === 'string').slice(0, 5) : [];
}

export function noteCommand(id: string) {
  writeLocal(RECENT_KEY, [id, ...recentCommands().filter((x) => x !== id)].slice(0, 5));
}

/** With a query: the matches, best first. Without one: recent first, then by group. */
export function rankCommands(commands: Command[], query: string, recent: string[] = []): Command[] {
  if (!query.trim()) {
    const rec = recent.map((id) => commands.find((c) => c.id === id)).filter((c): c is Command => !!c);
    const rest = commands.filter((c) => !recent.includes(c.id)).sort((a, b) => GROUP_ORDER.indexOf(a.group) - GROUP_ORDER.indexOf(b.group));
    return [...rec, ...rest];
  }
  return commands
    .map((c) => ({ c, s: Math.max(fuzzyScore(query, c.label), fuzzyScore(query, c.keywords ?? '') - 5, fuzzyScore(query, `${c.group} ${c.label}`) - 10) }))
    .filter((x) => x.s >= 0)
    .sort((a, b) => b.s - a.s || (recent.includes(b.c.id) ? 1 : 0) - (recent.includes(a.c.id) ? 1 : 0))
    .map((x) => x.c);
}
