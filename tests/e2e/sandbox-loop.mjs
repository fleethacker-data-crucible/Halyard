// End-to-end API walk through the full sandbox loop against a running host:
// test connection -> seed -> sync -> review -> propose sizes -> approve -> apply -> re-sync -> teardown.
//
//   node tests/e2e/sandbox-loop.mjs http://127.0.0.1:8773 <session-token> [total]
//
// Point the host at tests/fakes/fake_github.py for an offline run, or at a real sandbox repo.
const [base, token, totalArg, ...flags] = process.argv.slice(2);
const keep = flags.includes('--keep'); // leave the seeded items in place (skips teardown)
const existing = flags.includes('--existing'); // reuse an already-seeded run (E2E_RUN_ID) instead of seeding
if (!base || !token) {
  console.error('usage: node tests/e2e/sandbox-loop.mjs <base-url> <session-token> [total] [--keep] [--existing]');
  process.exit(2);
}
const total = Number(totalArg ?? 30);
const H = { 'X-Cascade-Session': token, 'Content-Type': 'application/json' };

async function call(method, path, body) {
  const r = await fetch(base + path, { method, headers: H, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  const data = text ? JSON.parse(text) : null;
  if (!r.ok) throw new Error(`${method} ${path} -> ${r.status} ${data?.code}: ${data?.error}`);
  return data;
}

async function waitJob(started, label) {
  const id = started.job?.id ?? started.id;
  // Real GitHub creates take ~3-4 s each (paced for rate limits), so allow time in proportion to the run.
  const polls = Math.max(600, total * 20);
  for (let i = 0; i < polls; i++) {
    const j = await call('GET', `/api/jobs/${id}`);
    if (j.state === 'succeeded') return j.result;
    if (j.state === 'failed' || j.state === 'cancelled') throw new Error(`${label} ${j.state}: ${j.error}`);
    await new Promise((r) => setTimeout(r, 300));
  }
  throw new Error(`${label} timed out`);
}

function assert(cond, message) {
  if (!cond) throw new Error('ASSERTION FAILED: ' + message);
  console.log('  ok  ' + message);
}

const runId = process.env.E2E_RUN_ID || 'e2e-' + Date.now().toString(36);
console.log(`sandbox loop against ${base}, ${total} items, run ${runId}`);

const conn = await call('POST', '/api/provider/test');
assert(conn.connected === true, `connected as ${conn.identity?.login}`);

if (!existing) {
  const seed = await waitJob(await call('POST', '/api/sandbox/seed', { total, runId, includeDefects: true }), 'seed');
  assert(seed.items.length === total, `seeded ${seed.items.length}/${total} items`);
  assert(seed.errors.length === 0, 'seed reported no errors');
}

// GitHub's issue listing can lag a few seconds behind creation; retry until every seeded item is visible.
async function syncRun() {
  let result;
  for (let attempt = 0; attempt < 10; attempt++) {
    result = await waitJob(await call('POST', '/api/sync', { repository: 'sandbox', labels: [`cascade-run::${runId}`] }), 'sync');
    if (result.count >= total) return result;
    await new Promise((r) => setTimeout(r, 3000));
  }
  return result;
}
const sync = await syncRun();
assert(sync.count === total, `snapshot holds ${sync.count} items`);

const review = await call('GET', '/api/review');
assert(review.summary.total === total, 'review covers every item');
assert(review.summary.needsReview >= 1, `audit found ${review.summary.needsReview} items needing review (defects were injected)`);
assert(review.audit.errorCount === 0, 'no topology errors in a generated hierarchy');

// Make some stories unsized so the estimator has work: there are none by default, so ask for all.
const est = await waitJob(await call('POST', '/api/intelligence/estimate', { kind: 'storyPoints', onlyUnsized: false, maxItems: 6 }), 'estimate');
assert(est.total > 0, `model produced ${est.total} proposals`);
const batch = await call('GET', `/api/proposals/${est.id}`);
assert(batch.proposals.every((p) => ['1', '2', '3', '5', '8', 'TBD'].includes(p.proposedValue)), 'every proposal respects the points policy (13 clamped)');

const decided = await call('POST', `/api/proposals/${est.id}/decide`, { all: true, decision: 'approved' });
assert(decided.count === batch.proposals.length, `approved ${decided.count}`);
const applied = await waitJob(await call('POST', `/api/proposals/${est.id}/apply`), 'apply');
assert(applied.failed === 0, `applied ${applied.applied}, skipped ${applied.skipped}, failed ${applied.failed}`);

await syncRun();
const after = await call('GET', '/api/review');
const target = batch.proposals[0];
const item = after.items.find((i) => i.key === target.itemKey);
assert(item && item.points === target.proposedValue, `#${target.itemNumber} now has points ${item?.points} (proposed ${target.proposedValue})`);
const detail = await call('GET', `/api/items/${target.itemNumber}`);
assert(detail.body.includes(`- **Story points:** ${target.proposedValue}`), 'mirror and Sizing section updated with the meta');

const writes = await call('GET', '/api/writes');
assert(writes.entries.length > 0 && writes.entries.every((w) => w.target.endsWith('cascade-sandbox')), `${writes.entries.length} audited writes, all to the sandbox`);

if (keep) {
  console.log(`kept run ${runId}; delete it later with: Invoke-SandboxTeardown.ps1 -RunId ${runId}`);
  console.log('sandbox loop passed (teardown skipped)');
  process.exit(0);
}
const dry = await waitJob(await call('POST', '/api/sandbox/teardown', { scope: 'run', runId, dryRun: true }), 'dry-run');
assert(dry.matched === total && dry.deleted === 0, `dry run lists ${dry.matched} and deletes nothing`);
const td = await waitJob(await call('POST', '/api/sandbox/teardown', { scope: 'run', runId }), 'teardown');
assert(td.deleted === total, `teardown deleted ${td.deleted}/${total}`);

const blocked = await fetch(base + '/api/sandbox/teardown', { method: 'POST', headers: H, body: JSON.stringify({ scope: 'everything', confirmText: 'yes please' }) });
assert(blocked.status === 403, 'wipe without the confirmation phrase is refused');

console.log('sandbox loop passed');
