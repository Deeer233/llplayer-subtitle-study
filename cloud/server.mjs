import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { mkdtemp,writeFile,unlink,rmdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { digest, dayOf, Fault } from './store.mjs';
import { decodeCard, ID, TOKEN, object, valid } from './protocol.mjs';

async function isShortSpeech(bytes,ffmpeg) {
  // A seekable source lets the MP3 decoder account for encoder padding at the 30-second boundary.
  const folder = await mkdtemp(join(tmpdir(),'review-audio-'));
  const path = join(folder,'clip.mp3');
  try {
    await writeFile(path,bytes,{flag:'wx',mode:0o600});
    return await decodeAudio(path,ffmpeg);
  } finally {
    try { await unlink(path); } catch (e) { if (e.code !== 'ENOENT') throw e; }
    await rmdir(folder);
  }
}
function decodeAudio(path,ffmpeg) {
  return new Promise((ok,fail) => {
    const child = spawn(ffmpeg,['-nostdin','-hide_banner','-loglevel','error','-xerror','-f','mp3','-i',path,'-map','0:a:0',
      '-ac','1','-ar','8000','-f','s16le','pipe:1'],{stdio:['ignore','pipe','ignore'],windowsHide:true});
    let size = 0, loud = false;
    const timer = setTimeout(() => child.kill(),10000);
    child.stdout.on('data',b => {
      size += b.length;
      if (size > 30 * 16000) { child.kill(); return; }
      for (let i=0; !loud && i+1<b.length; i+=2) loud = Math.abs(b.readInt16LE(i)) >= 50;
    });
    child.on('error',() => { clearTimeout(timer); fail(new Fault(503,'media-validator-unavailable')); });
    child.on('close',code => { clearTimeout(timer); ok(code === 0 && loud && size <= 30 * 16000); });
  });
}

async function body(req,limit) {
  if (req.headers['content-type']?.split(';')[0] !== 'application/json') throw new Fault(415,'json-required');
  if (Number(req.headers['content-length']) > limit) throw new Fault(413,'too-large');
  const chunks = []; let n = 0;
  for await (const chunk of req.iterator({destroyOnReturn:false})) {
    n += chunk.length;
    if (n > limit) { req.resume(); throw new Fault(413,'too-large'); }
    chunks.push(chunk);
  }
  try { const value = JSON.parse(Buffer.concat(chunks).toString('utf8')); valid(object(value)); return value; }
  catch { throw new Fault(400,'invalid-json'); }
}
const assets = new Map(Object.entries({'/':'index.html','/app.js':'app.js','/style.css':'style.css','/sw.js':'sw.js'})
  .map(([path,name]) => [path,{bytes:readFileSync(new URL(`./web/${name}`,import.meta.url)),
    type:name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'}]));
const SESSION_MS = 30 * 86400000;
export function makeServer(store,{origin,now=Date.now,sessionMs=SESSION_MS,ffmpeg='ffmpeg'}={}) {
  const url = new URL(origin);
  const local = ['localhost','127.0.0.1','[::1]'].includes(url.hostname);
  if (url.origin !== origin || !(url.protocol === 'https:' || (url.protocol === 'http:' && local))) throw new Error('Use a single HTTPS origin, or HTTP loopback for development.');
  const secure = url.protocol === 'https:';
  const name = secure ? '__Host-review' : 'review_local';
  const cookie = (value,age) => `${name}=${value}; Path=/; HttpOnly; SameSite=Strict; Max-Age=${age}${secure?'; Secure':''}`;
  const loginWindow = {failed:0,until:0};
  let uploads = 0, pairs = 0, pairWindow = 0;
  const server = createServer({requestTimeout:30000},async (req,res) => {
    res.setHeader('Cache-Control','private, no-store');
    res.setHeader('X-Content-Type-Options','nosniff');
    res.setHeader('Referrer-Policy','no-referrer');
    res.setHeader('Content-Security-Policy',"default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; media-src 'self' blob:; worker-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
    if (secure) res.setHeader('Strict-Transport-Security','max-age=31536000');
    const json = (data,status=200) => { res.writeHead(status,{'Content-Type':'application/json; charset=utf-8'}); res.end(JSON.stringify(data)); };
    const path = req.url;
    try {
      const asset = assets.get(path);
      if (req.method === 'GET' && asset) { res.writeHead(200,{'Content-Type':`${asset.type}; charset=utf-8`}); res.end(asset.bytes); return; }
      const a = store.account();
      if (!a) throw new Fault(503,'account-not-configured');
      const time = now();
      if (req.method === 'POST' && path === '/api/pair') {
        if (time >= pairWindow) { pairs=0; pairWindow=time+60000; }
        if (++pairs > 20) throw new Fault(429,'try-later');
        return json({uploadToken:store.pair((await body(req,2048)).code,time)});
      }
      if (req.method === 'PUT' && path === '/api/cards') {
        const credential = req.headers.authorization?.match(/^Bearer ([\w-]{43})$/)?.[1];
        if (!store.auth('upload',credential,time)) throw new Fault(401,'pair-again');
        if (uploads >= 2) throw new Fault(503,'try-later');
        uploads++;
        try {
          const {card,media} = decodeCard(await body(req,6500000));
          const hash = digest(JSON.stringify(card));
          const existing = store.version(card,hash);
          if (existing) return json(existing);
          for (const m of media) valid(await isShortSpeech(m.bytes,ffmpeg),'bad-audio');
          if (!store.auth('upload',credential,now())) throw new Fault(401,'pair-again');
          return json(store.publish(card,media,hash));
        } finally { uploads--; }
      }
      if (req.method === 'POST' && req.headers.origin !== origin) throw new Fault(403,'origin');
      if (req.method === 'POST' && path === '/api/login') {
        if (time >= loginWindow.until) { loginWindow.failed=0; loginWindow.until=time+60000; }
        if (loginWindow.failed >= 8) throw new Fault(429,'try-later');
        const {password} = await body(req,4096);
        if (loginWindow.failed >= 8) throw new Fault(429,'try-later');
        if (!store.checkPassword(a,password)) { loginWindow.failed++; throw new Fault(401,'login-failed'); }
        loginWindow.failed=0;
        const value = store.grant('session',time+sessionMs,'browser',time);
        res.setHeader('Set-Cookie',cookie(value,Math.floor(sessionMs/1000)));
        return json({ok:true});
      }
      const value = req.headers.cookie?.split(';').map(v=>v.trim()).find(v=>v.startsWith(`${name}=`))?.slice(name.length+1);
      const session = store.auth('session',value,time);
      if (req.method === 'POST' && path === '/api/logout') {
        if (session) store.revoke(session.hash);
        res.setHeader('Set-Cookie',cookie('',0));
        return json({ok:true});
      }
      if (!session) throw new Fault(401,'login-required');
      const day = dayOf(time,a.zone);
      if (req.method === 'GET' && path === '/api/today') return json({account:a.id,expires:session.until,zone:a.zone,...store.today(day)});
      if (req.method === 'GET' && path === '/api/library') return json(store.library());
      if (req.method === 'POST' && path === '/api/score') {
        const {event,ticket,outcome} = await body(req,2048);
        const current = now();
        if (!store.auth('session',value,current)) throw new Fault(401,'login-required');
        valid(typeof event === 'string' && ID.test(event) && typeof ticket === 'string' && TOKEN.test(ticket)
          && ['heard','hint','missed'].includes(outcome));
        return json(store.score(event.toLowerCase(),ticket,outcome,dayOf(current,a.zone)));
      }
      if (req.method === 'GET' && path?.startsWith('/api/audio/')) {
        const id = path.slice(11);
        const media = TOKEN.test(id) && store.audio(id);
        if (!media) throw new Fault(404,'audio-unavailable');
        const bytes = Buffer.from(media.bytes), raw = req.headers.range;
        const headers = {'Content-Type':'audio/mpeg','Content-Disposition':'inline','Accept-Ranges':'bytes'};
        if (!raw) { res.writeHead(200,{...headers,'Content-Length':bytes.length}); res.end(bytes); return; }
        const range = /^bytes=(\d*)-(\d*)$/.exec(raw);
        const start = range?.[1] ? Number(range[1]) : Math.max(0,bytes.length-Number(range?.[2]));
        const end = range?.[1] && range[2] ? Math.min(Number(range[2]),bytes.length-1) : bytes.length-1;
        if (!range || (!range[1]&&!range[2]) || !(start<=end)) { res.writeHead(416,{'Content-Range':`bytes */${bytes.length}`}); res.end(); return; }
        res.writeHead(206,{...headers,'Content-Length':end-start+1,'Content-Range':`bytes ${start}-${end}/${bytes.length}`});
        res.end(bytes.subarray(start,end+1)); return;
      }
      throw new Fault(404,'not-found');
    } catch (e) {
      if (!res.headersSent && !res.destroyed) json({error:e instanceof Fault ? e.message : 'internal-error'},e instanceof Fault ? e.status : 500);
      if (!(e instanceof Fault)) console.error('Internal request error:',e.code ?? e.name);
    }
  });
  server.headersTimeout=10000;
  return server;
}
