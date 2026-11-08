import assert from 'node:assert/strict';
import { test } from 'node:test';
import { startFairlead } from '../src/lib/fairlead';

type Reply = { state: string; canStart: boolean; message: string };

function fakeFetch(replies: Record<string, Reply[]>) {
  const calls: string[] = [];
  globalThis.fetch = (async (input: string, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${input}`;
    calls.push(key);
    const queue = replies[key] ?? [];
    const body = queue.length > 1 ? queue.shift()! : queue[0];
    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
  }) as typeof fetch;
  return calls;
}

test('starting Fairlead waits until it answers', async () => {
  const calls = fakeFetch({
    'POST /api/fairlead/start': [{ state: 'starting', canStart: true, message: 'Starting Fairlead.' }],
    'GET /api/fairlead': [{ state: 'offline', canStart: true, message: 'not yet' }, { state: 'ready', canStart: true, message: 'Fairlead is answering.' }],
  });
  const seen: string[] = [];
  const s = await startFairlead((x) => seen.push(x.state), 5000, 1);
  assert.equal(s.state, 'ready');
  assert.deepEqual(seen, ['starting', 'offline', 'ready']);
  assert.equal(calls[0], 'POST /api/fairlead/start');
});

test('a missing Fairlead is reported without waiting', async () => {
  const calls = fakeFetch({ 'POST /api/fairlead/start': [{ state: 'missing', canStart: false, message: 'Set HALYARD_FAIRLEAD_PATH.' }] });
  const s = await startFairlead(undefined, 5000, 1);
  assert.equal(s.state, 'missing');
  assert.deepEqual(calls, ['POST /api/fairlead/start']);
});

test('the wait gives up after its limit', async () => {
  fakeFetch({
    'POST /api/fairlead/start': [{ state: 'starting', canStart: true, message: '' }],
    'GET /api/fairlead': [{ state: 'offline', canStart: true, message: 'down' }],
  });
  const started = Date.now();
  const s = await startFairlead(undefined, 30, 5);
  assert.equal(s.state, 'offline');
  assert.ok(Date.now() - started < 1000);
});
