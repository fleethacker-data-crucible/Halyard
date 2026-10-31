import assert from 'node:assert/strict';
import { test } from 'node:test';
import { renderToStaticMarkup } from 'react-dom/server';
import { ToastProvider } from '../src/components/ui';
import { DictationPanel, ReadAloud } from '../src/components/voice';
import { GoalPanel, PromotePanel, ShellbackRefresh, WebhookPanel } from '../src/components/fleet-actions';
import { SprintReviewPanel } from '../src/components/sprint-review';
import { ApplyHistory } from '../src/components/apply-history';

// The new screens render their first, data-less state without a browser: panels that need data stay out of the way, buttons are named.
const toastRegion = /<div class="pointer-events-none[^>]*aria-live="polite"><\/div>/;
const wrap = (node: React.ReactNode) => renderToStaticMarkup(<ToastProvider>{node}</ToastProvider>);
const panelOnly = (node: React.ReactNode) => wrap(node).replace(toastRegion, '');

test('the webhook panel explains itself and offers a preview before any send', () => {
  const html = wrap(<WebhookPanel />);
  assert.match(html, /Share to Teams or Slack/);
  assert.match(html, /Preview message/);
  assert.doesNotMatch(html, /Send to the webhook/);
  assert.match(html, /HALYARD_REPORT_WEBHOOK/);
});

test('the goal panel says nothing is applied and has a Run button', () => {
  const html = wrap(<GoalPanel onBatch={() => {}} />);
  assert.match(html, /Get the backlog ready/);
  assert.match(html, /Nothing is applied/);
  assert.match(html, />Run</);
});

test('the Shellback refresh button names what it re-reads', () => {
  const html = wrap(<ShellbackRefresh onDone={() => {}} />);
  assert.match(html, /HALYARD_SHELLBACK_EXPORT/);
  assert.match(html, />Refresh</);
});

test('the dictation panel always offers typing and a two-step copy', () => {
  const html = wrap(<DictationPanel onCopied={() => {}} />);
  assert.match(html, /Dictate or type a backlog note/);
  assert.match(html, /<textarea/);
  assert.match(html, /Preview drafts/);
  assert.doesNotMatch(html, /Copy \d+ into the sandbox/);
  assert.doesNotMatch(html, />Dictate</); // no browser here, so no microphone button is offered
});

test('panels that wait for data render nothing until it arrives', () => {
  assert.equal(panelOnly(<PromotePanel />), '');
  assert.equal(panelOnly(<SprintReviewPanel />), '');
  assert.equal(panelOnly(<ApplyHistory onDone={() => {}} />), '');
  assert.equal(panelOnly(<ReadAloud data={undefined} />), '');
});
