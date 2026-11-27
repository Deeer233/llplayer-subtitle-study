import assert from 'node:assert/strict';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import { anchorTime, auditHtml } from './study-audit-form.mjs';

class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.handlers = {}; this.value = ''; this.readyState = 0; }
  append(c) { this.children.push(c); }
  addEventListener(name, fn) { this.handlers[name] = fn; }
  querySelector(tag) { return this.all(tag)[0]; }
  replaceChildren() { this.children = []; }
  setAttribute() {}
  pause() { this.paused = true; }
  click() { return this.handlers.click?.(); }
  remove() {}
  all(tag) { return this.children.flatMap((c) => c instanceof Element ? (c.tag === tag ? [c, ...c.all(tag)] : c.all(tag)) : []); }
}

function start(html) {
  const controls = Object.fromEntries(['title', 'count', 'download', 'import', 'navigation', 'previous', 'next', 'sample', 'message', 'data']
    .map((id) => [id, new Element(id)]));
  controls.import.parentElement = new Element('label');
  controls.data.textContent = /<script id="data" type="application\/json">([^]*?)<\/script>/.exec(html)[1];
  const script = /<script>([^]*?)<\/script>/.exec(html)[1];
  const document = { getElementById: (id) => controls[id], createElement: (tag) => new Element(tag), body: new Element('body') };
  let saved;
  runInNewContext(script, { document, structuredClone, Blob, setTimeout: () => {}, URL: { createObjectURL: (value) => { saved = value; return 'blob:test'; } } });
  return { controls, saved: async () => JSON.parse(await saved.text()), fromFile: async (data) => {
    controls.import.files = [{ text: async () => JSON.stringify(data) }];
    await controls.import.handlers.change();
  } };
}

test('timing page exports only an explicit original-audio position and accepts partial progress', async () => {
  const row = { cue: 22, third: 1, startMs: 3400, clipStartMs: 1000, clipEndMs: 7000, text: 'Dialogue', clip: 'cue-22.wav' };
  assert.equal(anchorTime(row, 6), null);
  assert.equal(anchorTime(row, 2.5236), 3524);
  const anchors = { episodeMatches: null, durationMs: 10000, anchors: [{ cue: 22, actualStartMs: null, sameLine: null }] };
  const page = start(auditHtml('timing', { rows: [row], anchors }));
  const { controls } = page;
  const audio = controls.sample.querySelector('audio');
  assert.equal(audio.src, 'cue-22.wav');
  const mark = controls.sample.querySelector('button');
  await mark.click();
  await controls.download.click();
  assert.deepEqual(await page.saved(), anchors);
  audio.readyState = 2; audio.currentTime = 2.524;
  await mark.click();
  for (const select of controls.sample.all('select')) { select.value = 'true'; select.handlers.change(); }
  assert.equal(audio.paused, true);
  assert.equal(controls.count.textContent, '1 / 1 已填');
  await controls.download.click();
  const result = await page.saved();
  assert.deepEqual(result, { episodeMatches: true, durationMs: 10000, anchors: [{ cue: 22, actualStartMs: 3524, sameLine: true }] });
  await page.fromFile(result);
  assert.equal(controls.sample.querySelector('input').value, 3524);
});

test('listening page requires confirmation after edits and keeps embedded dialogue as data', async () => {
  const unsafeText = '</script><script>throw new Error("not data")</script>';
  const html = auditHtml('transcript', { samples: [{ id: 'cue-0010', line: unsafeText, before: [], after: [], target: 'throw', verified: false }] });
  assert.doesNotMatch(html, /<script>throw/);
  const page = start(html);
  const { controls } = page;
  assert.equal(controls.sample.querySelector('audio').src, 'clips/cue-0010.wav');
  const [line] = controls.sample.all('textarea');
  const target = controls.sample.all('input').find((e) => e.type !== 'checkbox');
  const check = controls.sample.all('input').find((e) => e.type === 'checkbox');
  line.value = 'I live here.'; line.handlers.input();
  check.checked = true; check.handlers.change();
  assert.equal(check.checked, false);
  target.value = 'live'; target.handlers.input();
  check.checked = true; check.handlers.change();
  assert.equal(controls.count.textContent, '1 / 1 已填');
  line.value = 'I live at home.'; line.handlers.input();
  await controls.download.click();
  assert.deepEqual(await page.saved(), [{ id: 'cue-0010', line: 'I live at home.', before: [], after: [], target: 'live', verified: false }]);
});

test('playback checklist exports only explicit observations and rejects another checklist', async () => {
  const rows = [{ id: 'open', status: null, note: '' }, { id: 'replay', status: null, note: '' }];
  const steps = rows.map(({ id }) => ({ id, title: id, step: 'Manual action', expect: 'Expected result' }));
  const page = start(auditHtml('playback', { folder: 'Test package', episode: 'Test episode', checkId: 'fixture', rows, steps }));
  const { controls } = page;
  assert.equal(controls.count.textContent, '0 / 2 已填');
  const menu = controls.sample.querySelector('select');
  menu.value = 'fail'; menu.handlers.change();
  const note = controls.sample.querySelector('textarea');
  note.value = 'No sound'; note.handlers.input();
  await controls.download.click();
  const result = await page.saved();
  assert.deepEqual(result, { checkId: 'fixture', rows: [{ id: 'open', status: 'fail', note: 'No sound' }, rows[1]] });
  await page.fromFile({ ...result, checkId: 'wrong' });
  assert.match(controls.message.textContent, /无法载入/);
  await page.fromFile(result);
  assert.equal(controls.sample.querySelector('textarea').value, 'No sound');
  assert.equal(controls.count.textContent, '1 / 2 已填');
});

test('blind page exports explicit grades for the exact report and disables preview export', async () => {
  const contextLine = '</script><script>throw new Error("not data")</script>';
  const data = { rows: [{ id: 'cue-0010', line: 'I live here.', before: ['Where do you live?'], after: [contextLine],
    target: 'live', clip: '../clips/cue-0010.wav', answer: { chinese: '住', sense: '居住' } }],
    grades: { reportId: 'fixed-report', rows: [{ id: 'cue-0010', translationOk: null, senseOk: null, serious: [] }] } };
  const page = start(auditHtml('blind', data));
  const { controls } = page;
  const details = controls.sample.querySelector('details');
  assert.equal(Boolean(details.open), false);
  assert.deepEqual(details.all('li').map((item) => item.textContent), ['Where do you live?', contextLine]);
  const [chinese, sense] = controls.sample.all('select');
  chinese.value = 'false'; chinese.handlers.change();
  sense.value = 'true'; sense.handlers.change();
  const [serious] = controls.sample.all('input');
  serious.checked = true; serious.handlers.change();
  await controls.download.click();
  const result = await page.saved();
  assert.deepEqual(result, { reportId: 'fixed-report', rows: [{ id: 'cue-0010', translationOk: false, senseOk: true, serious: ['negation'] }] });
  await page.fromFile({ ...result, reportId: 'other-report' });
  assert.match(controls.message.textContent, /无法载入/);
  const preview = start(auditHtml('blind', data, true));
  assert.equal(preview.controls.download.hidden, true);
  await preview.controls.download.click();
  assert.match(preview.controls.message.textContent, /不导出发布评分/);
});
