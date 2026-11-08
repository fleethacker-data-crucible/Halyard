import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { orbGesture, parseOrbPrefs } from '../src/lib/orb-prefs';
import { applyThemeSpec, clampSpec, FLEET_THEME, loadTheme, type ThemeRoot } from '../src/lib/theme';
import { edited, parseTheme, PRESETS, surprise } from '../src/lib/theme-presets';

function memory(seed: Record<string, string> = {}) {
  const m = new Map(Object.entries(seed));
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) };
}

test('the old light or dark choice becomes the new theme mode, once', () => {
  assert.equal(loadTheme(memory({ 'halyard-theme': 'light' })).mode, 'light');
  assert.equal(loadTheme(memory({ 'halyard-theme': 'dark' })).mode, 'dark');
  assert.equal(loadTheme(memory()).mode, 'system');
  // A saved theme wins over the old key.
  assert.equal(loadTheme(memory({ 'halyard-theme': 'dark', 'halyard.theme.v1': JSON.stringify({ ...FLEET_THEME, mode: 'light' }) })).mode, 'light');
});

test('a theme file from Lazarette or Semaphore reads here, and bad values fall back', () => {
  const t = parseTheme(JSON.stringify({ v: 1, name: 'Harbor', mode: 'dark', accent: '#38bdf8', shape: 'blob', tintStrength: 99 }));
  assert.ok(t);
  assert.equal(t.accent, '#38bdf8');
  assert.equal(t.shape, 'soft');
  assert.equal(t.tintStrength, 40);
  assert.equal(parseTheme('not json'), null);
  assert.equal(parseTheme('[1,2]'), null);
});

test('editing a preset names it Custom, and Surprise keeps a readable accent', () => {
  assert.equal(edited(PRESETS[1]!, { accent: '#ff0000' }).name, 'Custom');
  const s = surprise(FLEET_THEME, () => 0.3);
  assert.match(s.accent, /^#[0-9a-f]{6}$/);
  assert.deepEqual(clampSpec(s), s);
});

test('applying a theme writes the Fleet variables and the attributes the stylesheet reads', () => {
  const props = new Map<string, string>();
  const root: ThemeRoot = { style: { setProperty: (n, v) => void props.set(n, v), removeProperty: (n) => void props.delete(n) }, dataset: {} };
  applyThemeSpec(clampSpec({ ...FLEET_THEME, accent: '#0e7490', shape: 'round', density: 'compact', motion: 'off' }), false, root);
  assert.equal(props.get('--fl-accent'), '#0e7490');
  assert.equal(props.get('--fl-radius-control'), '10px');
  assert.equal(root.dataset.density, 'compact');
  assert.equal(root.dataset.motion, 'off');
});

test('orb prefs keep only known values and the orb is on unless turned off', () => {
  assert.deepEqual(parseOrbPrefs(null), { on: true, look: 'ring', theme: 'brass', talk: 'click' });
  assert.deepEqual(parseOrbPrefs({ on: false, look: 'mesh', theme: 'reef', talk: 'hold' }), { on: false, look: 'mesh', theme: 'reef', talk: 'hold' });
  assert.deepEqual(parseOrbPrefs({ look: 'disco', theme: 7, talk: 'shout' }), { on: true, look: 'ring', theme: 'brass', talk: 'click' });
});

test('a press on the orb is a drag once it moves, a hold once it is held still', () => {
  assert.equal(orbGesture({ x: 0, y: 0 }, { x: 2, y: 2 }, 100), 'press');
  assert.equal(orbGesture({ x: 0, y: 0 }, { x: 2, y: 2 }, 400), 'hold');
  assert.equal(orbGesture({ x: 0, y: 0 }, { x: 10, y: 0 }, 400), 'drag');
});

test('the five hierarchy levels have five different hues in both themes', () => {
  const css = readFileSync(new URL('../src/styles.css', import.meta.url), 'utf8');
  const block = (from: number) => css.slice(from, css.indexOf('}', from));
  const light = block(css.indexOf('/* Chart room: level hues'));
  const dark = block(css.indexOf(':root[data-theme="dark"]'));
  for (const b of [light, dark]) {
    const hues = ['theme', 'epic', 'feature', 'story', 'task'].map((l) => new RegExp(`--lv-${l}:\\s*(#[0-9a-f]{6})`, 'i').exec(b)?.[1]?.toLowerCase());
    assert.ok(hues.every(Boolean), 'every level has a colour');
    assert.equal(new Set(hues).size, 5);
  }
});
