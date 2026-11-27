import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { promisify } from 'node:util';
import { assessTimeline, cache, parseSrt, scan } from './media.mjs';
import { parseTranslation, translate } from './providers.mjs';

const exec = promisify(execFile);
const entry = fileURLToPath(new URL('./feasibility.mjs', import.meta.url));
const ffmpeg = (...args) => exec('ffmpeg', ['-nostdin', '-v', 'error', '-n', ...args], { timeout: 20_000 });
const block = (text) => `1\n00:00:00,100 --> 00:00:01,200\n${text}\n`;

test('real FFmpeg: embedded English wins; local sidecar works; missing English only reports its absence', async () => {
  await mkdir(cache, { recursive: true });
  const dir = await mkdtemp(join(cache, 'sample-'));
  const embedded = join(dir, 'embedded.mp4');
  const standalone = join(dir, 'only-sidecar.mkv');
  const bare = join(dir, 'without-subs.mp4');
  const chinese = join(dir, 'chinese-only.mkv');
  const seed = join(dir, 'seed.srt');
  const zh = join(dir, 'chinese-only.srt');
  const line = 'I thought you were going to bail on me.';
  const sidecar = 'I was here with you all of the time.';
  await Promise.all([writeFile(seed, block(line)), writeFile(zh, block('\u6211\u4ee5\u4e3a\u4f60\u8981\u653e\u6211\u9e3d\u5b50')),
    writeFile(join(dir, 'embedded.en.srt'), block(sidecar)), writeFile(join(dir, 'only-sidecar.srt'), block(sidecar))]);
  await ffmpeg('-f', 'lavfi', '-i', 'color=s=64x64:r=10:d=2', '-f', 'lavfi', '-i', 'sine=f=440:d=2',
    '-c:v', 'mpeg4', '-c:a', 'aac', bare);
  await Promise.all([
    ffmpeg('-i', bare, '-i', seed, '-map', '0', '-map', '1:s:0', '-c', 'copy', '-c:s', 'mov_text', '-metadata:s:s:0', 'language=eng', embedded),
    ffmpeg('-i', bare, '-c', 'copy', standalone),
    ffmpeg('-i', bare, '-i', zh, '-map', '0', '-map', '1:s:0', '-c', 'copy', '-metadata:s:s:0', 'language=zho', chinese),
  ]);
  const results = await Promise.all([embedded, standalone, bare, chinese].map(scan));
  assert.deepEqual(results.map((x) => x.source), ['embedded', 'sidecar', null, null]);
  assert.equal(results[0].cues[0].text, line);
  assert.equal(results[0].verification, 'unchecked');
  assert.equal(results[1].cues[0].text, sidecar);
  for (const result of results.slice(2)) {
    assert.equal(result.status, 'no_english_subtitles');
    assert.equal(result.cueCount, 0);
    assert.ok(result.message);
    assert.equal(result.next, undefined);
  }
  const { stdout } = await exec(process.execPath, [entry, 'scan', embedded]);
  assert.equal(JSON.parse(stdout).source, 'embedded');
  const noSubtitles = JSON.parse((await exec(process.execPath, [entry, 'scan', bare])).stdout);
  assert.equal(noSubtitles.status, 'no_english_subtitles');
  assert.equal(noSubtitles.next, undefined);
});

const actualStarts = [2, 11, 20, 28, 34, 42, 52, 72, 79, 88].map((value) => value * 1_000);
const anchors = actualStarts.map((actualStartMs, i) => ({ cue: i + 1, actualStartMs, sameLine: true }));
const sample = { episodeMatches: true, durationMs: 90_000, anchors };
const cuesAt = (delta) => actualStarts.map((actual, i) => ({ startMs: actual - delta(i), endMs: actual - delta(i) + 1_000 }));

test('timebase: 9/10 within 500 ms passes; a uniform offset needs an explicit switch', () => {
  assert.equal(assessTimeline(cuesAt((i) => i === 9 ? 501 : 500), sample).within500Ms, 9);
  assert.equal(assessTimeline(cuesAt((i) => i === 9 ? 501 : 500), sample).passed, true);
  assert.equal(assessTimeline(cuesAt(() => 1_200), sample).passed, false);
  const adjusted = assessTimeline(cuesAt(() => 1_200), sample, true);
  assert.deepEqual([adjusted.passed, adjusted.offsetMs, adjusted.within500Ms, adjusted.automaticallyAdopted], [true, 1_200, 10, false]);
});

test('timebase: refuse wrong episode, wrong lines, drifting versions and missing-third evidence', () => {
  const clean = cuesAt(() => 0);
  assert.deepEqual(assessTimeline(clean, { ...sample, episodeMatches: false }).reasons, ['episode_mismatch']);
  assert.deepEqual(assessTimeline(clean, { ...sample, anchors: [{ ...anchors[0], sameLine: false }, ...anchors.slice(1)] }).reasons,
    ['dialogue_mismatch']);
  assert.equal(assessTimeline(cuesAt((i) => i * 200), sample, true).reasons.includes('timeline_drift'), true);
  assert.throws(() => assessTimeline(clean, { ...sample, anchors: anchors.map((a) => ({ ...a, actualStartMs: 100 })) }), /each third/);
});

test('CLI does not advertise or accept subtitle search or transcription', async () => {
  const { stdout } = await exec(process.execPath, [entry, '--help']);
  assert.match(stdout, /<scan\|timeline\|translate>/);
  assert.doesNotMatch(stdout, /search|transcribe/);
  for (const command of ['search', 'transcribe']) {
    await assert.rejects(() => exec(process.execPath, [entry, command, 'unused.mkv']),
      { code: 1, stderr: `Unknown command: ${command}\n` });
  }
});

test('translation verifier withholds uncertain or ungrounded senses and rejects malformed translations', () => {
  const answer = { zh: '\u4f60\u653e\u4e86\u6211\u9e3d\u5b50', expression: 'bailed on', lemma: 'bail on', sense: '\u653e\u9e3d\u5b50', certain: true };
  const line = 'You bailed on me.';
  const parse = (overrides = {}) => parseTranslation(JSON.stringify({ ...answer, ...overrides }), line, 'bailed');
  assert.equal(parse().senseStatus, 'candidate');
  assert.deepEqual(parse({ expression: 'bail on' }), { zh: answer.zh, senseStatus: 'uncertain' });
  assert.equal(parse({ certain: false }).senseStatus, 'uncertain');
  assert.throws(() => parse({ zh: 'not translated' }), /Invalid structured translation/);
});

test('translation integration: sends only dialogue and context to loopback and excludes unverified lines', async (t) => {
  await mkdir(cache, { recursive: true });
  const dir = await mkdtemp(join(cache, 'translation-test-'));
  const sampleFile = join(dir, 'dialogues.json');
  const unverifiedFile = join(dir, 'unverified.json');
  const row = { id: 'fixture-1', line: 'You bailed on me.', before: ['We had plans.'], after: ['I am sorry.'],
    target: 'bailed', verified: true, privatePath: 'C:\\only-local.mp4' };
  await Promise.all([writeFile(sampleFile, JSON.stringify([row])), writeFile(unverifiedFile, JSON.stringify([{ ...row, verified: false }]))]);
  const environment = process.env.OLLAMA_NO_CLOUD;
  t.after(() => { if (environment === undefined) delete process.env.OLLAMA_NO_CLOUD; else process.env.OLLAMA_NO_CLOUD = environment; });
  process.env.OLLAMA_NO_CLOUD = '1';
  const request = t.mock.method(globalThis, 'fetch', async (url, config) => {
    assert.equal(new URL(url).origin, 'http://127.0.0.1:11434');
    assert.equal(config.redirect, 'error');
    if (url.endsWith('/api/tags')) return Response.json({ models: [{ name: 'qwen3:4b', digest: 'test-only' }] });
    assert.equal(url, 'http://127.0.0.1:11434/api/chat');
    assert.deepEqual(JSON.parse(JSON.parse(config.body).messages[1].content),
      { line: row.line, before: row.before, after: row.after, target: row.target });
    return Response.json({ message: { content: JSON.stringify({ zh: '\u4f60\u653e\u4e86\u6211\u9e3d\u5b50',
      expression: 'bailed on', lemma: 'bail on', sense: '\u653e\u9e3d\u5b50', certain: true }) } });
  });
  await assert.rejects(() => translate(unverifiedFile, {}), /human-verified/);
  assert.equal(request.mock.callCount(), 0);
  assert.equal((await translate(sampleFile, {})).status, 'awaiting_blind_review');
  assert.equal(request.mock.callCount(), 2);
});

test('SRT discards invalid times and labels cues in chronological order', () => {
  assert.deepEqual(parseSrt(`\ufeff3\r\n00:00:03.000 --> 00:00:04.000\r\nThere!\r\n\r\n${block('<i>Hello!</i>')}\n4\n00:00:60,000 --> 00:01:01,000\nInvalid`), [
    { id: 1, startMs: 100, endMs: 1200, text: 'Hello!' }, { id: 2, startMs: 3000, endMs: 4000, text: 'There!' },
  ]);
});
