import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { copyFile, mkdir, mkdtemp, readFile, readdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { cache } from './media.mjs';
import { playbackChecklist } from './study-playback-check.mjs';

const exec = promisify(execFile);
const put = async (dir, name, data) => {
  const path = join(dir, name);
  await writeFile(path, typeof data === 'string' ? data : JSON.stringify(data), { flag: 'wx' });
  return path;
};

test('acceptance entry validates real form shape, gates inference, and preserves result codes', async () => {
  const root = await mkdtemp(join(cache, 'acceptance-synthetic-'));
  const pilot = join(root, 'pilot');
  const dialogue = join(root, '.pilot', 'dialogue-audit-RZkQbF');
  const alignment = join(root, '.pilot', 'alignment-EDNCKS');
  const packageDir = join(root, '.pilot', 'player-20261125-231635-248');
  const previousPackageDir = join(root, '.pilot', 'player-20261125-223342-909');
  await Promise.all([pilot, dialogue, alignment, packageDir, previousPackageDir, join(dialogue, 'clips')]
    .map((p) => mkdir(p, { recursive: true })));
  const assembly = await put(packageDir, 'LLPlayer.dll', 'Synthetic package identity');
  await copyFile(assembly, join(previousPackageDir, 'LLPlayer.dll'));
  const names = ['run-study-acceptance.ps1', 'review-study-translation.mjs', 'study-audit-form.mjs',
    'feasibility.mjs', 'media.mjs', 'providers.mjs', 'study-playback-check.mjs'];
  await Promise.all(names.map((name) => copyFile(fileURLToPath(new URL(name, import.meta.url)), join(pilot, name))));
  const entry = join(pilot, names[0]);
  const run = (...args) => exec('pwsh', ['-NoProfile', '-File', entry, ...args], { timeout: 30_000, windowsHide: true });
  const json = async (...args) => JSON.parse((await run(...args)).stdout);

  const { checkId, rows: playbackRows } = await playbackChecklist(packageDir);
  const blank = await put(root, 'playback-blank.json', { checkId, rows: playbackRows });
  await assert.rejects(run('-Playback', blank), (e) => {
    const result = JSON.parse(e.stdout);
    assert.equal(e.code, 2);
    assert.equal(result.passed, false);
    assert.deepEqual(result.failed, []);
    assert.deepEqual(result.untested, playbackRows.map(({ id }) => id));
    return true;
  });
  const partial = await put(root, 'playback-partial.json', { checkId, rows: playbackRows.map((row, i) =>
    i === 0 ? { ...row, status: 'fail', note: 'No sound' } : row) });
  await assert.rejects(run('-Playback', partial), (e) => {
    const result = JSON.parse(e.stdout);
    assert.equal(e.code, 2);
    assert.deepEqual(result.failed, ['open']);
    assert.equal(result.untested.length, playbackRows.length - 1);
    return true;
  });
  const allPassed = await put(root, 'playback-synthetic-pass.json', { checkId,
    rows: playbackRows.map((row) => ({ ...row, status: 'pass' })) });
  const playback = await json('-Playback', allPassed);
  assert.equal(playback.passed, true);
  assert.equal(playback.verification, 'operator-reported');
  const previous = await playbackChecklist(previousPackageDir);
  const previousPass = await put(root, 'playback-previous-package-synthetic.json', { checkId: previous.checkId,
    rows: previous.rows.map((row) => ({ ...row, status: 'pass' })) });
  await assert.rejects(run('-Playback', previousPass), {
    code: 1, stderr: /does not match this package and checklist/,
  });

  const times = [1000, 2000, 3000, 4500, 5500, 6500, 7500, 8500, 9500, 10500];
  const clock = (ms) => `00:00:${String(Math.floor(ms / 1000)).padStart(2, '0')},${String(ms % 1000).padStart(3, '0')}`;
  await put(alignment, 'english.srt', times.map((ms, i) => `${i + 1}\n${clock(ms)} --> ${clock(ms + 200)}\nSynthetic line.\n`).join('\n'));
  const anchors = times.map((actualStartMs, i) => ({ cue: i + 1, actualStartMs, sameLine: true }));
  const timing = { episodeMatches: true, durationMs: 12_000, anchors };
  await put(alignment, 'anchors.json', { ...timing, episodeMatches: null,
    anchors: anchors.map(({ cue }) => ({ cue, actualStartMs: null, sameLine: null })) });
  assert.equal((await json('-Anchors', await put(root, 'anchors.json', timing))).within500Ms, 10);
  const wrongEpisode = await put(root, 'wrong-episode.json', { ...timing, episodeMatches: false });
  await assert.rejects(run('-Anchors', wrongEpisode), (e) => e.code === 2 && JSON.parse(e.stdout).reasons.includes('episode_mismatch'));
  const incomplete = await put(root, 'incomplete.json', { ...timing, episodeMatches: null });
  await assert.rejects(run('-Anchors', incomplete), { stderr: /Alignment export is incomplete/ });

  const samples = Array.from({ length: 100 }, (_, i) => ({ id: `cue-${String(i + 1).padStart(4, '0')}`,
    line: 'Synthetic line.', before: [], after: [], verified: false, ...(i < 50 ? { target: 'line' } : {}) }));
  const template = await put(dialogue, 'samples.json', samples);
  await assert.rejects(run('-Samples', template), { stderr: /100 of 100 rows are not confirmed/ });
  assert.deepEqual((await readdir(dialogue)).sort(), ['clips', 'samples.json']);
  const edited = samples.map((row) => ({ ...row, verified: true }));
  const wrongTarget = await put(root, 'wrong-target.json', [{ ...edited[0], target: 'missing' }, ...edited.slice(1)]);
  await assert.rejects(run('-Samples', wrongTarget), { stderr: /Target missing from the English line/ });
  assert.deepEqual((await readdir(dialogue)).sort(), ['clips', 'samples.json']);

  const data = edited.map(({ id, line, target, before, after }, i) => ({ id, line, target, before, after,
    answer: i === 99 ? null : { chinese: 'Synthetic answer', sense: target && i !== 49 ? 'Synthetic sense' : null } }));
  const content = JSON.stringify({ dataset: 'operator-marked-verified', requestMode: 'playback-and-lookup-separated', rows: data });
  // Isolated fixture: this launcher never invokes the real model or writes to the actual audit directory.
  await put(pilot, 'start-study-candidate.ps1', `param([switch]$DynamicPort,[string]$Server,[string]$Model,[string]$Samples,[string]$SamplesPackage,[string]$Report)
if (!$DynamicPort -or !(Test-Path -LiteralPath $Samples -PathType Leaf) -or !$SamplesPackage) { throw 'Missing validated snapshot or package.' }
$data = ConvertFrom-Json -AsHashtable -InputObject @'
${content}
'@
$data.playerAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $SamplesPackage 'LLPlayer.dll')).Hash.ToLowerInvariant()
$data.inputSha256 = (Get-FileHash -LiteralPath $Samples).Hash.ToLowerInvariant()
$data.localModel = @{ sha256 = 'a' * 64 }
[IO.File]::WriteAllText($Report, (ConvertTo-Json -Depth 8 -InputObject $data))
Write-Warning 'Some sample sentences returned no translation.'
`);
  await writeFile(join(dialogue, 'clips', 'cue-0001.wav'), Buffer.alloc(45), { flag: 'wx' });
  const input = await put(root, 'valid-synthetic-samples.json', edited);
  const prepared = await json('-Samples', input);
  assert.deepEqual(JSON.parse(await readFile(prepared.verifiedInput, 'utf8')), edited);
  assert.match(await readFile(prepared.html, 'utf8'), /clips\/cue-0001\.wav/);
  assert.notEqual((await json('-Samples', input)).report, prepared.report, 'next run must not overwrite a report');

  const grades = JSON.parse(await readFile(join(prepared.html, '..', 'grades.json'), 'utf8'));
  assert.equal(grades.rows[99].translationOk, false, 'blank translation is pre-marked incorrect');
  assert.equal(grades.rows[49].senseOk, false, 'blank sense is pre-marked incorrect');
  grades.rows = grades.rows.map((row, i) => ({ ...row, translationOk: i < 90, senseOk: i < 50 ? i < 45 : null }));
  const passing = await put(root, 'passing.json', grades);
  const score = await json('-Report', prepared.report, '-Grades', passing);
  assert.deepEqual([score.translations, score.senses, score.meetsThresholds], [90, 45, true]);
  const blankMarkedCorrect = await put(root, 'blank-marked-correct.json', { ...grades,
    rows: grades.rows.map((row, i) => i === 99 ? { ...row, translationOk: true } : row) });
  await assert.rejects(run('-Report', prepared.report, '-Grades', blankMarkedCorrect), { stderr: /cue-0100/ });
  grades.rows[44].senseOk = false;
  const failing = await put(root, 'failing.json', grades);
  await assert.rejects(run('-Report', prepared.report, '-Grades', failing), (e) => e.code === 2 && JSON.parse(e.stdout).senses === 44);
  const wrongReport = await put(root, 'wrong-report.json', { ...grades, reportId: 'wrong' });
  await assert.rejects(run('-Report', prepared.report, '-Grades', wrongReport), { stderr: /does not match this exact blind report/ });
  await writeFile(assembly, 'Different package identity');
  await assert.rejects(run('-Report', prepared.report, '-Grades', passing), { stderr: /not produced by the current packaged/ });
  await assert.rejects(run('-Playback', allPassed), { stderr: /does not match this package and checklist/ });
});
