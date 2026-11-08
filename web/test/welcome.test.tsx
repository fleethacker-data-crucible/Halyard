import assert from 'node:assert/strict';
import { test } from 'node:test';
import { renderToStaticMarkup } from 'react-dom/server';
import { Welcome } from '../src/components/welcome';
import { itemTitle } from '../src/lib/format';
import type { Status } from '../src/lib/schemas';
import { queueCrew, showWelcomeOnLaunch, takeCrewRequest, welcomePrimary } from '../src/lib/welcome';

const status = (over: { snapshot?: Status['snapshot']; sandbox?: string; proposals?: number } = {}) => ({
  config: { sandboxRepository: over.sandbox ?? '' },
  snapshot: over.snapshot ?? null,
  proposals: over.proposals ?? 0,
}) as unknown as Status;
const snap = { id: 's', repository: 'acme/backlog', provider: 'github', takenAt: '', count: 100, ageSeconds: 5, labelFilter: null } as NonNullable<Status['snapshot']>;

test('the primary way in follows where the person is', () => {
  assert.equal(welcomePrimary(undefined).kind, 'waiting');
  assert.equal(welcomePrimary(status()).kind, 'connect');
  assert.equal(welcomePrimary(status({ sandbox: 'acme/Halyard-Ballast' })).route, 'sandbox');
  const c = welcomePrimary(status({ snapshot: snap, sandbox: 'x' }));
  assert.equal(c.kind, 'continue');
  assert.match(c.label, /acme\/backlog \(100 items\)/);
});

test('the welcome shows on launch, not on a deep link or when turned off', () => {
  assert.equal(showWelcomeOnLaunch('', false), true);
  assert.equal(showWelcomeOnLaunch('#/', false), true);
  assert.equal(showWelcomeOnLaunch('#/proposals?batch=b1', false), false);
  assert.equal(showWelcomeOnLaunch('', true), false);
});

test('a queued crew request is taken once', () => {
  queueCrew('Run standup');
  assert.deepEqual(takeCrewRequest(), { message: 'Run standup' });
  assert.equal(takeCrewRequest(), null);
});

test('itemTitle drops the level prefix the chip already shows', () => {
  assert.equal(itemTitle('STORY :: Export the leadership dashboard as a PDF'), 'Export the leadership dashboard as a PDF');
  assert.equal(itemTitle('feature::Source Feed Contracts'), 'Source Feed Contracts');
  assert.equal(itemTitle('TASK :: Rotate keys :: HUMAN APPROVAL REQUIRED'), 'Rotate keys');
  assert.equal(itemTitle('Storyboard the demo'), 'Storyboard the demo');
  assert.equal(itemTitle('STORY ::'), 'STORY ::');
  assert.equal(itemTitle(null), '');
});

test('the welcome page has the emblem, one headline, the primary button and the skip choice', () => {
  const html = renderToStaticMarkup(<Welcome status={status({ snapshot: snap, proposals: 3 })} onGo={() => {}} skipNextTime={false} onSkipNextTime={() => {}} />);
  assert.match(html, /brand\/emblem-dark\.png/);
  assert.match(html, /Raise the sails\./);
  assert.match(html, /Continue with acme\/backlog/);
  assert.match(html, /Run standup/);
  assert.match(html, /Proposals \(3\)/);
  assert.match(html, /Open straight to the overview next time/);
  const first = renderToStaticMarkup(<Welcome status={status({ sandbox: 'acme/Halyard-Ballast' })} onGo={() => {}} skipNextTime={false} onSkipNextTime={() => {}} />);
  assert.match(first, /Seed the sandbox/);
  assert.doesNotMatch(first, /Run standup/);
});
