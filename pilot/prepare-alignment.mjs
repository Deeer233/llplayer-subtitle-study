import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { basename, join } from 'node:path';
import { cache, english, extractWav, probe, scan } from './media.mjs';

const stamp = (ms) => [Math.floor(ms / 3_600_000), Math.floor(ms / 60_000) % 60, Math.floor(ms / 1_000) % 60]
  .map((n) => String(n).padStart(2, '0')).join(':') + `.${String(ms % 1000).padStart(3, '0')}`;

function pickCues(cues, durationMs) {
  const candidates = cues.filter(({ text, startMs, endMs }) => endMs <= durationMs && startMs >= 0 && endMs - startMs <= 8_000
    && !/[[\]()\u266a\u266b]/u.test(text) && !/^\s*-|\n\s*-/u.test(text) && (text.match(/[A-Za-z]+/g) ?? []).length >= 4);
  const used = new Set();
  return [3, 4, 3].flatMap((count, third) => {
    const lane = candidates.filter((cue) => Math.floor(3 * cue.startMs / durationMs) === third);
    if (lane.length < count) throw new Error(`Not enough dialogue cues in third ${third + 1}`);
    return Array.from({ length: count }, (_, i) => {
      const target = (third + (i + .5) / count) * durationMs / 3;
      const cue = lane.filter((x) => !used.has(x.id))
        .reduce((best, x) => !best || Math.abs(x.startMs - target) < Math.abs(best.startMs - target) ? x : best, null);
      used.add(cue.id);
      return { ...cue, third: third + 1 };
    }).sort((a, b) => a.startMs - b.startMs);
  });
}

async function main(args) {
  if (args.length !== 1) throw new Error('Usage: node pilot/prepare-alignment.mjs <episode.mkv|episode.mp4>');
  const { video, source, index, durationMs, cues, srt } = await scan(args[0]);
  if (!source || !(durationMs > 0)) throw new Error('Need readable English subtitles and known duration');
  const { streams } = await probe(video);
  const audio = streams.find((stream) => stream.codec_type === 'audio' && english(stream.tags?.language));
  if (!audio) throw new Error('No audio stream tagged English; no track was assumed');
  const selected = pickCues(cues, durationMs);
  await mkdir(cache, { recursive: true });
  const dir = await mkdtemp(join(cache, 'alignment-'));
  const rows = [];
  for (const { id, third, startMs, endMs, text } of selected) {
    const clipStartMs = Math.max(0, startMs - 3_000);
    const clipEndMs = Math.min(durationMs, endMs + 3_000);
    const clip = `cue-${String(id).padStart(4, '0')}.wav`;
    await extractWav(video, audio.index, clipStartMs / 1_000, (clipEndMs - clipStartMs) / 1_000, join(dir, clip));
    rows.push({ cue: id, third, startMs, endMs, clipStartMs, clipEndMs, text, clip });
  }
  const sheet = ['# 原声对轴抽查', '', `样本：${basename(video)}。音轨流索引：${audio.index}。`,
    '先听片段，记下目标字幕对应的起点。片段从字幕预计起点前约 3 秒开始，但这不是实测起点。',
    '在 `anchors.json` 填入：`actualStartMs` = 片段起点（毫秒）+ 听到第一个目标音节的片内毫秒数。对白一致填 `sameLine: true`，否则填 `false`；听不清就留空。集数核对后再填写 `episodeMatches`。',
    '', '| 段 | 字幕编号 | 原声 | 片段起点（毫秒） | 字幕起点 | 英文字幕 |', '|---|---:|---|---:|---|---|',
    ...rows.map((row) => `| ${['首', '中', '尾'][row.third - 1]} | ${row.cue} | [原声](${row.clip}) | ${row.clipStartMs} | ${stamp(row.startMs)} | ${row.text.replaceAll('|', '\\|').replace(/[<>]/g, '').replace(/\r?\n/g, ' / ')} |`), ''];
  const anchors = { episodeMatches: null, durationMs, anchors: rows.map(({ cue }) => ({ cue, actualStartMs: null, sameLine: null })) };
  const files = { 'AUDIT.md': sheet.join('\n'), 'english.srt': srt, 'anchors.json': JSON.stringify(anchors, null, 2),
    'clips.json': JSON.stringify({ video, durationMs, audioIndex: audio.index, source, subtitleIndex: index, rows }, null, 2) };
  await Promise.all(Object.entries(files).map(([name, data]) => writeFile(join(dir, name), data, { flag: 'wx' })));
  console.log(JSON.stringify({ dir, source, subtitleIndex: index, cueCount: cues.length, audioIndex: audio.index,
    durationMs, exported: rows.length, verification: 'unchecked' }, null, 2));
}

main(process.argv.slice(2)).catch((error) => { console.error(error.message); process.exitCode = 1; });
