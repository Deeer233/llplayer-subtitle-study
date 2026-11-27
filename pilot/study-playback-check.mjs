import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { basename, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { cache, readJson } from './media.mjs';
import { auditHtml } from './study-audit-form.mjs';

// A checklist records the operator's observations; it does not control or inspect a window.
export const playbackSteps = [
  { id: 'open', title: '打开、画面和扬声器', step: '在资源管理器打开上面显示的试用包目录，双击 Start-Local-Translation.cmd。等播放器打开，把下面的 MKV 拖进去。', expect: '画面连续播放，扬声器能听到英文原声，出现内嵌英文字幕。' },
  { id: 'on-demand', title: '按需翻译只显示当前句', step: '跳到 2:17，切到按需模式。点「本句翻译」，再播到下一句。', expect: '只显示当前句的简中，下一句恢复纯英文，不沿用上一句中文。' },
  { id: 'bilingual', title: '自动双语', step: '切到「自动双语」，连续看两句，再跳到 6:16 左右。', expect: '有匹配的简中时持续显示；无匹配时，本地模型只补当前句，不拿附近字幕顶替。模型尚未返回时保留英文。' },
  { id: 'lookup', title: '点词、短语和关闭词卡', step: '回到 2:17，在播放时点 live，关卡；暂停后再选中 live at，打开并关卡。', expect: '点词暂停，能看到「住在（某地）」的本句义或明确标注的词典参考。主动选中的短语可查询；关卡后恢复点词前的播放或暂停状态。' },
  { id: 'replay', title: '两处原声重听', step: '在 2:17，分别从播放页和词卡按「重听本句」。', expect: '两处都从约 2:15 播到约 2:21 后暂停。实际听到本句原声，不必手拖进度条。' },
  { id: 'resume', title: '重开记住模式和进度', step: '选「自动双语」，跳到约 2:17。关闭播放器，再从同一包打开并拖入同一 MKV。', expect: '接近原进度，仍为「自动双语」。' },
  { id: 'verification', title: '字幕来源和原声核对', step: '找一句没核对过的对白，观察来源标记。点「核对本句」，重听后按实际情况确认或取消。', expect: '内嵌来源和原声核对状态分开；未确认前不显示为已核对。确认只改变本地核对记录。' },
];
const episode = 'D:\\绝命毒师\\01.mkv';

export async function playbackChecklist(packageDir) {
  const folder = resolve(packageDir);
  const assembly = await readFile(join(folder, 'LLPlayer.dll'));
  const assemblySha256 = createHash('sha256').update(assembly).digest('hex');
  const checkId = createHash('sha256').update(JSON.stringify([1, basename(folder), assemblySha256, episode, playbackSteps])).digest('hex');
  return { folder, episode, steps: playbackSteps, checkId,
    rows: playbackSteps.map(({ id }) => ({ id, status: null, note: '' })) };
}

export function assessPlayback(checklist, input) {
  if (input?.checkId !== checklist.checkId || !Array.isArray(input.rows) || input.rows.length !== checklist.rows.length
    || !input.rows.every((row, i) => row?.id === checklist.rows[i].id && [null, 'pass', 'fail'].includes(row.status)
      && typeof row.note === 'string' && row.note.length <= 500)) {
    throw new Error('Playback export does not match this package and checklist.');
  }
  const failed = input.rows.filter((row) => row.status === 'fail').map(({ id }) => id);
  const untested = input.rows.filter((row) => row.status === null).map(({ id }) => id);
  return { passed: !failed.length && !untested.length, failed, untested, checkId: checklist.checkId,
    verification: 'operator-reported' };
}

async function main([action, packageDir, exportPath, ...extra]) {
  if (!packageDir || extra.length || !((action === 'prepare' && !exportPath) || (action === 'check' && exportPath)))
    throw new Error('Usage: study-playback-check.mjs prepare <package> | check <package> <playback-check.json>');
  const data = await playbackChecklist(packageDir);
  if (action === 'check') {
    const result = assessPlayback(data, await readJson(exportPath));
    if (!result.passed) process.exitCode = 2;
    return result;
  }
  await mkdir(cache, { recursive: true });
  const dir = await mkdtemp(join(cache, 'playback-check-'));
  const html = join(dir, 'PLAYBACK.html');
  await writeFile(html, auditHtml('playback', data), { flag: 'wx' });
  return { html, checkId: data.checkId };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then((result) => console.log(JSON.stringify(result, null, 2)))
    .catch((error) => { console.error(error.message); process.exitCode = 1; });
}
