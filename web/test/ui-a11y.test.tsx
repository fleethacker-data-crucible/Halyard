import assert from 'node:assert/strict';
import { test } from 'node:test';
import { renderToStaticMarkup } from 'react-dom/server';
import { ConfirmDialog, Tabs, nextTabIndex, tabIds, tabPanelProps } from '../src/components/ui';

const items = [{ value: 'a', label: 'Alpha', count: 3 }, { value: 'b', label: 'Beta' }, { value: 'c', label: 'Gamma' }];

// Literal for a RegExp source. The character class includes backslash, so an id cannot
// close the escape the way a colon-only prefix (`\:`) did.
function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

test('tabs expose a labelled tablist, one tab stop and a panel for each tab', () => {
  const html = renderToStaticMarkup(<Tabs idPrefix="t" label="Things" value="b" onChange={() => {}} items={items} />);
  assert.match(html, /role="tablist"[^>]*aria-label="Things"|aria-label="Things"[^>]*role="tablist"/);
  const tabs = [...html.matchAll(/<button[^>]*role="tab"[^>]*>/g)].map((m) => m[0]);
  assert.equal(tabs.length, 3);
  assert.match(tabs[1]!, /aria-selected="true"/);
  assert.match(tabs[1]!, /tabindex="0"/);
  assert.match(tabs[0]!, /aria-selected="false"/);
  assert.match(tabs[0]!, /tabindex="-1"/);
  assert.match(tabs[2]!, /tabindex="-1"/);
  for (const [i, v] of ['a', 'b', 'c'].entries()) {
    assert.ok(tabs[i]!.includes(`id="${tabIds('t', v).tab}"`));
    assert.ok(tabs[i]!.includes(`aria-controls="${tabIds('t', v).panel}"`));
  }
});

test('the panel names its tab and the tab names its panel', () => {
  const p = tabPanelProps('review', 'gaps');
  assert.equal(p.role, 'tabpanel');
  assert.equal(p.id, 'review-panel-gaps');
  assert.equal(p['aria-labelledby'], 'review-tab-gaps');
  assert.equal(p.tabIndex, 0);
  assert.equal(tabIds('review', 'gaps').tab, p['aria-labelledby']);
});

test('arrow keys, Home and End move between tabs and wrap', () => {
  assert.equal(nextTabIndex('ArrowRight', 0, 3), 1);
  assert.equal(nextTabIndex('ArrowRight', 2, 3), 0);
  assert.equal(nextTabIndex('ArrowLeft', 0, 3), 2);
  assert.equal(nextTabIndex('ArrowLeft', 2, 3), 1);
  assert.equal(nextTabIndex('Home', 2, 3), 0);
  assert.equal(nextTabIndex('End', 0, 3), 2);
  assert.equal(nextTabIndex('Enter', 0, 3), null);
  assert.equal(nextTabIndex('ArrowRight', 0, 0), null);
});

test('a confirmation dialog is named by its title and described by its body', () => {
  const html = renderToStaticMarkup(<ConfirmDialog open={false} title="Delete the run?" confirmLabel="Delete" onCancel={() => {}} onConfirm={() => {}}>This cannot be undone.</ConfirmDialog>);
  const labelled = html.match(/aria-labelledby="([^"]+)"/)?.[1];
  const described = html.match(/aria-describedby="([^"]+)"/)?.[1];
  assert.ok(labelled && described && labelled !== described);
  assert.match(html, new RegExp(`<h2 id="${escapeRegExp(labelled!)}">Delete the run\\?</h2>`));
  assert.match(html, new RegExp(`<div id="${escapeRegExp(described!)}"[^>]*>This cannot be undone\\.</div>`));
});

test('dialog id patterns treat backslash and other regex metacharacters as literals', () => {
  const id = String.raw`a\b.+(c)|[d]`;
  assert.equal(escapeRegExp(id), String.raw`a\\b\.\+\(c\)\|\[d\]`);
  const html = `<h2 id="${id}">Delete the run?</h2><div id="${id}">This cannot be undone.</div>`;
  assert.match(html, new RegExp(`<h2 id="${escapeRegExp(id)}">Delete the run\\?</h2>`));
  assert.match(html, new RegExp(`<div id="${escapeRegExp(id)}"[^>]*>This cannot be undone\\.</div>`));
});

test('two dialogs on one page get different ids', () => {
  const html = renderToStaticMarkup(<><ConfirmDialog open={false} title="A" confirmLabel="x" onCancel={() => {}} onConfirm={() => {}}>a</ConfirmDialog><ConfirmDialog open={false} title="B" confirmLabel="x" onCancel={() => {}} onConfirm={() => {}}>b</ConfirmDialog></>);
  const ids = [...html.matchAll(/aria-labelledby="([^"]+)"/g)].map((m) => m[1]);
  assert.equal(ids.length, 2);
  assert.notEqual(ids[0], ids[1]);
});

