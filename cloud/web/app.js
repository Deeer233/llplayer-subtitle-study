const $ = id => document.getElementById(id);
const notice = text => { $('notice').textContent = text; };
const show = name => { for (const id of ['login','today','quiz','words']) $(id).hidden = id !== name; };
class HttpError extends Error { constructor(status) { super(String(status)); this.status = status; } }
async function api(path, body) {
  const res = await fetch(path, {cache:'no-store', signal:AbortSignal.timeout(8000), ...(body === undefined ? {} : {
    method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(body)
  })});
  if (!res.ok) throw new HttpError(res.status);
  return res.json();
}
const db = new Promise((resolve,reject) => {
  const request = indexedDB.open('original-review',1);
  request.onupgradeneeded = () => request.result.createObjectStore('private');
  request.onsuccess = () => resolve(request.result);
  request.onerror = () => reject(request.error);
});
async function local(edit) {
  const conn = await db;
  return new Promise((resolve,reject) => {
    const tx = conn.transaction('private', edit ? 'readwrite' : 'readonly', {durability:'strict'});
    const store = tx.objectStore('private'), read = store.get('state');
    let result;
    read.onsuccess = () => {
      result = read.result ?? {pending:[]};
      if (edit) { result = edit(result); result ? store.put(result,'state') : store.delete('state'); }
    };
    tx.oncomplete = () => resolve(result);
    tx.onabort = tx.onerror = () => reject(tx.error);
  });
}
const sw = 'serviceWorker' in navigator && navigator.serviceWorker.register('/sw.js').catch(() => null);
const audio = new Audio();
let group, offline = false, connected = false, sessionGeneration = 0, cacheRun = 0, busy = false;
let queue = [], makeup = [], question, again = false, played = false, hinted = false, objectUrl;
const broadcast = typeof BroadcastChannel === 'function' ? new BroadcastChannel('review-session') : null;
broadcast?.addEventListener('message', () => { disable(); show('login'); notice('此设备已退出。'); });
const resultText = {'withdrawn':'这题已撤回，未计成绩。','changed':'这题已修正，旧答案未计成绩。',
  'invalid-ticket':'题目已失效，未计成绩。','already-scored':'这题已在另一处记过成绩。'};
function stop() {
  audio.pause(); audio.removeAttribute('src'); audio.load();
  if (objectUrl) URL.revokeObjectURL(objectUrl);
  objectUrl = null;
}
function disable() {
  stop(); group = question = null; queue = []; makeup = []; sessionGeneration++; cacheRun++;
  $('nav').hidden = true; clearAnswer();
}
function clearAnswer() {
  for (const id of ['hint-value','expression','meaning','english','chinese','source']) $(id).textContent = '';
  for (const id of ['hint-value','answer','rating','makeup']) $(id).hidden = true;
  $('word-list').replaceChildren();
}
function todayFor(zone) {
  const p = Object.fromEntries(new Intl.DateTimeFormat('en',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts().map(p=>[p.type,p.value]));
  return `${p.year}-${p.month}-${p.day}`;
}
const readyFor = (a,b) => a && a.day === b.day && a.account === b.account && a.items.length === b.items.length
  && a.items.every((q,i) => q.id === b.items[i].id && q.sha256 === b.items[i].sha256);
async function flush(account) {
  const {pending,account:owner} = await local();
  if (owner !== account) return;
  for (const item of pending) {
    const receipt = await api('/api/score',item);
    if (resultText[receipt.result]) notice(resultText[receipt.result]);
    await local(s => s.account === account ? {...s,pending:s.pending.filter(e => e.event !== item.event)} : s);
  }
}
async function unauthorized() {
  disable();
  await local(s => s.pending.length ? {account:s.account,pending:s.pending} : null);
  broadcast?.postMessage('expired');
  show('login'); notice('请登录。未提交成绩会保留。');
}
async function load() {
  const generation = ++sessionGeneration;
  cacheRun++; stop(); question = null; clearAnswer();
  let state = await local();
  let data;
  try {
    if (state.logout) {
      await api('/api/logout',{});
      await local(s => s.logout ? null : s);
      state = await local();
      disable(); show('login'); notice('已退出，私人缓存已清除。'); return;
    }
    data = await api('/api/today');
    if (generation !== sessionGeneration) return;
    if (state.account && state.account !== data.account && state.pending.length) {
      disable(); show('login'); notice('这台设备还有另一账号的待交成绩，不能覆盖。'); return;
    }
    await flush(data.account);
    if (state.account === data.account && state.pending.length) data = await api('/api/today');
    if (generation !== sessionGeneration) return;
    const current = await local(s => s.logout || s.account === data.account ? s : {account:data.account,pending:[]});
    if (current.logout) { disable(); show('login'); return; }
    offline = false; connected = true;
  } catch (error) {
    if (generation !== sessionGeneration) return;
    if (error.status === 401) { await unauthorized(); return; }
    if (error instanceof HttpError) { disable(); show('login'); notice('服务暂不可用。'); return; }
    const cached = state.group;
    if (!state.consent || !cached || cached.expires <= Date.now() || todayFor(cached.zone) !== cached.day) {
      disable(); show('login'); notice('无法联网；没有有效的今日离线题。'); return;
    }
    const done = new Set(state.pending.map(e => e.ticket));
    const items = cached.items.filter(q => !done.has(q.id));
    data = {...cached,items,answered:cached.answered+cached.items.length-items.length};
    offline = true; connected = false;
  }
  if (generation !== sessionGeneration) return;
  group = data; $('nav').hidden = false; $('library').disabled = offline; show('today');
  $('date').textContent = group.day;
  $('summary').textContent = `已答 ${group.answered} 张 · 剩余 ${group.items.length} 张`;
  $('start').disabled = !group.items.length;
  $('consent').hidden = offline || !!state.consent;
  $('offline').textContent = offline ? '本组可离线 · 离线中' : '';
  if (!offline) void prepare();
}
async function prepare() {
  const current = group, seq = ++cacheRun;
  const state = await local();
  if (seq !== cacheRun || !state.consent || !current || state.account !== current.account) return;
  if (readyFor(state.group,current) && state.group.expires === current.expires) { $('offline').textContent = current.items.length ? '本组可离线' : ''; return; }
  $('offline').textContent = current.items.length ? '正在准备离线原声…' : '';
  try {
    if (!(await sw)) throw new Error('no service worker');
    await navigator.serviceWorker.ready;
    const items = [];
    for (const q of current.items) {
      if (seq !== cacheRun) return;
      let blob = state.group?.items.find(old => old.id === q.id && old.sha256 === q.sha256)?.blob;
      if (!blob) {
        const res = await fetch(q.audio,{cache:'no-store',signal:AbortSignal.timeout(12000)});
        if (!res.ok) throw new HttpError(res.status);
        blob = await res.blob();
        const sha = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await blob.arrayBuffer())),b=>b.toString(16).padStart(2,'0')).join('');
        if (sha !== q.sha256) throw new Error('audio digest');
      }
      items.push({...q,blob});
    }
    await local(s => seq === cacheRun && s.consent && s.account === current.account ? {...s,group:{...current,items}} : s);
    if (seq === cacheRun) $('offline').textContent = items.length ? '本组可离线' : '';
  } catch {
    if (seq === cacheRun) $('offline').textContent = '本组未缓存完整，可在线复习';
  }
}
function play() {
  if (!question) return;
  if (audio.error || !audio.getAttribute('src')) audio.src = objectUrl || question.audio;
  if (audio.readyState) audio.currentTime = 0;
  $('play-state').textContent = '正在播放…';
  const item = question;
  audio.play().catch(() => { if (item === question) { $('play-state').textContent = '无法播放，请重试或跳过。'; } });
}
audio.addEventListener('playing', () => { if (question) $('play-state').textContent = '播放中'; });
audio.addEventListener('ended', () => {
  if (!question) return;
  played = true; $('play-state').textContent = '播放完成'; $('hint').disabled = $('reveal').disabled = false;
});
audio.addEventListener('error', () => {
  if (!question || !audio.getAttribute('src')) return;
  played = false; $('play-state').textContent = '无法播放，请重试或跳过。';
  $('hint').disabled = $('reveal').disabled = true; $('rating').hidden = true;
});
function next() {
  question = null; stop(); clearAnswer();
  again = queue.length === 0;
  question = (again ? makeup : queue).shift();
  if (!question) { show('today'); setTimeout(() => { void run(load); },0); return; }
  played = hinted = false;
  $('hint').disabled = $('reveal').disabled = true;
  $('position').textContent = again ? '补听 · 不计成绩' : `剩余 ${queue.length + 1} 张`;
  $('quiz-title').textContent = '听原声';
  if (question.blob) objectUrl = URL.createObjectURL(question.blob);
  show('quiz'); play();
}
function reveal() {
  if (!question || !played) return;
  for (const id of ['expression','meaning','english','chinese','source']) $(id).textContent = question[id];
  $('hint-value').hidden = true; $('answer').hidden = false; $('hint').disabled = true;
  $('rating').hidden = again; $('makeup').hidden = !again;
}
async function grade(outcome) {
  if (!question || !played || again || $('answer').hidden) return;
  const current = question, account = group.account;
  if (todayFor(group.zone) !== group.day || Date.now() >= group.expires) { notice('学习日或登录已变更，请刷新。'); await load(); return; }
  if (outcome === 'heard' && hinted) outcome = 'hint';
  const entry = {event:crypto.randomUUID(),ticket:current.id,outcome};
  await local(s => {
    if (s.account !== account || s.pending.some(e => e.ticket === current.id)) return s;
    return {...s,pending:[...s.pending,entry]};
  });
  if (current !== question) return;
  if (outcome !== 'heard') makeup.push(current);
  if (connected) {
    try { await flush(account); }
    catch (e) { if (e.status === 401) { await unauthorized(); return; } notice('成绩已保存在此设备，联网后补交。'); }
  }
  if (current === question) next();
}
async function run(fn) {
  if (busy) return;
  busy = true;
  try { await fn(); }
  catch (e) { notice(e.status === 401 ? '口令错误或会话失效。' : e.status === 429 ? '请求过多，请稍后再试。' : '未完成，请重试。'); }
  finally { busy = false; }
}
$('login').addEventListener('submit', e => { e.preventDefault(); void run(async () => {
  const pass = $('password').value; $('password').value = ''; notice('');
  if ((await local()).logout) { await api('/api/logout',{}); await local(s=>s.logout?null:s); }
  await api('/api/login',{password:pass}); await load();
}); });
$('start').onclick = () => { if (group && !busy) { queue = [...group.items]; makeup = []; next(); } };
$('home').onclick = $('refresh').onclick = () => run(load);
$('consent').onclick = () => run(async () => {
  await local(s => ({...s,consent:true})); $('consent').hidden = true;
  void navigator.storage?.persist?.().catch(() => {}); void prepare();
});
$('library').onclick = () => run(async () => {
  let rows;
  try { rows = await api('/api/library'); } catch (e) { if (e.status === 401) { await unauthorized(); return; } throw e; }
  stop(); question = null;
  $('word-list').replaceChildren(...rows.map(row => {
    const el = document.createElement('li'); el.textContent = `${row.expression}${row.meaning ? ' · '+row.meaning : ''}`;
    const sub = document.createElement('small'); sub.textContent = row.textOnly ? '外部导入 · 文字' : `有效查词 ${row.count} 次${row.due ? ' · '+row.due : ''}`;
    el.append(sub);
    if (row.textOnly && (row.english || row.chinese)) {
      const ex = document.createElement('small'); ex.textContent = [row.english,row.chinese].filter(Boolean).join(' · '); el.append(ex);
    }
    return el;
  })); show('words');
});
$('logout').onclick = () => run(async () => {
  const account = group?.account;
  try { if (account) await flush(account); } catch { /* Confirm before discarding. */ }
  if ((await local()).pending.length && !confirm('有成绩尚未提交。确定放弃这些成绩并退出？取消可稍后联网补交。')) return;
  disable(); await local(() => ({logout:true,pending:[]})); broadcast?.postMessage('logout'); show('login');
  try { await api('/api/logout',{}); await local(s=>s.logout?null:s); notice('已退出，私人缓存已清除。'); }
  catch { notice('已清除本机私人缓存；恢复联网后完成会话注销。'); }
});
$('replay').onclick = play;
$('skip').onclick = $('next').onclick = () => { if (!busy) next(); };
$('hint').onclick = () => {
  if (!question || !played || !$('answer').hidden) return;
  hinted = true; $('hint-value').textContent = question.expression; $('hint-value').hidden = false;
};
$('reveal').onclick = reveal;
$('heard').onclick = () => run(() => grade('heard'));
$('missed').onclick = () => run(() => grade('missed'));
window.addEventListener('online', () => { void run(load); });
window.addEventListener('pageshow', e => { if (e.persisted) void run(load); });
show('login'); void run(load);
