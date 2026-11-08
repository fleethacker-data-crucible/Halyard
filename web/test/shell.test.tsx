import assert from 'node:assert/strict';
import { test } from 'node:test';
import { renderToStaticMarkup } from 'react-dom/server';
import { ErrorBoundary } from '../src/components/shell-dialogs';
import { fuzzyScore, rankCommands, type Command } from '../src/lib/commands';
import { GO_KEYS, SHORTCUTS, keysText, shortcutFor } from '../src/lib/shortcuts';

const key = (k: string, mods: Partial<{ ctrlKey: boolean; metaKey: boolean; altKey: boolean }> = {}) => ({ key: k, ctrlKey: false, metaKey: false, altKey: false, ...mods });
const idle = { typing: false, dialogOpen: false, pendingGo: false };

test('Ctrl+K opens the palette even while typing, but not over a dialog', () => {
  assert.deepEqual(shortcutFor(key('k', { ctrlKey: true }), idle), { kind: 'palette' });
  assert.deepEqual(shortcutFor(key('K', { metaKey: true }), { ...idle, typing: true }), { kind: 'palette' });
  assert.equal(shortcutFor(key('k', { ctrlKey: true }), { ...idle, dialogOpen: true }), null);
});

test('?, . and g work only when not typing and no dialog is open', () => {
  assert.deepEqual(shortcutFor(key('?'), idle), { kind: 'help' });
  assert.deepEqual(shortcutFor(key('.'), idle), { kind: 'talk' });
  assert.deepEqual(shortcutFor(key('g'), idle), { kind: 'prefix' });
  assert.equal(shortcutFor(key('?'), { ...idle, typing: true }), null);
  assert.equal(shortcutFor(key('.'), { ...idle, dialogOpen: true }), null);
  assert.equal(shortcutFor(key('g', { altKey: true }), idle), null);
});

test('g then a letter goes to that page; another letter does nothing', () => {
  assert.deepEqual(shortcutFor(key('p'), { ...idle, pendingGo: true }), { kind: 'go', route: 'proposals' });
  assert.deepEqual(shortcutFor(key('o'), { ...idle, pendingGo: true }), { kind: 'go', route: 'overview' });
  assert.equal(shortcutFor(key('z'), { ...idle, pendingGo: true }), null);
  assert.equal(new Set(Object.values(GO_KEYS)).size, Object.keys(GO_KEYS).length, 'one letter per page');
});

test('every shortcut is listed in the help table', () => {
  const listed = SHORTCUTS.flatMap((g) => g.items.map((i) => keysText(i.keys)));
  for (const k of ['Ctrl+K', '?', '.', 'Esc', 'g p', 'g o', 'g s']) assert.ok(listed.includes(k), k);
});

test('fuzzy matching prefers word starts and whole words', () => {
  assert.ok(fuzzyScore('prop', 'Proposals') > fuzzyScore('prop', 'Run the proper sync'));
  assert.ok(fuzzyScore('rs', 'Run standup') > 0);
  assert.equal(fuzzyScore('xyz', 'Run standup'), -1);
});

test('rankCommands finds by label or keywords, recent first without a query', () => {
  const c = (id: string, label: string, group: Command['group'], keywords?: string): Command => ({ id, label, group, keywords, run: () => {} });
  const cmds = [c('a', 'Overview', 'Go to'), c('b', 'Synchronize the backlog', 'Backlog', 'sync pull refresh'), c('c', 'Run standup', 'Crew')];
  assert.equal(rankCommands(cmds, 'pull')[0]?.id, 'b');
  assert.equal(rankCommands(cmds, 'stand')[0]?.id, 'c');
  assert.deepEqual(rankCommands(cmds, '', ['b']).map((x) => x.id), ['b', 'c', 'a']);
});

test('the error boundary renders its page normally', () => {
  const html = renderToStaticMarkup(<ErrorBoundary label="overview"><p>fine</p></ErrorBoundary>);
  assert.equal(html, '<p>fine</p>');
});
