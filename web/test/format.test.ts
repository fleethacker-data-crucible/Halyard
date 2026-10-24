import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ageLabel, humanizeCode, pct, plural, toCsv, truncate } from '../src/lib/format';
import { LEVELS, expectedParent, isLevel, levelDepth } from '../src/lib/levels';
import { Review, Status } from '../src/lib/schemas';

test('humanizeCode turns stable codes into sentences', () => {
  assert.equal(humanizeCode('story_missing_acceptance_criteria'), 'Story missing acceptance criteria');
  assert.equal(humanizeCode('PARENT_NOT_DECLARED'), 'Parent not declared');
});

test('toCsv quotes commas, quotes and newlines', () => {
  const csv = toCsv([{ a: 'x,y', b: 'say "hi"', c: ['one', 'two'] }, { a: 'line\nbreak', b: null, c: 3 }], ['a', 'b', 'c']);
  assert.equal(csv, 'a,b,c\r\n"x,y","say ""hi""",one; two\r\n"line\nbreak",,3');
});

test('small formatters', () => {
  assert.equal(pct(1, 4), '25%');
  assert.equal(pct(1, 0), '0%');
  assert.equal(plural(1, 'item'), '1 item');
  assert.equal(plural(2, 'story', 'stories'), '2 stories');
  assert.equal(truncate('abcdef', 4), 'abc…');
  assert.equal(ageLabel(30), 'under a minute old');
  assert.equal(ageLabel(7200), '2 h old');
});

test('levels follow the locked topology', () => {
  assert.deepEqual(LEVELS, ['THEME', 'EPIC', 'FEATURE', 'STORY', 'TASK']);
  assert.equal(expectedParent('STORY'), 'FEATURE');
  assert.equal(expectedParent('THEME'), null);
  assert.ok(levelDepth('TASK') > levelDepth('THEME'));
  assert.ok(isLevel('EPIC'));
  assert.ok(!isLevel('MILESTONE'));
});

test('schemas reject a review without a summary', () => {
  assert.equal(Review.safeParse({ items: [] }).success, false);
  assert.equal(Status.safeParse({}).success, false);
});
