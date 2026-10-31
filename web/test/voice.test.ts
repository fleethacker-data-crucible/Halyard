import assert from 'node:assert/strict';
import { test } from 'node:test';
import { briefText, chunkForSpeech, dictationSupport, handoffFromDictation, pickLocalVoice } from '../src/lib/voice';
import type { Leadership } from '../src/lib/schemas';

const voice = (name: string, lang: string, localService: boolean, isDefault = false) => ({ name, lang, localService, default: isDefault });

test('pickLocalVoice never chooses a cloud voice', () => {
  assert.equal(pickLocalVoice([voice('Cloud', 'en-US', false)]), undefined);
  assert.equal(pickLocalVoice([]), undefined);
  assert.equal(pickLocalVoice([voice('Cloud', 'en-US', false), voice('Local', 'en-GB', true)])?.name, 'Local');
});

test('pickLocalVoice prefers the language, then the default voice', () => {
  const voices = [voice('Hedda', 'de-DE', true, true), voice('Zira', 'en-US', true), voice('David', 'en-US', true, true)];
  assert.equal(pickLocalVoice(voices, 'en-US')?.name, 'David');
  assert.equal(pickLocalVoice(voices, 'de')?.name, 'Hedda');
  assert.equal(pickLocalVoice(voices, 'fr'), undefined);
});

const leadership = {
  currentSprint: '2026-S23', featuresOnTrack: 7, featuresActive: 9, averageVelocity: 18.5, features: new Array(10).fill({}),
  ready: { stories: 14, points: 40, notReady: 2, averageVelocity: 18.5, healthy: true },
  capacity: { configured: true, usedPercent: 82, plannedPoints: 18, teamCapacity: 22 },
  blockers: [{ title: 'Waiting on the vendor', ageDays: 9, escalated: true }, { title: 'Access request', ageDays: 2, escalated: false }],
  notes: ['Two features have no target.'],
} as unknown as Leadership;

test('briefText reads as sentences, oldest blocker first', () => {
  const text = briefText(leadership);
  assert.match(text, /^Leadership brief for sprint 2026-S23\./);
  assert.match(text, /7 of 10 features on track or done, 9 still active\./);
  assert.match(text, /14 stories, 40 points, which is healthy\./);
  assert.match(text, /Capacity used is 82 percent: 18 of 22 points planned\./);
  assert.match(text, /There are 2 open blockers, 1 escalated\. Waiting on the vendor, 9 days old\. Access request, 2 days old\./);
  assert.match(text, /Two features have no target\.$/);
  assert.doesNotMatch(text, /[|<>#*]/);
});

test('briefText handles no blockers and no capacity', () => {
  const text = briefText({ ...leadership, blockers: [], capacity: { ...leadership.capacity, configured: false }, notes: [] } as Leadership);
  assert.match(text, /There are no open blockers\./);
  assert.doesNotMatch(text, /Capacity used/);
});

test('chunkForSpeech keeps sentences together and never cuts a word', () => {
  assert.deepEqual(chunkForSpeech('One. Two. Three.', 10), ['One. Two.', 'Three.']);
  assert.deepEqual(chunkForSpeech(''), []);
  const long = 'word '.repeat(100).trim() + '.';
  const chunks = chunkForSpeech(long, 50);
  assert.ok(chunks.length > 5);
  assert.ok(chunks.every((c) => c.length <= 50));
  assert.equal(chunks.join(' ').replace(/\./g, ''), long.replace(/\./g, ''));
});

test('dictation is offered only when recognition can run locally', () => {
  assert.equal(dictationSupport({}).supported, false);
  assert.match(dictationSupport({}).reason, /no speech recognition/);
  class Online {}
  assert.equal(dictationSupport({ webkitSpeechRecognition: Online }).supported, false);
  assert.match(dictationSupport({ webkitSpeechRecognition: Online }).reason, /online service/);
  class Local { processLocally = false; }
  Object.defineProperty(Local.prototype, 'processLocally', { value: false, writable: true });
  assert.equal(dictationSupport({ SpeechRecognition: Local }).supported, true);
});

test('a dictated note becomes a fleet-handoff document', () => {
  const doc = handoffFromDictation('Renew the badge reader contract. Vendor wants a decision by Friday.\n\nOrder spare cables', new Date('2026-10-31T09:00:00Z'));
  assert.equal(doc.format, 'fleet-handoff');
  assert.equal(doc.version, 1);
  assert.equal(doc.source.app, 'dictation');
  assert.equal(doc.items.length, 2);
  assert.equal(doc.items[0]!.title, 'Renew the badge reader contract');
  assert.equal(doc.items[0]!.body, 'Vendor wants a decision by Friday.');
  assert.equal(doc.items[1]!.title, 'Order spare cables');
  assert.equal(doc.items[1]!.body, '');
  assert.equal(doc.items[0]!.ref, 'dictation-20261031090000-1');
  assert.deepEqual(handoffFromDictation('   \n ', new Date()).items, []);
});
