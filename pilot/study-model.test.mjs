import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { cache } from './media.mjs';

const exec = promisify(execFile);
const script = fileURLToPath(new URL('./check-study-model.ps1', import.meta.url));
const run = () => exec('pwsh', ['-NoProfile', '-File', script], {
  env: { ...process.env, OLLAMA_NO_CLOUD: '1', LLPLAYER_STUDY_LOCAL_KEY: '' }, timeout: 25_000,
});

test('player client accepts loopback replies, keeps valid Chinese, and declines cloud models', async (t) => {
  const requests = [];
  let remote = false;
  const reply = (res, data) => res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(data));
  const server = createServer(async (req, res) => {
    if (req.method === 'GET' && req.url === '/api/tags') {
      return reply(res, { models: [{ name: 'qwen3:4b', ...(remote ? { remote_host: 'https://invalid.example' } : {}) }] });
    }
    if (req.method !== 'POST' || req.url !== '/api/chat') return res.writeHead(404).end();
    let text = '';
    for await (const chunk of req) text += chunk;
    const payload = JSON.parse(text);
    const row = JSON.parse(payload.messages[1].content);
    requests.push({ payload, row });
    const content = row.line.startsWith('I thought') ? { zh: '\u6211\u4ee5\u4e3a\u4f60\u8981\u653e\u6211\u9e3d\u5b50', certain: true,
      expression: 'bail on', lemma: 'bail on', sense: '\u653e\u9e3d\u5b50' }
      : { zh: '\u6d4b\u8bd5\u8bd1\u6587', certain: true, expression: row.target, lemma: 1 };
    reply(res, { message: { content: JSON.stringify(content) } });
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(11434, '127.0.0.1', resolve);
  });
  t.after(() => new Promise((resolve) => server.close(resolve)));

  const { stdout } = await run();
  const report = JSON.parse(stdout);
  assert.equal(report.qualityScored, false);
  assert.equal(report.translated, 8);
  assert.equal(report.contextualCandidates, 1);
  assert.equal(report.rows[0].answer.lemma, 'bail on');
  assert.equal(report.rows[1].answer.sense, null);
  assert.equal(requests.length, 8);
  for (const { payload, row } of requests) {
    assert.equal(payload.model, 'qwen3:4b');
    assert.equal(payload.think, false);
    assert.deepEqual(Object.keys(row).sort(), ['after', 'before', 'line', 'target']);
  }

  remote = true;
  await assert.rejects(run, (error) => {
    assert.equal(error.code, 2);
    assert.equal(JSON.parse(error.stdout).translated, 0);
    return true;
  });
  assert.equal(requests.length, 8, 'no chat request is sent for a remote model');
});

test('candidate uses the assigned loopback port and key and discards incomplete responses', async (t) => {
  const key = 'a'.repeat(64);
  const requests = [];
  const server = createServer(async (req, res) => {
    let text = '';
    for await (const chunk of req) text += chunk;
    requests.push({ url: req.url, authorization: req.headers.authorization, payload: JSON.parse(text) });
    const finish_reason = requests.length === 1 ? 'length' : 'stop';
    const row = JSON.parse(requests.at(-1).payload.messages[1].content);
    const content = JSON.stringify(row.line === 'They came back.' ? row.target
      ? { zh: '\u4ed6\u4eec\u56de\u6765\u4e86', certain: true, expression: 'came back', lemma: 'come back', sense: '\u56de\u6765' }
      : { zh: 'no translation', certain: false }
      : { zh: '\u6d4b\u8bd5\u8bd1\u6587', certain: false });
    res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ choices: [{ finish_reason, message: { content } }] }));
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const env = { ...process.env, OLLAMA_NO_CLOUD: '', LLPLAYER_STUDY_LOCAL_KEY: key,
    LLPLAYER_STUDY_LOCAL_PORT: String(server.address().port) };
  await assert.rejects(exec('pwsh', ['-NoProfile', '-File', script], {
    env, timeout: 25_000,
  }), (error) => {
    assert.equal(error.code, 2);
    const report = JSON.parse(error.stdout);
    assert.equal(report.translated, 7);
    assert.equal(report.rows[0].answer, null);
    return true;
  });
  assert.equal(requests.length, 8);
  for (const { url, authorization, payload } of requests) {
    assert.equal(url, '/v1/chat/completions');
    assert.equal(authorization, `Bearer ${key}`);
    assert.equal(payload.model, 'study-qwen3-4b');
    assert.equal(payload.chat_template_kwargs.enable_thinking, false);
    assert.deepEqual(payload.response_format.schema.required, ['zh', 'expression', 'lemma', 'sense', 'certain']);
    assert.deepEqual(Object.keys(JSON.parse(payload.messages[1].content)).sort(), ['after', 'before', 'line', 'target']);
  }
  const { stdout } = await exec('pwsh', ['-NoProfile', '-File', script, '-Probe8B'], {
    env, timeout: 25_000,
  });
  assert.equal(JSON.parse(stdout).translated, 8);
  assert.equal(requests.length, 16);
  assert.ok(requests.slice(8).every(({ payload }) => payload.model === 'study-qwen3-8b'));

  await mkdir(cache, { recursive: true });
  const dir = await mkdtemp(join(cache, 'playback-model-fixture-'));
  const samples = join(dir, 'samples.json');
  await writeFile(samples, JSON.stringify([
    { id: 'unanswered-translation', line: 'They came back.', target: 'came', verified: true },
    { id: 'translation-only', line: 'We were waiting.', verified: true },
  ]), { flag: 'wx' });
  const split = await exec('pwsh', ['-NoProfile', '-File', script, '-Samples', samples], {
    env, timeout: 25_000,
  });
  const batch = JSON.parse(split.stdout);
  assert.equal(batch.requestMode, 'playback-and-lookup-separated');
  assert.equal(batch.translated, 1);
  assert.equal(batch.contextualCandidates, 1);
  assert.equal(batch.rows[0].answer.chinese, null, 'the word-card translation must not fill a playback blank');
  assert.equal(batch.rows[0].answer.lemma, 'come back');
  assert.deepEqual(requests.slice(16).map(({ payload }) => JSON.parse(payload.messages[1].content).target), ['', 'came', '']);

  await assert.rejects(exec('pwsh', ['-NoProfile', '-File', script], {
    env: { ...env, LLPLAYER_STUDY_LOCAL_PORT: `127.0.0.1:${server.address().port}` }, timeout: 25_000,
  }), (error) => {
    assert.equal(error.code, 2);
    assert.equal(JSON.parse(error.stdout).translated, 0);
    return true;
  });
  assert.equal(requests.length, 19, 'an invalid port must not cause any requests');
});
