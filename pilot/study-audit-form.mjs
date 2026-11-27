import { stat, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { readJson } from './media.mjs';

export function anchorTime(row, positionSeconds) {
  if (!Number.isFinite(positionSeconds) || positionSeconds < 0) return null;
  const value = row.clipStartMs + Math.round(positionSeconds * 1000);
  return value < row.clipEndMs ? value : null;
}

const theme = `
  :root{color-scheme:light dark;font:16px/1.55 system-ui,sans-serif}
  body{margin:0} main{max-width:60rem;padding:1.25rem;margin:auto}
  header{border-bottom:1px solid GrayText;padding-bottom:.7rem}
  h1{font-size:1.55rem;margin:0 0 .6rem} h2{font-size:1.1rem} h3{font-size:1rem}
  .tools,.field{display:flex;flex-wrap:wrap;align-items:center;gap:.55rem}
  .field,.section{margin:.8rem 0} .section{border-bottom:1px solid GrayText;padding-bottom:.8rem}
  button,input,select,textarea{font:inherit} button,select{padding:.25rem .6rem}
  audio{height:2.7rem;max-width:100%;vertical-align:middle}
  textarea{display:block;box-sizing:border-box;width:100%;margin:.3rem 0 .7rem}
  input[type=number]{width:8rem} [role=alert]{color:CanvasText;font-weight:600}
  output{font-variant-numeric:tabular-nums} p{overflow-wrap:anywhere}
  label{max-width:100%} details{margin:.6rem 0} .marker{color:GrayText}
`;

// This function is serialized into a self-contained local page; all sample text is rendered via textContent.
function form({ mode, data, preview }, markAnchor) {
  const byId = (id) => document.getElementById(id);
  const el = (tag, text, parent) => {
    const node = document.createElement(tag);
    if (text != null) node.textContent = text;
    parent?.append(node);
    return node;
  };
  const isTiming = mode === 'timing', isTranscript = mode === 'transcript', isPlayback = mode === 'playback';
  const source = isTiming ? data.anchors.anchors : isTranscript ? data.samples : isPlayback ? data.rows : data.grades.rows;
  let values = structuredClone(source);
  let episode = isTiming ? data.anchors.episodeMatches : null;
  let i = 0;
  const title = isTiming ? '原声对轴' : isTranscript ? '英文对白听校' : isPlayback ? '播放器手动验收' : '译文与本句义盲评';
  document.title = title;
  byId('title').textContent = title;
  const content = byId('sample');
  const message = byId('message');
  const importFile = byId('import');
  const download = byId('download');
  if (isPlayback) {
    const header = byId('title').parentElement;
    el('p', `试用包：${data.folder}`, header);
    el('p', `剧集：${data.episode}`, header);
    el('p', '请实际操作播放器。没做的项目保留为未测；可随时导出进度。', header);
  }
  if (preview) {
    message.textContent = '未听校试跑。此表不导出发布评分。';
    importFile.parentElement.hidden = true;
    download.hidden = true;
  }
  const options = [['', '未填写'], ['true', '是'], ['false', '否']];
  const select = (parent, label, current, set, choices = options) => {
    const row = el('label', label + ' ', parent);
    const menu = el('select', null, row);
    for (const [key, name] of choices) el('option', name, menu).value = key;
    menu.value = current == null ? '' : String(current);
    menu.addEventListener('change', () => { set(menu.value === '' ? null : menu.value === 'true'); update(); });
    return menu;
  };
  const seek = el('select', null, byId('navigation'));
  seek.setAttribute('aria-label', '跳到编号');
  source.forEach((s, j) => { el('option', `${j + 1} · ${s.id ?? s.cue}`, seek).value = String(j); });
  const previous = byId('previous'), next = byId('next');
  const done = (s, j) => isTiming ? Number.isInteger(s.actualStartMs) && typeof s.sameLine === 'boolean'
    : isTranscript ? s.verified === true && !!s.line.trim() && (!source[j].target || !!s.target && s.line.toLowerCase().includes(s.target.toLowerCase()))
      : isPlayback ? s.status === 'pass' || s.status === 'fail'
        : typeof s.translationOk === 'boolean' && (!data.rows[j].target || typeof s.senseOk === 'boolean');
  function update() {
    byId('count').textContent = `${values.filter(done).length} / ${values.length} 已填`;
    previous.disabled = i === 0;
    next.disabled = i === values.length - 1;
    seek.value = String(i);
  }
  function play(src, parent) {
    const audio = el('audio', null, parent);
    audio.controls = true;
    audio.preload = 'none';
    audio.src = src;
    return audio;
  }
  function show(j) {
    i = Math.max(0, Math.min(values.length - 1, j));
    content.querySelector('audio')?.pause();
    content.replaceChildren();
    const value = values[i];
    if (isPlayback) {
      const row = data.steps[i];
      el('h2', row.title, content);
      el('p', row.step, content);
      el('p', `通过条件：${row.expect}`, content);
      const menu = el('select', null, el('label', '实际结果 ', content));
      for (const [key, name] of [['', '未测'], ['pass', '通过'], ['fail', '失败']]) el('option', name, menu).value = key;
      menu.value = value.status ?? '';
      menu.addEventListener('change', () => { value.status = menu.value || null; update(); });
      const note = el('textarea', null, el('label', '备注（可选）', content));
      note.rows = 2; note.maxLength = 500; note.value = value.note;
      note.addEventListener('input', () => { value.note = note.value; });
    } else if (isTiming) {
      const row = data.rows[i];
      select(content, '集数一致', episode, (v) => { episode = v; });
      el('h2', `字幕 ${row.cue} · ${['开头', '中段', '结尾'][row.third - 1]}`, content);
      el('p', row.text, content);
      const media = el('div', null, content);
      media.className = 'field';
      const audio = play(row.clip, media);
      const mark = el('button', '用当前播放位置标记起点', media);
      const rowTime = el('div', null, content);
      rowTime.className = 'field';
      const label = el('label', '原声起点（全片毫秒） ', rowTime);
      const time = el('input', null, label);
      time.type = 'number'; time.min = '0'; time.max = String(data.anchors.durationMs - 1); time.step = '1';
      time.value = value.actualStartMs ?? '';
      time.addEventListener('input', () => { value.actualStartMs = time.value && time.validity.valid ? time.valueAsNumber : null; update(); });
      mark.addEventListener('click', () => {
        if (audio.readyState === 0) { message.textContent = '先播放原声。'; return; }
        audio.pause();
        const absolute = markAnchor(row, audio.currentTime);
        if (absolute == null) { message.textContent = '请选择片段内的一个位置。'; return; }
        value.actualStartMs = absolute; time.value = absolute; message.textContent = ''; update();
      });
      el('output', `字幕起点 ${row.startMs} 毫秒`, rowTime);
      select(content, '原声和字幕是同一句', value.sameLine, (v) => { value.sameLine = v; });
    } else if (isTranscript) {
      el('h2', value.id, content);
      play(`clips/${value.id}.wav`, content);
      const row = el('div', null, content);
      row.className = 'section';
      const label = el('label', '英文对白', row);
      const line = el('textarea', null, label);
      line.rows = 2; line.value = value.line;
      const checkedLabel = el('label', null, content);
      const checked = el('input', null, checkedLabel);
      checked.type = 'checkbox'; checked.checked = value.verified;
      checkedLabel.append(' 已听校，英文和目标表达与原声一致');
      const invalidate = () => { value.verified = false; checked.checked = false; update(); };
      line.addEventListener('input', () => { value.line = line.value.trim(); invalidate(); });
      if (source[i].target) {
        const targetLabel = el('label', '目标表达 ', row);
        const target = el('input', null, targetLabel);
        target.value = value.target ?? '';
        target.addEventListener('input', () => { value.target = target.value.trim(); invalidate(); });
      }
      const context = el('details', null, row);
      el('summary', '前后语境', context);
      for (const [key, label] of [['before', '前文'], ['after', '后文']]) {
        const field = el('textarea', null, el('label', label, context));
        field.rows = 2;
        field.value = (value[key] ?? []).join('\n');
        field.addEventListener('input', () => { value[key] = field.value.split(/\r?\n/).map((s) => s.trim()).filter(Boolean); invalidate(); });
      }
      checked.addEventListener('change', () => {
        const needsTarget = !!source[i].target;
        const valid = !!value.line && (!needsTarget || !!value.target && value.line.toLowerCase().includes(value.target.toLowerCase()))
          && (value.before?.length ?? 0) <= 2 && (value.after?.length ?? 0) <= 2;
        value.verified = checked.checked && valid;
        if (checked.checked && !valid) message.textContent = '英文不能为空，目标词须在原句，前后语境各最多两行。';
        else message.textContent = '';
        checked.checked = value.verified; update();
      });
    } else {
      const row = data.rows[i];
      el('h2', row.id, content);
      el('p', row.line, content).lang = 'en';
      if (row.clip) play(row.clip, content);
      if (row.before?.length || row.after?.length) {
        const context = el('details', null, content);
        el('summary', '查看前后英文语境（不参与评分）', context);
        for (const [label, lines] of [['前文', row.before], ['后文', row.after]]) {
          if (!lines?.length) continue;
          el('strong', label, context);
          const list = el('ul', null, context);
          for (const line of lines) el('li', line, list).lang = 'en';
        }
      }
      el('p', row.answer?.chinese || '无译文', content);
      if (row.answer?.chinese) select(content, '译文保留原意', value.translationOk, (v) => { value.translationOk = v; });
      if (row.target) {
        el('h3', `目标表达：${row.target}`, content);
        el('p', [row.answer?.expression, row.answer?.lemma, row.answer?.sense].filter(Boolean).join(' / ') || '无本句义', content);
        if (row.answer?.sense) select(content, '本句义正确', value.senseOk, (v) => { value.senseOk = v; });
      }
      const flags = el('fieldset', null, content);
      flags.className = 'field';
      el('legend', '严重错误', flags);
      for (const [key, name] of [['negation', '否定译反'], ['relationship', '人物关系错'], ['literal-phrase', '短语字面误译'], ['other', '其他']]) {
        const label = el('label', null, flags);
        const flag = el('input', null, label);
        flag.type = 'checkbox'; flag.checked = value.serious.includes(key);
        label.append(name);
        flag.addEventListener('change', () => { value.serious = flag.checked ? [...value.serious, key] : value.serious.filter((s) => s !== key); });
      }
    }
    update();
  }
  function exportState() {
    if (isTiming) return { ...data.anchors, episodeMatches: episode, anchors: values };
    if (isTranscript) return values;
    if (isPlayback) return { checkId: data.checkId, rows: values };
    return { reportId: data.grades.reportId, rows: values };
  }
  importFile.addEventListener('change', async () => {
    try {
      const incoming = JSON.parse(await importFile.files[0].text());
      const rows = isTiming ? incoming.anchors : isTranscript ? incoming : incoming.rows;
      const choice = (x) => x === null || typeof x === 'boolean';
      const validRow = (v, j) => isTiming ? v?.cue === source[j].cue && choice(v.sameLine) && (v.actualStartMs === null
        || Number.isInteger(v.actualStartMs) && v.actualStartMs >= 0 && v.actualStartMs < data.anchors.durationMs)
        : isTranscript ? v?.id === source[j].id && typeof v.verified === 'boolean' && typeof v.line === 'string'
          && (source[j].target ? typeof v.target === 'string' : v.target == null)
          && [v.before, v.after].every((xs) => Array.isArray(xs) && xs.length <= 2 && xs.every((x) => typeof x === 'string'))
          : isPlayback ? v?.id === source[j].id && [null, 'pass', 'fail'].includes(v.status)
            && typeof v.note === 'string' && v.note.length <= 500
          : v?.id === source[j].id && choice(v.translationOk) && choice(v.senseOk) && Array.isArray(v.serious)
            && (source[j].translationOk !== false || v.translationOk === false) && (source[j].senseOk !== false || v.senseOk === false)
            && (!!data.rows[j].target || v.senseOk === null)
            && v.serious.every((key) => ['negation', 'relationship', 'literal-phrase', 'other'].includes(key));
      if (!Array.isArray(rows) || rows.length !== source.length || !rows.every(validRow)
        || (isTiming && (incoming.durationMs !== data.anchors.durationMs || !choice(incoming.episodeMatches)))
        || (isPlayback ? incoming.checkId !== data.checkId : !isTiming && !isTranscript && incoming.reportId !== data.grades.reportId)) throw new Error();
      if (isTiming) episode = incoming.episodeMatches;
      values = rows; show(i);
      message.textContent = '已载入填写进度。';
    } catch { message.textContent = '无法载入：格式错误，或与本表样本不一致。'; }
    importFile.value = '';
  });
  download.addEventListener('click', () => {
    if (preview) return;
    const blob = new Blob([JSON.stringify(exportState(), null, 2)], { type: 'application/json' });
    const href = URL.createObjectURL(blob);
    const link = el('a', null, document.body);
    link.href = href;
    link.download = isTiming ? 'anchors-filled.json' : isTranscript ? 'samples-verified.json' : isPlayback ? 'playback-check.json' : 'grades-filled.json';
    link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(href), 1000);
  });
  previous.addEventListener('click', () => show(i - 1));
  next.addEventListener('click', () => show(i + 1));
  seek.addEventListener('change', () => show(Number(seek.value)));
  show(0);
}

export function auditHtml(mode, data, preview = false) {
  const json = JSON.stringify({ mode, data, preview }).replaceAll('<', '\\u003c');
  return `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; media-src file:; connect-src 'none'">
<title>本机验收</title><style>${theme}</style><body><main><header><h1 id="title"></h1>
<div class="tools"><span id="count" aria-live="polite"></span><button id="download">导出进度</button>
<label>载入进度 <input id="import" type="file" accept="application/json,.json"></label></div></header>
<div class="tools field" id="navigation"><button id="previous">上一条</button><button id="next">下一条</button></div>
<article id="sample"></article><p id="message" role="alert"></p></main>
<script id="data" type="application/json">${json}</script>
<script>(${form.toString()})(JSON.parse(document.getElementById('data').textContent), ${anchorTime.toString()});</script></body></html>`;
}

async function saveListeningForms([alignmentDir, transcriptDir]) {
  if (!alignmentDir || !transcriptDir) throw new Error('Usage: node study-audit-form.mjs <alignment-directory> <dialogue-audit-directory>');
  const [anchors, { rows }, samples] = await Promise.all([
    readJson(join(alignmentDir, 'anchors.json')), readJson(join(alignmentDir, 'clips.json')), readJson(join(transcriptDir, 'samples.json')),
  ]);
  if (rows.length !== 10 || anchors.anchors.length !== rows.length || rows.some((r, i) => r.cue !== anchors.anchors[i].cue || !/^cue-\d+\.wav$/.test(r.clip)))
    throw new Error('Expected 10 matching alignment anchors and existing clips.');
  if (samples.length !== 100 || samples.filter((r) => r.target).length !== 50 || samples.some((r) => !/^cue-\d+$/.test(r.id)))
    throw new Error('Expected 100 subtitle samples and 50 target expressions.');
  await Promise.all([...rows.map((r) => join(alignmentDir, r.clip)), ...samples.map((r) => join(transcriptDir, 'clips', `${r.id}.wav`))].map(async (clip) => {
    const f = await stat(clip);
    if (!f.isFile() || f.size <= 44) throw new Error(`Missing usable audio: ${clip}`);
  }));
  const alignment = resolve(alignmentDir, 'ALIGNMENT.html'), transcript = resolve(transcriptDir, 'TRANSCRIPT.html');
  await Promise.all([
    writeFile(alignment, auditHtml('timing', { anchors, rows }), { flag: 'wx' }),
    writeFile(transcript, auditHtml('transcript', { samples }), { flag: 'wx' }),
  ]);
  return { alignment, transcript };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  saveListeningForms(process.argv.slice(2)).then((result) => console.log(JSON.stringify(result, null, 2)))
    .catch((error) => { console.error(error.message); process.exitCode = 1; });
}
