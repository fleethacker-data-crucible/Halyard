// The keyboard shortcuts Halyard answers to, listed once: the ? help dialog shows this table, the command palette shows the
// same keys, and useShortcuts (lib/use-shortcuts.ts) acts on them. UNIFIED-UI 7: every shortcut is listed under ?.
import type { Route } from '../app';

export interface Shortcut {
  keys: string[];
  what: string;
}

/** "g" then a letter goes to a page, as in GitHub and Gmail. */
export const GO_KEYS: Record<string, Route> = {
  o: 'overview', l: 'leadership', v: 'vision', i: 'intake', h: 'hierarchy', r: 'review', p: 'proposals', n: 'plan',
  b: 'sandbox', c: 'connection', d: 'diagnostics', s: 'settings',
};

export const SHORTCUTS: Array<{ group: string; items: Shortcut[] }> = [
  {
    group: 'Anywhere',
    items: [
      { keys: ['Ctrl', 'K'], what: 'Open the command palette: every page and action' },
      { keys: ['?'], what: 'Open this help' },
      { keys: ['.'], what: 'Talk to the crew (opens the panel and listens)' },
      { keys: ['Esc'], what: 'Close the top dialog or panel' },
    ],
  },
  {
    group: 'Go to a page (press g, then the letter)',
    items: Object.entries(GO_KEYS).map(([k, route]) => ({ keys: ['g', k], what: route.charAt(0).toUpperCase() + route.slice(1) })),
  },
  {
    group: 'In dialogs, lists and the tree',
    items: [
      { keys: ['Tab'], what: 'Next control; focus stays inside a dialog' },
      { keys: ['↑', '↓'], what: 'Move through the command palette, a menu or the hierarchy tree' },
      { keys: ['Enter'], what: 'Run the highlighted command, or open the selected item' },
    ],
  },
];

/** Shortcut keys as one string for a tooltip or the palette: ['Ctrl','K'] -> "Ctrl+K", ['g','o'] -> "g o". */
export function keysText(keys: string[]): string {
  return keys.length === 2 && keys[0] === 'g' ? keys.join(' ') : keys.join('+');
}

export function isTyping(t: EventTarget | null): boolean {
  const el = t as HTMLElement | null;
  if (!el || !el.tagName) return false;
  return el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable;
}

export type ShortcutAction = { kind: 'palette' } | { kind: 'help' } | { kind: 'talk' } | { kind: 'go'; route: Route } | { kind: 'prefix' } | null;

/**
 * What a key press means, given whether the previous key was "g" (pendingGo) and whether the person is typing in a field or a
 * dialog is open. Only Ctrl+K works while typing; nothing but Ctrl+K works while a dialog is open. Pure, so it is tested.
 */
export function shortcutFor(e: { key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey?: boolean }, state: { typing: boolean; dialogOpen: boolean; pendingGo: boolean }): ShortcutAction {
  const mod = e.ctrlKey || e.metaKey;
  if (mod && !e.altKey && e.key.toLowerCase() === 'k') return state.dialogOpen ? null : { kind: 'palette' };
  if (state.typing || state.dialogOpen || mod || e.altKey) return null;
  if (state.pendingGo) {
    const route = GO_KEYS[e.key.toLowerCase()];
    return route ? { kind: 'go', route } : null;
  }
  if (e.key === '?') return { kind: 'help' };
  if (e.key === '.') return { kind: 'talk' };
  if (e.key === 'g') return { kind: 'prefix' };
  return null;
}
