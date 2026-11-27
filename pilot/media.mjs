import { execFile } from 'node:child_process';
import { readFile, readdir, stat } from 'node:fs/promises';
import { basename, dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const exec = promisify(execFile);
export const cache = fileURLToPath(new URL('../.pilot/', import.meta.url));
export const videoTypes = ['.mp4', '.mkv'];
const types = ['.srt', '.ass', '.ssa', '.vtt'];
const codecs = new Set(['subrip', 'ass', 'ssa', 'mov_text', 'webvtt', 'text', 'sami', 'microdvd']);
export const english = (tag = '') => /^(en|eng)(?:[-_][a-z0-9]+)*$/i.test(tag);
const functionWords = new Set('a an and are as at be but can could did do does for from had has have he here him his how i if in is it me my no not of on or our she so that the their them there they this to up us was we were what when where who will with would you your'.split(' '));

export function parseSrt(value) {
  const cues = [];
  for (const block of value.trim().split(/\r?\n[\t ]*\r?\n/)) {
    const lines = block.split(/\r?\n/);
    const row = /^\d+\s*$/.test(lines[0]) ? 1 : 0;
    const match = /^\s*(\d+:\d{2}:\d{2}[,.]\d{3})\s+-->\s+(\d+:\d{2}:\d{2}[,.]\d{3})(?:\s.*)?$/.exec(lines[row]);
    if (!match) continue;
    const [startMs, endMs] = match.slice(1).map((s) => {
      const [h, m, sec, ms] = s.split(/[:,.]/).map(Number);
      return m < 60 && sec < 60 ? ((h * 60 + m) * 60 + sec) * 1_000 + ms : NaN;
    });
    const text = lines.slice(row + 1).join('\n').replace(/<\/?(?:[ibus]|font)(?:\s[^>]*?)?>/gi, '').trim();
    if (Number.isSafeInteger(startMs) && Number.isSafeInteger(endMs) && endMs > startMs && text) cues.push({ startMs, endMs, text });
  }
  return cues.sort((a, b) => a.startMs - b.startMs).map((cue, i) => ({ id: i + 1, ...cue }));
}

function looksEnglish(cues, tagged) {
  const text = cues.slice(0, 200).map((cue) => cue.text).join(' ').toLowerCase();
  const letters = text.match(/\p{L}/gu) ?? [];
  const words = text.match(/[a-z]+/g) ?? [];
  const hits = words.filter((word) => functionWords.has(word));
  if (!letters.length || words.join('').length / letters.length < .95) return false;
  if (tagged && words.length < 12) return new Set(hits).size >= 2;
  return hits.length >= Math.max(3, words.length * .18) && new Set(hits).size >= 3;
}

export async function localFile(input, exts) {
  const path = resolve(input);
  if (!exts.includes(extname(path).toLowerCase()) || !(await stat(path)).isFile()) {
    throw new Error(`Not a supported local file (${exts.join(', ')}): ${path}`);
  }
  return path;
}

export async function readJson(input) {
  return JSON.parse((await readFile(await localFile(input, ['.json']), 'utf8')).replace(/^\uFEFF/, ''));
}

async function run(tool, args) {
  const { stdout } = await exec(tool, args, { timeout: 120_000, maxBuffer: 16 * 1024 * 1024, windowsHide: true });
  return stdout;
}

const demuxers = { '.mkv': 'matroska', '.mp4': 'mov', '.srt': 'srt', '.ass': 'ass', '.ssa': 'ass', '.vtt': 'webvtt', '.wav': 'wav' };
const inputFormat = (path) => ['-protocol_whitelist', 'file,pipe', '-f', demuxers[extname(path).toLowerCase()], '-i', path];

export async function probe(video) {
  return JSON.parse(await run('ffprobe', ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', ...inputFormat(video)]));
}

export async function normalizeSrt(path, index) {
  return run('ffmpeg', ['-nostdin', '-v', 'error', ...inputFormat(path),
    '-map', index == null ? '0:s:0' : `0:${index}`, '-c:s', 'srt', '-f', 'srt', 'pipe:1']);
}

function sidecarHint(name, stem) {
  if (!types.includes(extname(name).toLowerCase())) return null;
  const tag = basename(name, extname(name)).toLowerCase();
  if (tag === stem) return 'untagged';
  if (tag.startsWith(`${stem}.`) && english(tag.slice(stem.length + 1))) return 'tagged';
  return null;
}

export async function scan(input) {
  const video = await localFile(input, videoTypes);
  const { streams, format } = await probe(video);
  if (!streams?.some((s) => s.codec_type === 'video' && !s.disposition?.attached_pic)) throw new Error('No video stream found');
  const tracks = streams.filter((s) => s.codec_type === 'subtitle').map((s) => ({
    index: s.index, codec: s.codec_name, language: s.tags?.language?.toLowerCase() ?? 'und',
  }));
  const base = { video, durationMs: Math.round(Number(format.duration) * 1_000), tracks, attempts: [] };
  const stem = basename(video, extname(video)).toLowerCase();
  const sidecars = (await readdir(dirname(video), { withFileTypes: true })).filter((entry) => entry.isFile())
    .map(({ name }) => ({ name, hint: sidecarHint(name, stem) })).filter(({ hint }) => hint !== null)
    .sort((a, b) => Number(b.hint === 'tagged') - Number(a.hint === 'tagged') || a.name.localeCompare(b.name));
  const inputs = [
    ...tracks.filter((t) => codecs.has(t.codec) && english(t.language))
      .map(({ index }) => ({ path: video, index, source: 'embedded', hint: 'tagged' })),
    ...sidecars.map(({ name, hint }) => ({ path: join(dirname(video), name), source: 'sidecar', hint })),
    ...tracks.filter((t) => codecs.has(t.codec) && ['', 'und', 'unk'].includes(t.language))
      .map(({ index }) => ({ path: video, index, source: 'embedded', hint: 'untagged' })),
  ];
  for (const { path, index, source, hint } of inputs) {
    let srt;
    try { srt = await normalizeSrt(path, index); }
    catch (error) {
      if (error.code === 'ENOENT') throw error;
      base.attempts.push({ path, index, source, status: 'read_failed' });
      continue;
    }
    const cues = parseSrt(srt);
    if (cues.length && looksEnglish(cues, hint === 'tagged')) {
      return { ...base, source, path, index, languageEvidence: hint === 'tagged' ? 'tag+text_heuristic' : 'text_heuristic',
        verification: 'unchecked', cueCount: cues.length, cues, srt };
    }
    base.attempts.push({ path, index, source, status: 'empty_or_english_not_confirmed' });
  }
  return { ...base, source: null, cueCount: 0, status: 'no_english_subtitles', message: '没有找到可用的英文字幕。' };
}

const median = (values) => {
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
};

export function assessTimeline(cues, { episodeMatches, durationMs, anchors }, allowOffset = false) {
  if (typeof episodeMatches !== 'boolean' || !Number.isSafeInteger(durationMs) || durationMs <= 0
      || !Array.isArray(anchors) || anchors.length !== 10 || new Set(anchors.map((x) => x?.cue)).size !== 10) {
    throw new Error('Expected episodeMatches, durationMs and 10 distinct cue anchors');
  }
  const points = anchors.map((x) => {
    const cue = Number.isSafeInteger(x?.cue) ? cues[x.cue - 1] : null;
    if (!cue || !Number.isSafeInteger(x.actualStartMs) || x.actualStartMs < 0 || x.actualStartMs >= durationMs
        || typeof x.sameLine !== 'boolean') throw new Error('Invalid anchor: cue, actualStartMs or sameLine');
    return { ...x, third: Math.floor(3 * x.actualStartMs / durationMs), deltaMs: x.actualStartMs - cue.startMs };
  });
  const groups = [0, 1, 2].map((third) => points.filter((p) => p.third === third).map((p) => p.deltaMs));
  if (groups.some((group) => group.length < 2)) throw new Error('Need at least 2 anchors in each third of the video');
  const groupOffsets = groups.map(median);
  const driftMs = Math.max(...groupOffsets) - Math.min(...groupOffsets);
  const countWithin = (offset) => points.filter((p) => Math.abs(p.deltaMs - offset) <= 500).length;
  const rawWithin500Ms = countWithin(0);
  const offsetMs = allowOffset && rawWithin500Ms < 9 ? Math.round(median(points.map((p) => p.deltaMs))) : 0;
  const within500Ms = countWithin(offsetMs);
  const outOfBounds = cues.some((c) => c.startMs + offsetMs < 0 || c.endMs + offsetMs > durationMs + 500);
  const reasons = [];
  if (!episodeMatches) reasons.push('episode_mismatch');
  if (points.some((point) => !point.sameLine)) reasons.push('dialogue_mismatch');
  if (driftMs > 1_000) reasons.push('timeline_drift');
  if (within500Ms < 9 || outOfBounds) reasons.push('outside_timing_tolerance');
  return { passed: reasons.length === 0, reasons, offsetMs, rawWithin500Ms, within500Ms, driftMs,
    verification: 'human_sample_only', automaticallyAdopted: false };
}

export async function extractWav(video, audioIndex, startSeconds, seconds, wav) {
  await run('ffmpeg', ['-nostdin', '-v', 'error', '-n', '-ss', String(startSeconds), ...inputFormat(video),
    '-map', `0:${audioIndex}`, '-t', String(seconds), '-ac', '1', '-ar', '16000', '-c:a', 'pcm_s16le', wav]);
}

export { types as subtitleTypes };
