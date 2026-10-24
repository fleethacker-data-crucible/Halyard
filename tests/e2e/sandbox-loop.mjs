// End-to-end API walk through the full sandbox loop against a running host:
// test connection -> seed (80% complete, 20% with gaps) -> sync -> review (every injected gap found)
// -> metadata repair -> refine drafts -> propose sizes -> manual edit -> approve -> apply -> re-sync -> teardown.
//
//   node tests/e2e/sandbox-loop.mjs http://127.0.0.1:8773 <session-token> [total]
//
// Point the host at tests/fakes/fake-github.mjs for an offline run, or at a real sandbox repo.
const [base, token, totalArg, ...flags] = process.argv.slice(2);
const keep = flags.includes('--keep'); // leave the seeded items in place (skips teardown)
const existing = flags.includes('--existing'); // reuse an already-seeded run (E2E_RUN_ID) instead of seeding
// --fake <url>: the fake GitHub is reachable, so raw tickets can be planted in a source repository to exercise
// intake, and the sandbox can be wiped at the end. Never used against a real host.
const fakeUrl = flags.includes('--fake') ? flags[flags.indexOf('--fake') + 1] : null;
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

let seeded = null;
if (!existing) {
  seeded = await waitJob(await call('POST', '/api/sandbox/seed', { total, runId, includeDefects: true }), 'seed');
  assert(seeded.items.length === total, `seeded ${seeded.items.length}/${total} items`);
  assert(seeded.errors.length === 0, 'seed reported no errors');
  const review = seeded.items.filter((i) => i.defects.length > 0).length;
  assert(review >= Math.floor(total * 0.15) && review <= Math.ceil(total * 0.25), `${review} of ${total} items were seeded with gaps (about 20%)`);
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
assert(review.summary.needsReview >= 1, `audit found ${review.summary.needsReview} items needing review (gaps were injected)`);
assert(review.audit.errorCount === 0, 'no topology errors in a generated hierarchy');
if (seeded) {
  // Every injected gap must surface as the expected code on the right item (matched by number from the manifest).
  const byNumber = new Map(review.items.map((i) => [i.number, i]));
  let missed = [];
  for (const s of seeded.items.filter((i) => i.expectedCodes.length > 0)) {
    const r = byNumber.get(s.number);
    const raised = new Set([...(r?.deficiencies ?? []), ...(r?.findings ?? []).map((f) => f.code)]);
    for (const code of s.expectedCodes) if (!raised.has(code)) missed.push(`${s.id}:${code}`);
  }
  assert(missed.length === 0, `every injected gap was found by the audit${missed.length ? ' (missed ' + missed.join(', ') + ')' : ''}`);
}

async function approveAndApply(batchId, label) {
  const b = await call('GET', `/api/proposals/${batchId}`);
  const pending = b.proposals.filter((p) => p.decision === 'pending').length;
  if (pending > 0) {
    const decided = await call('POST', `/api/proposals/${batchId}/decide`, { all: true, decision: 'approved' });
    assert(decided.count === pending, `${label}: approved ${decided.count}`);
  }
  const applied = await waitJob(await call('POST', `/api/proposals/${batchId}/apply`), `${label} apply`);
  assert(applied.failed === 0, `${label}: applied ${applied.applied}, skipped ${applied.skipped}, failed ${applied.failed}`);
  return b;
}

// 1. Metadata repair (deterministic): missing blocks, TBD placeholders, wrong-level sizing keys.
const metaBatch = await call('POST', '/api/intelligence/metadata', {});
assert(metaBatch.proposals.length > 0, `metadata repair proposed ${metaBatch.proposals.length} changes`);
const preview = await call('GET', `/api/proposals/${metaBatch.id}/${metaBatch.proposals[0].id}/preview`);
assert(preview.proposed.body !== preview.current.body && preview.proposed.body.startsWith('~~~cascade-meta'), 'preview shows the projected body with a meta block');
await approveAndApply(metaBatch.id, 'metadata');

// 2. Refinement drafts from the model for content gaps (acceptance criteria, user need, tasks, done-when...).
const refine = await waitJob(await call('POST', '/api/intelligence/refine', { maxItems: 12 }), 'refine');
assert(refine.total > 0, `model drafted ${refine.total} refinements`);
const refineBatch = await call('GET', `/api/proposals/${refine.id}`);
assert(refineBatch.proposals.every((p) => p.kind === 'refinement' && p.addresses.length > 0), 'every refinement names the gaps it closes');
assert(refineBatch.proposals.every((p) => p.remaining.length === 0), 'every draft closes its gaps when checked against the DoR rules');
await approveAndApply(refine.id, 'refinement');

// 3. Sizes from the model for stories left TBD (the metadata repair made the unsized ones TBD).
const est = await waitJob(await call('POST', '/api/intelligence/estimate', { kind: 'storyPoints', onlyUnsized: false, maxItems: 6 }), 'estimate');
assert(est.total > 0, `model produced ${est.total} size proposals`);
const batch = await approveAndApply(est.id, 'sizes');
assert(batch.proposals.every((p) => ['1', '2', '3', '5', '8', 'TBD'].includes(p.proposedValue)), 'every proposal respects the points policy (13 clamped)');

// 4. A manual edit from the browser goes through the same path, pre-approved.
const sized = new Set(batch.proposals.map((p) => p.itemKey));
const story = review.items.find((i) => i.type === 'STORY' && i.needsReview === false && !sized.has(i.key));
const manual = await call('POST', '/api/proposals/manual', { number: story.number, kind: 'points', value: '2', note: 'e2e' });
assert(manual.proposals[0].decision === 'approved', 'manual edit is recorded as an approved proposal');
await approveAndApply(manual.id, 'manual');

await syncRun();
const after = await call('GET', '/api/review');
const target = batch.proposals[0];
const item = after.items.find((i) => i.key === target.itemKey);
assert(item && item.points === target.proposedValue, `#${target.itemNumber} now has points ${item?.points} (proposed ${target.proposedValue})`);
const detail = await call('GET', `/api/items/${target.itemNumber}`);
assert(detail.body.includes(`- **Story points:** ${target.proposedValue}`), 'mirror and Sizing section updated with the meta');
const manualItem = after.items.find((i) => i.number === story.number);
assert(manualItem.points === '2', `manual edit landed on #${story.number}`);
const contentGaps = ['story_missing_acceptance_criteria', 'story_missing_user_need', 'story_missing_tasks', 'feature_missing_done_when', 'epic_missing_success_picture', 'epic_missing_outcome_statement', 'theme_missing_scope'];
const stillOpen = after.items.filter((i) => i.deficiencies.some((d) => contentGaps.includes(d))).length;
const wereOpen = review.items.filter((i) => i.deficiencies.some((d) => contentGaps.includes(d))).length;
assert(stillOpen < wereOpen, `content gaps went from ${wereOpen} to ${stillOpen} after refinement`);
assert(after.items.every((i) => !i.deficiencies.includes('missing_cascade_metadata')), 'no item is left without a cascade-meta block');

// 5. Leadership, planning and vision read the same snapshot.
const lead = await call('GET', '/api/leadership');
assert(lead.features.length > 0 && lead.velocity.length > 0, `leadership view: ${lead.featuresOnTrack}/${lead.features.length} features on track, velocity ${lead.velocity.map((v) => v.pointsDone).join('/')}`);
assert(lead.capacity.configured, 'capacity assessed from config/team.json');
const reportHtml = await (await fetch(base + '/api/leadership/report.html', { headers: H })).text();
assert(reportHtml.startsWith('<!doctype html>') && reportHtml.includes('Feature % complete'), 'HTML report renders');
const planPreview = await call('POST', '/api/plan/preview', { sprintsAhead: 2 });
assert(planPreview.plan.sprints.length === 3, `planner frames ${planPreview.plan.sprints.length} sprints, places ${planPreview.plan.placed} stories`);
const planned = await call('POST', '/api/plan/propose', { sprintsAhead: 2 });
if (planned.batch && planned.batch.total > 0) {
  await approveAndApply(planned.batch.id, 'sprint plan');
}
const skills = await call('POST', '/api/intelligence/skills', {});
assert(skills.kind === 'skills', `skill tags proposed for ${skills.proposals.length} items`);
const visionJob = await waitJob(await call('POST', '/api/vision/draft', { useModel: true }), 'vision');
const visionDoc = await call('GET', `/api/vision/${visionJob.id}`);
assert(visionDoc.markdown.includes('## Vision') && visionDoc.themes.length > 0, `vision drafted with ${visionDoc.themes.length} themes and ${visionDoc.priorities.length} priorities`);

// 6. Intake: raw tickets in a source repository become structured sandbox copies (fake GitHub only).
if (fakeUrl) {
  const raw = [
    { title: 'As an analyst I want nightly exports so that reports are fresh', body: 'Acceptance criteria\n- export runs nightly\n- failures page someone' },
    { title: 'Epic: modernise the ingestion platform', body: 'Success picture: every feed has a contract and an owner.' },
    { title: 'Login page throws 500 on empty password', body: 'Steps: submit the form empty.', labels: ['bug'] },
    { title: 'Rename the staging bucket', body: '', labels: ['chore'] },
  ];
  for (const t of raw) {
    const r = await fetch(`${fakeUrl}/repos/sun-of-anton/raw-backlog/issues`, { method: 'POST', headers: { Authorization: 'Bearer fake', 'Content-Type': 'application/json' }, body: JSON.stringify({ title: t.title, body: t.body, labels: t.labels ?? [] }) });
    assert(r.status === 201, `planted raw ticket "${t.title.slice(0, 30)}…"`);
  }
  await waitJob(await call('POST', '/api/sync', { repository: 'all' }), 'sync all');
  const sources = await call('GET', '/api/intake/sources');
  assert(sources.items.length === raw.length, `${sources.items.length} source tickets visible, none copied yet`);
  assert(sources.items.find((i) => i.title.startsWith('Epic:')).guessedType === 'EPIC', 'epic guessed from the title prefix');
  const keys = sources.items.map((i) => i.key);
  const prev = await call('POST', '/api/intake/preview', { keys });
  assert(prev.drafts.length === raw.length && prev.drafts.every((d) => d.body.includes('source_iid:')), 'drafts carry the source reference');
  const intake = await waitJob(await call('POST', '/api/intake/run', { keys }), 'intake');
  assert(intake.items.length === raw.length && intake.errors.length === 0, `intake copied ${intake.items.length} tickets into the sandbox`);
  await waitJob(await call('POST', '/api/sync', { repository: 'all' }), 'sync all');
  const again = await call('GET', '/api/intake/sources');
  assert(again.items.every((i) => i.copiedAs), 'every source ticket now shows its sandbox copy');
  const rerun = await call('POST', '/api/intake/preview', { keys });
  assert(rerun.alreadyCopied === raw.length, 'a second intake would copy nothing twice');
  const copies = (await call('GET', '/api/review')).items.filter((i) => i.labels.includes('cascade-intake'));
  assert(copies.length === raw.length && copies.every((i) => i.hasMeta && i.type !== 'UNCLASSIFIED'), 'copies are classified work items with cascade-meta');
  const sourceWrites = (await call('GET', '/api/writes')).entries.filter((w) => w.target.includes('raw-backlog'));
  assert(sourceWrites.length === 0, 'the source repository was never written');
  // back to a run-scoped sandbox snapshot for the teardown checks
  await syncRun();
}

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
if (fakeUrl) {
  const wipe = await waitJob(await call('POST', '/api/sandbox/teardown', { scope: 'everything', confirmText: 'DELETE sun-of-anton/cascade-sandbox' }), 'wipe');
  assert(wipe.deleted >= 4, `wipe with the phrase removed the ${wipe.deleted} remaining (intake) items`);
}

console.log('sandbox loop passed');
