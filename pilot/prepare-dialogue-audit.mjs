import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { cache, english, extractWav, probe, scan } from './media.mjs';

const expressions = /\b(?:look into|look out|look like|make a living|put up with|turn [\w ]{2,15}? in|turn (?:on|off)|take (?:off|care)|go ahead|get (?:away|out|in|back|over|up)|give up|clean (?:this |it )?up|show up|hold up|find out|come on|cook up|come up|the bomb|big deal|got busted|dumb-ass|smart-ass|all right|going on|work out|think about|drive by|piece of|end up|rip off|pinched a loaf|come here|no matter|kicked?|guess|busted|bust|deal|live|cook(?:ing)?|gotta|gonna|wanna|gimme|shit|ass|hell|stuck|cover|figured?|charge|high|straight|screw|damn|beats?|mess|handle|batch|flask|rehab|negotiate|buddy|dude|weird)\b/i;
const compact = (s) => s.trim().replace(/\s+/gu, ' ');
const spread = (rows, n) => Array.from({ length: n }, (_, i) => rows[Math.floor((i + .5) * rows.length / n)]);
const clock = (ms) => `${Math.floor(ms / 60_000)}:${(ms % 60_000 / 1000).toFixed(3).padStart(6, '0')}`;
const textCell = (text) => text.replaceAll('|', '\\|').replaceAll('<', '&lt;').replaceAll('>', '&gt;');

async function main() {
  if (process.argv.length !== 3) throw new Error('Usage: node pilot/prepare-dialogue-audit.mjs <episode>');
  const { video, cues, source, index, durationMs } = await scan(process.argv[2]);
  if (!source || !(durationMs > 0)) throw new Error('Need readable English subtitles and known duration');
  const { streams } = await probe(video);
  const audio = streams.find((s) => s.codec_type === 'audio' && english(s.tags?.language));
  if (!audio) throw new Error('No audio stream tagged English; no track was assumed');

  const eligible = cues.filter(({ text, startMs, endMs }) => /^[\s"'“‘]*[A-Z]/u.test(text) && /[.?!]["'”’]*$/u.test(text)
    && !/(^|\n)\s*[-–—]|\[[^\]]*\]|\([^)]*\)|[♪♫<>]|\.\.\.|…|--/u.test(text)
    && (text.match(/\b[A-Za-z]+\b/g) ?? []).length >= 3 && endMs <= durationMs && endMs - startMs <= 9_000);
  if (eligible.length < 100) throw new Error(`Only ${eligible.length} eligible dialogue candidates`);
  const colloquial = eligible.filter((cue) => expressions.test(compact(cue.text)));
  const targetCues = colloquial.length >= 50 ? spread(colloquial, 50) : [...colloquial,
    ...spread(eligible.filter((cue) => !expressions.test(compact(cue.text))), 50 - colloquial.length)];
  const targetIds = new Set(targetCues.map((cue) => cue.id));
  const chosen = [...targetCues, ...spread(eligible.filter((cue) => !targetIds.has(cue.id)), 50)].sort((a, b) => a.id - b.id);

  await mkdir(cache, { recursive: true });
  const dir = await mkdtemp(join(cache, 'dialogue-audit-'));
  const clips = join(dir, 'clips');
  await mkdir(clips);
  const rows = [];
  for (const cue of chosen) {
    const id = `cue-${String(cue.id).padStart(4, '0')}`;
    const clip = `clips/${id}.wav`;
    const line = compact(cue.text);
    const target = !targetIds.has(cue.id) ? null : line.match(expressions)?.[0]
      ?? (line.match(/[a-z]+(?:['’][a-z]+)*/gi) ?? []).sort((a, b) => b.length - a.length)[0];
    const sample = { id, verified: false, line, before: cues.slice(Math.max(0, cue.id - 3), cue.id - 1).map((c) => compact(c.text)),
      after: cues.slice(cue.id, cue.id + 2).map((c) => compact(c.text)), ...(target ? { target } : {}) };
    const clipStartMs = Math.max(0, cue.startMs - 1_250);
    const clipEndMs = Math.min(durationMs, cue.endMs + 1_250);
    rows.push({ sample, clip, clipStartMs, startMs: cue.startMs, endMs: cue.endMs, clipEndMs });
  }
  for (let i = 0; i < rows.length; i += 4) {
    await Promise.all(rows.slice(i, i + 4).map((row) => extractWav(video, audio.index, row.clipStartMs / 1000,
      (row.clipEndMs - row.clipStartMs) / 1000, join(dir, row.clip))));
  }
  const heading = ['# 100 条真实对白：英文听校', '', '只用于本机验收。当前 100 条都未核对。',
    '听原声，用 `samples.json` 核对同编号的英文。若原声不同，修正 `line`；不同说话者不能拼在一条。必要时改 `target` 或换例句，保留 100 条、其中 50 条带目标词。',
    '确认后把对应 `verified` 改成 `true`。听不清的先留 `false`。必要时用播放器听前后上下文，旧字幕不是正确答案。',
    '', '| 编号 / 原声 | 字幕起点 | 目标 | 英文候选 |', '|---|---:|---|---|'];
  await Promise.all([
    writeFile(join(dir, 'samples.json'), JSON.stringify(rows.map(({ sample }) => sample), null, 2), { flag: 'wx' }),
    writeFile(join(dir, 'AUDIT.md'), [...heading, ...rows.map(({ sample, clip, startMs }) =>
      `| [${sample.id}](${clip}) | ${clock(startMs)} | ${textCell(sample.target ?? '')} | ${textCell(sample.line)} |`), ''].join('\n'), { flag: 'wx' }),
  ]);
  console.log(JSON.stringify({ dir, episode: video, source, subtitleIndex: index, audioIndex: audio.index, samples: rows.length,
    targetSamples: targetIds.size, unverified: rows.length, colloquialCandidates: colloquial.length }, null, 2));
}

main().catch((error) => { console.error(error.message); process.exitCode = 1; });
