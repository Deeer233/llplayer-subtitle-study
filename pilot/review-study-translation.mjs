import { mkdir, mkdtemp, stat, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { cache, readJson } from './media.mjs';
import { auditHtml } from './study-audit-form.mjs';

const cell = (value) => (value ?? '').replace(/[\r\n]+/g, ' ').replaceAll('|', '\\|').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
const seriousKinds = ['negation', 'relationship', 'literal-phrase', 'other'];
const validContext = (lines) => Array.isArray(lines) && lines.length <= 2 && lines.every((line) => typeof line === 'string');

async function main([mode, reportPath, gradesPath]) {
  if (!reportPath || !['prepare', 'score'].includes(mode)) throw new Error('Usage: review-study-translation.mjs prepare <report.json> | score <report.json> <grades.json>');
  const report = await readJson(reportPath);
  const { dataset, requestMode, rows } = report;
  const verified = dataset === 'operator-marked-verified';
  if (verified && requestMode !== 'playback-and-lookup-separated')
    throw new Error('Verified reports must measure playback translation separately from word lookup. Rerun the verified samples.');
  const provenance = [report.playerAssemblySha256, report.inputSha256, report.localModel?.sha256];
  if (verified && !provenance.every((hash) => typeof hash === 'string' && /^[0-9a-f]{64}$/.test(hash)))
    throw new Error('Verified reports must identify the packaged player, input snapshot and local model. Rerun the verified samples.');
  if ((!verified && dataset !== 'unverified-episode-pilot') || !Array.isArray(rows) || rows.length !== 100
    || rows.filter((row) => row.target).length !== 50 || new Set(rows.map((row) => row.id)).size !== 100) {
    throw new Error('Expected 100 unique episode rows with exactly 50 targets.');
  }
  if (verified && rows.some(({ before, after }) => !validContext(before) || !validContext(after)))
    throw new Error('Verified reports must preserve the English context on both sides. Rerun the verified samples.');
  const reportId = createHash('sha256').update(JSON.stringify([dataset, requestMode, provenance, ...rows.map(({ id, line, target, before, after, answer }) =>
    [id, line, target ?? null, before ?? null, after ?? null, answer?.chinese ?? null, answer?.expression ?? null,
      answer?.lemma ?? null, answer?.sense ?? null])])).digest('hex');
  if (mode === 'prepare' && !gradesPath) {
    await mkdir(cache, { recursive: true });
    const dir = await mkdtemp(join(cache, verified ? 'study-blind-' : 'study-preview-'));
    const html = join(dir, 'BLIND.html');
    const grades = { reportId, rows: rows.map(({ id, target, answer }) => ({ id,
      translationOk: answer?.chinese ? null : false, senseOk: target && !answer?.sense ? false : null, serious: [] })) };
    const formRows = await Promise.all(rows.map(async ({ id, line, target, before, after, answer }) => {
      const row = { id, line, target, before: validContext(before) ? before : [], after: validContext(after) ? after : [], answer };
      if (/^cue-\d+$/.test(id)) {
        const clip = join(dirname(resolve(reportPath)), 'clips', `${id}.wav`);
        const info = await stat(clip).catch(() => null);
        if (info?.isFile() && info.size > 44) row.clip = relative(dir, clip).split(sep).map(encodeURIComponent).join('/');
      }
      return row;
    }));
    const guide = ['# 译文与本句义盲评', '', verified ? '英文按操作者提交的核对标志生成。' : '预览：源字幕尚未人工核对，不能用来判定发布门槛。',
      '逐条判断译文和本句义是否保留原意。空答算错。HTML 界面可展开两侧英文语境，判断时只给当前句评分；不要把已有简中字幕当标准答案。',
      '在 `grades.json` 把正确项标为 `true`，错误项标为 `false`。无目标词的 `senseOk` 保留 `null`。严重错误标记：`negation`（否定译反）、`relationship`（人物关系错）、`literal-phrase`（短语字面误译）、`other`（其他）。',
      '', '| 编号 | 英文 | 译文 | 目标词 | 表达 / 原形 | 本句义 |', '|---|---|---|---|---|---|'];
    await Promise.all([
      writeFile(join(dir, 'BLIND.md'), [...guide, ...rows.map(({ id, line, target, answer }) =>
        `| ${cell(id)} | ${cell(line)} | ${cell(answer?.chinese)} | ${cell(target)} | ${cell(answer?.expression)} / ${cell(answer?.lemma)} | ${cell(answer?.sense)} |`), ''].join('\n'), { flag: 'wx' }),
      writeFile(join(dir, 'grades.json'), JSON.stringify(grades, null, 2), { flag: 'wx' }),
      writeFile(html, auditHtml('blind', { rows: formRows, grades }, !verified), { flag: 'wx' }),
    ]);
    return { dir, html, scoreAllowed: verified };
  }
  if (mode !== 'score' || !gradesPath || !verified) throw new Error('Scoring requires an operator-verified report and the completed grades file.');
  const gradeSheet = await readJson(gradesPath);
  if (gradeSheet?.reportId !== reportId) throw new Error('Grade sheet does not match this exact blind report. Prepare a new sheet.');
  const grades = gradeSheet.rows;
  if (!Array.isArray(grades) || grades.length !== rows.length || new Set(grades.map((row) => row.id)).size !== rows.length)
    throw new Error('Grade IDs must be unique and cover the entire report.');
  const byId = new Map(grades.map((row) => [row.id, row]));
  let translations = 0, senses = 0;
  const severe = Object.fromEntries(seriousKinds.map((kind) => [kind, 0]));
  for (const { id, target, answer } of rows) {
    const g = byId.get(id);
    if (!g || typeof g.translationOk !== 'boolean' || (target ? typeof g.senseOk !== 'boolean' : g.senseOk !== null)
      || (g.translationOk && !answer?.chinese) || (g.senseOk && !answer?.sense) || !Array.isArray(g.serious)
      || g.serious.some((kind) => !seriousKinds.includes(kind))) throw new Error(`Missing or invalid grade: ${id}`);
    translations += Number(g.translationOk);
    senses += Number(g.senseOk === true);
    for (const kind of new Set(g.serious)) severe[kind]++;
  }
  return { translations, translationTotal: 100, senses, senseTotal: 50, severe, meetsThresholds: translations >= 90 && senses >= 45 };
}

main(process.argv.slice(2)).then((result) => console.log(JSON.stringify(result, null, 2)))
  .catch((error) => { console.error(error.message); process.exitCode = 1; });
