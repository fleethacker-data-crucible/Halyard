import { test } from 'node:test';
import assert from 'node:assert/strict';
import { diffLines, diffStats, foldUnchanged } from '../src/lib/diff';

test('identical texts produce only same ops', () => {
  const ops = diffLines('a\nb\nc', 'a\nb\nc');
  assert.deepEqual(ops.map((o) => o.kind), ['same', 'same', 'same']);
});

test('added section shows as additions after the common prefix', () => {
  const before = '## User need\nAs a tester\n\n## Tasks\n- [ ] x';
  const after = '## User need\nAs a tester\n\n## Acceptance criteria\n- [ ] one\n\n## Tasks\n- [ ] x';
  const ops = diffLines(before, after);
  const stats = diffStats(ops);
  assert.equal(stats.removed, 0);
  assert.equal(stats.added, 3);
  assert.equal(stats.unchanged, 5);
  assert.ok(ops.some((o) => o.kind === 'add' && o.text === '## Acceptance criteria'));
});

test('replaced line shows as a deletion and an addition', () => {
  const ops = diffLines('points: 13', 'points: 8');
  assert.deepEqual(ops, [{ kind: 'del', text: 'points: 13' }, { kind: 'add', text: 'points: 8' }]);
});

test('empty before is all additions; CRLF is normalised', () => {
  assert.deepEqual(diffLines('', 'a\r\nb').map((o) => o.kind), ['add', 'add']);
});

test('folding keeps context around changes and collapses long same runs', () => {
  const before = Array.from({ length: 20 }, (_, i) => `line ${i}`).join('\n');
  const after = before.replace('line 10', 'line ten');
  const rows = foldUnchanged(diffLines(before, after), 2);
  const folds = rows.filter((r) => r.kind === 'fold');
  assert.equal(folds.length, 2);
  assert.equal(rows.filter((r) => r.kind === 'same').length, 4);
});
