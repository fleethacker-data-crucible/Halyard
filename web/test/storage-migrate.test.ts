import assert from 'node:assert/strict';
import { test } from 'node:test';
import { LEGACY_PREFIX, MIGRATED_KEY, PREFIX, migrateLegacyStorage, type KeyValueStorage } from '../src/lib/storage-migrate';

class FakeStorage implements KeyValueStorage {
  readonly map = new Map<string, string>();
  constructor(init: Record<string, string> = {}) { for (const [k, v] of Object.entries(init)) this.map.set(k, v); }
  get length() { return this.map.size; }
  key(i: number) { return [...this.map.keys()][i] ?? null; }
  getItem(k: string) { return this.map.has(k) ? this.map.get(k)! : null; }
  setItem(k: string, v: string) { this.map.set(k, v); }
}

test('copies cascade.* keys to halyard.* and leaves the originals', () => {
  const s = new FakeStorage({ 'cascade.hierarchy.view': '"graph"', 'cascade.tree.review': '{"a":true}', 'other.key': '1' });
  assert.equal(migrateLegacyStorage(s), 2);
  assert.equal(s.getItem('halyard.hierarchy.view'), '"graph"');
  assert.equal(s.getItem('halyard.tree.review'), '{"a":true}');
  assert.equal(s.getItem('cascade.hierarchy.view'), '"graph"');
  assert.equal(s.getItem('cascade.tree.review'), '{"a":true}');
  assert.equal(s.getItem('halyard.key'), null);
  assert.notEqual(s.getItem(MIGRATED_KEY), null);
});

test('never overwrites a value already saved under the new name', () => {
  const s = new FakeStorage({ 'cascade.hierarchy.view': '"graph"', 'halyard.hierarchy.view': '"tree"' });
  assert.equal(migrateLegacyStorage(s), 0);
  assert.equal(s.getItem('halyard.hierarchy.view'), '"tree"');
});

test('runs once: later changes to old keys are not copied again', () => {
  const s = new FakeStorage({ 'cascade.a': '1' });
  assert.equal(migrateLegacyStorage(s), 1);
  s.setItem('cascade.b', '2');
  assert.equal(migrateLegacyStorage(s), 0);
  assert.equal(s.getItem('halyard.b'), null);
});

test('tolerates missing or throwing storage', () => {
  assert.equal(migrateLegacyStorage(null), 0);
  const broken: KeyValueStorage = { length: 1, key: () => { throw new Error('blocked'); }, getItem: () => null, setItem: () => { throw new Error('full'); } };
  assert.equal(migrateLegacyStorage(broken), 0);
});

test('prefixes are the documented ones', () => {
  assert.equal(PREFIX, 'halyard.');
  assert.equal(LEGACY_PREFIX, 'cascade.');
});
