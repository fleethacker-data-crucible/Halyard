import assert from 'node:assert/strict';
import { test } from 'node:test';
import { cleanForSpeech, historyForEngine, MAX_PIECE, orbCaption, orbState, speechPieces } from '../src/lib/crew';
import { cleanTranscript, createUtterance, downsample } from '../src/lib/dictation-audio';

const base = { listening: false, transcribing: false, busy: false, speaking: false, awaitingApproval: false };

test('orbState puts the microphone first, then work, speech and approval', () => {
  assert.equal(orbState(base), 'idle');
  assert.equal(orbState({ ...base, awaitingApproval: true }), 'approval');
  assert.equal(orbState({ ...base, awaitingApproval: true, speaking: true }), 'speaking');
  assert.equal(orbState({ ...base, speaking: true, busy: true }), 'thinking');
  assert.equal(orbState({ ...base, busy: true, listening: true }), 'listening');
  assert.equal(orbState({ ...base, transcribing: true }), 'thinking');
});

test('orbCaption names the batches waiting', () => {
  assert.equal(orbCaption('approval', 1), '1 batch to review');
  assert.equal(orbCaption('approval', 3), '3 batches to review');
  assert.equal(orbCaption('idle', 3), '');
});

test('cleanForSpeech keeps the words and reads issue numbers', () => {
  assert.equal(cleanForSpeech('**Review** [batch](#/proposals) for #42, see https://x.test/a'), 'Review batch for number 42, see link');
  assert.equal(cleanForSpeech('- one\n- two'), 'one two');
});

test('speechPieces splits by sentence and never cuts a word', () => {
  assert.deepEqual(speechPieces('One. Two!\nThree'), ['One.', 'Two!', 'Three']);
  const long = Array.from({ length: 200 }, (_, i) => `word${i}`).join(' ');
  const pieces = speechPieces(long);
  assert.ok(pieces.length > 1);
  assert.ok(pieces.every((p) => p.length <= MAX_PIECE));
  assert.equal(pieces.join(' '), long);
  assert.deepEqual(speechPieces('```\ncode\n``` ...'), []);
});

test('historyForEngine sends only the last turns, as text', () => {
  const msgs = Array.from({ length: 14 }, (_, i) => ({ role: (i % 2 ? 'assistant' : 'user') as 'user' | 'assistant', text: `m${i}`, turn: { answer: '', calls: [], batches: [], source: 'brief' } }));
  const h = historyForEngine(msgs);
  assert.equal(h.length, 10);
  assert.deepEqual(h[0], { role: 'user', text: 'm4' });
  assert.ok(!('turn' in h[0]!));
});

test('dictation audio: downsample, end of utterance, and Whisper noise', () => {
  assert.equal(downsample(new Float32Array(48000), 48000).length, 16000);
  const u = createUtterance({ endSilenceMs: 100, minSpeechMs: 50 });
  const loud = new Float32Array(1600).fill(0.1);
  const quiet = new Float32Array(800);
  assert.equal(u.push(quiet), 'waiting');
  assert.equal(u.push(loud), 'speaking');
  assert.equal(u.push(quiet), 'speaking');
  assert.equal(u.push(quiet), 'done');
  assert.ok(u.heard);
  assert.equal(cleanTranscript(' [BLANK_AUDIO] get the backlog (music) ready '), 'get the backlog ready');
});
