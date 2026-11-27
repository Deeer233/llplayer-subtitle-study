import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { cache } from './media.mjs';

const cli = fileURLToPath(new URL('./review-study-translation.mjs', import.meta.url));
const run = async (...args) => JSON.parse((await promisify(execFile)(process.execPath, [cli, ...args])).stdout);

test('blind scores belong to the exact answers and unverified results cannot be scored', async () => {
  await mkdir(cache, { recursive: true });
  const dir = await mkdtemp(join(cache, 'blind-fixture-'));
  const put = async (name, data) => {
    const path = join(dir, name);
    await writeFile(path, JSON.stringify(data), { flag: 'wx' });
    return path;
  };
  const rows = Array.from({ length: 100 }, (_, i) => ({ id: `fixture-${i}`, line: 'Synthetic line',
    before: i === 0 ? ['The earlier line sets the scene.'] : [], after: [],
    target: i < 50 ? 'line' : null, answer: { chinese: 'Synthetic translation', sense: i < 50 ? 'Synthetic sense' : null } }));
  const dataset = 'operator-marked-verified';
  const requestMode = 'playback-and-lookup-separated';
  const identity = { playerAssemblySha256: 'a'.repeat(64), inputSha256: 'b'.repeat(64), localModel: { sha256: 'c'.repeat(64) } };
  const report = await put('original.json', { dataset, requestMode, rows, ...identity });
  const prep = await run('prepare', report);
  const html = await readFile(prep.html, 'utf8');
  assert.match(html, /The earlier line sets the scene/);
  const sheet = JSON.parse(await readFile(join(prep.dir, 'grades.json'), 'utf8'));
  const first = await put('grades.json', { ...sheet, rows: sheet.rows.map((row, i) => ({ ...row,
    translationOk: i < 90, senseOk: i < 50 ? i < 45 : null, serious: i === 95 ? ['negation'] : [] })) });
  const score = await run('score', report, first);
  assert.equal(score.meetsThresholds, true);
  assert.equal(score.translations, 90);
  assert.equal(score.senses, 45);
  assert.equal(score.severe.negation, 1);

  const changedContext = rows.map((row, i) => i === 0 ? { ...row, after: ['Different context.'] } : row);
  const contextual = await put('different-context.json', { dataset, requestMode, rows: changedContext, ...identity });
  await assert.rejects(run('score', contextual, first), { stderr: /does not match this exact blind report/ });
  const missingContext = rows.map((row, i) => i === 0 ? { ...row, before: undefined } : row);
  const noContext = await put('missing-context.json', { dataset, requestMode, rows: missingContext, ...identity });
  await assert.rejects(run('prepare', noContext), { stderr: /must preserve the English context/ });

  for (const [i, change] of [
    { playerAssemblySha256: 'd'.repeat(64) }, { inputSha256: 'e'.repeat(64) }, { localModel: { sha256: 'f'.repeat(64) } },
  ].entries()) {
    const different = await put(`different-${i}.json`, { dataset, requestMode, rows, ...identity, ...change });
    await assert.rejects(run('score', different, first), { stderr: /does not match this exact blind report/ });
  }
  rows[0].answer.sense = 'Different answer, same sample ID';
  const changed = await put('changed.json', { dataset, requestMode, rows, ...identity });
  await assert.rejects(run('score', changed, first), { stderr: /does not match this exact blind report/ });
  const legacy = await put('legacy.json', { dataset, rows });
  await assert.rejects(run('prepare', legacy), { stderr: /measure playback translation separately/ });
  const withoutProvenance = await put('without-provenance.json', { dataset, requestMode, rows });
  await assert.rejects(run('prepare', withoutProvenance), { stderr: /must identify the packaged player/ });
  const unverified = await put('unverified.json', { dataset: 'unverified-episode-pilot', rows });
  await assert.rejects(run('score', unverified, first), { stderr: /requires an operator-verified report/ });
});
