import { DurableObject } from 'cloudflare:workers';
import { Buffer } from 'node:buffer';
import { timingSafeEqual } from 'node:crypto';
import { createStore, dayOf, digest, Fault, schema, token } from '../../store-core.mjs';
import { decodeCard, ID, TOKEN, object, valid } from '../../protocol.mjs';
import { isShortAudio } from './audio.mjs';

const SESSION_MS = 30 * 86400000;
const ASSETS = new Map([['/','/index.html'],['/app.js','/app.js'],['/style.css','/style.css'],['/sw.js','/sw.js']]);
const PROTECTION = {
  'Cache-Control':'private, no-store',
  'X-Content-Type-Options':'nosniff',
  'Referrer-Policy':'no-referrer',
  'Content-Security-Policy':"default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; media-src 'self' blob:; worker-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
};
const json = (value,status=200,headers={}) => Response.json(value,{status,headers});

async function body(req,limit) {
  if (req.headers.get('Content-Type')?.split(';')[0] !== 'application/json') throw new Fault(415,'json-required');
  if (Number(req.headers.get('Content-Length')) > limit) throw new Fault(413,'too-large');
  if (!req.body) throw new Fault(400,'invalid-json');
  const reader=req.body.getReader(), chunks=[];
  let length=0;
  try {
    while (true) {
      const {value,done}=await reader.read();
      if (done) break;
      length+=value.length;
      if (length>limit) {
        void reader.cancel().catch(()=>{});
        throw new Fault(413,'too-large');
      }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  try {
    const value=JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (!object(value)) throw new Error();
    return value;
  } catch { throw new Fault(400,'invalid-json'); }
}

function serveAudio(media,range) {
  if (!media) throw new Fault(404,'audio-unavailable');
  const bytes=new Uint8Array(media.bytes);
  const headers={'Content-Type':'audio/mpeg','Content-Disposition':'inline','Accept-Ranges':'bytes'};
  if (!range) return new Response(bytes,{headers:{...headers,'Content-Length':String(bytes.length)}});
  const match=/^bytes=(\d*)-(\d*)$/.exec(range);
  const start=match?.[1] ? Number(match[1]) : Math.max(0,bytes.length-Number(match?.[2]));
  const end=match?.[1] && match[2] ? Math.min(Number(match[2]),bytes.length-1) : bytes.length-1;
  if (!match || (!match[1] && !match[2]) || !(start<=end)) {
    return new Response(null,{status:416,headers:{'Content-Range':'bytes */'+bytes.length}});
  }
  const output=bytes.subarray(start,end+1);
  return new Response(output,{status:206,headers:{...headers,'Content-Length':String(output.length),
    'Content-Range':'bytes '+start+'-'+end+'/'+bytes.length}});
}

function matches(value,expected) {
  return typeof value==='string' && TOKEN.test(value) && timingSafeEqual(Buffer.from(value),Buffer.from(expected));
}
function passwordMatches(value,expected) {
  return typeof value==='string' && value.length<=256 && matches(value,expected);
}

export class PrivateLibrary extends DurableObject {
  constructor(ctx,env) {
    super(ctx,env);
    const storage=ctx.storage;
    this.sql=storage.sql;
    this.sql.exec(schema);
    this.sql.exec('CREATE TABLE IF NOT EXISTS rate (kind TEXT PRIMARY KEY, until INTEGER NOT NULL, count INTEGER NOT NULL)');
    this.txn=fn=>storage.transactionSync(fn);
    this.store=createStore({
      get:(...args)=>this.sql.exec(...args).toArray()[0],
      all:(...args)=>this.sql.exec(...args).toArray(),
      run:(query,...args)=>this.sql.exec(query,...args.map(arg=>Buffer.isBuffer(arg)
        ? arg.buffer.slice(arg.byteOffset,arg.byteOffset+arg.byteLength) : arg)),
      txn:this.txn,
    });
    this.uploads=0;
  }

  throttle(kind,time,limit) {
    const row=this.sql.exec('SELECT until,count FROM rate WHERE kind=?',kind).toArray()[0];
    if (row?.until>time && row.count>=limit) throw new Fault(429,'try-later');
    if (!row || row.until<=time) this.sql.exec('INSERT OR REPLACE INTO rate VALUES (?,?,1)',kind,time+60000);
    else this.sql.exec('UPDATE rate SET count=count+1 WHERE kind=?',kind);
  }

  async api(req,origin) {
    try { return await this.route(req,origin); }
    catch (error) {
      if (error instanceof Fault) return json({error:error.message},error.status);
      console.error('Private review request failed:',error.name);
      return json({error:'internal-error'},500);
    }
  }

  async route(req,origin) {
    const {pathname:path, search}=new URL(req.url);
    const {method}=req;
    if (search) throw new Fault(404,'not-found');
    const password=this.env.REVIEW_PASSWORD, admin=this.env.REVIEW_ADMIN_TOKEN;
    if (!TOKEN.test(password??'') || !TOKEN.test(admin??'') || password===admin) throw new Fault(503,'account-not-configured');
    const store=this.store;
    const now=Date.now();
    const bearer=req.headers.get('Authorization')?.match(/^Bearer ([\w-]{43})$/)?.[1];
    if (path.startsWith('/api/admin/')) {
      if (!matches(bearer,admin)) throw new Fault(401,'admin-required');
      if (method==='POST' && path==='/api/admin/pair') {
        const {label}=await body(req,2048);
        valid(typeof label==='string' && /^[\p{L}\p{N}_ -]{1,40}$/u.test(label));
        const time=Date.now();
        this.ensureAccount();
        return json({code:store.grant('pair',time+600000,label,time)});
      }
      if (method==='POST' && path==='/api/admin/revoke') {
        const {label}=await body(req,2048);
        valid(typeof label==='string' && /^[\p{L}\p{N}_ -]{1,40}$/u.test(label));
        store.revokeDevice(label);
        return json({ok:true});
      }
      throw new Fault(404,'not-found');
    }
    if (method==='POST' && path==='/api/pair') {
      this.throttle('pair',now,20);
      return json({uploadToken:store.pair((await body(req,2048)).code,Date.now())});
    }
    if (method==='PUT' && path==='/api/cards') {
      if (!store.auth('upload',bearer,now)) throw new Fault(401,'pair-again');
      if (this.uploads>=2) throw new Fault(503,'try-later');
      this.uploads++;
      try {
        const {card,media}=decodeCard(await body(req,6500000));
        if (!store.auth('upload',bearer,Date.now())) throw new Fault(401,'pair-again');
        const hash=digest(JSON.stringify(card));
        const existing=store.version(card,hash);
        if (existing) return json(existing);
        for (const {bytes} of media) valid(await isShortAudio(bytes),'bad-audio');
        if (!store.auth('upload',bearer,Date.now())) throw new Fault(401,'pair-again');
        return json(store.publish(card,media,hash));
      } finally { this.uploads--; }
    }
    if (method==='POST' && req.headers.get('Origin')!==origin) throw new Fault(403,'origin');
    const secure=origin.startsWith('https:');
    const cookieName=secure?'__Host-review':'review_local';
    const cookie=(value,age)=>cookieName+'='+value+'; Path=/; HttpOnly; SameSite=Strict; Max-Age='+age+(secure?'; Secure':'');
    if (method==='POST' && path==='/api/login') {
      this.throttle('login',now,8);
      if (!passwordMatches((await body(req,4096)).password,password)) throw new Fault(401,'login-failed');
      this.sql.exec('DELETE FROM rate WHERE kind=?','login');
      this.ensureAccount();
      const time=Date.now();
      const value=store.grant('session',time+SESSION_MS,'browser',time);
      return json({ok:true},200,{'Set-Cookie':cookie(value,SESSION_MS/1000)});
    }
    const value=req.headers.get('Cookie')?.split(';').map(x=>x.trim()).find(x=>x.startsWith(cookieName+'='))?.slice(cookieName.length+1);
    const session=store.auth('session',value,now);
    if (method==='POST' && path==='/api/logout') {
      if (session) store.revoke(session.hash);
      return json({ok:true},200,{'Set-Cookie':cookie('',0)});
    }
    if (!session) throw new Fault(401,'login-required');
    if (method==='GET' && path==='/api/library') return json(store.library());
    if (method==='GET' && path==='/api/today') {
      const a=store.account();
      return json({account:a.id,expires:session.until,zone:a.zone,...store.today(dayOf(now,a.zone))});
    }
    if (method==='POST' && path==='/api/score') {
      const {event,ticket,outcome}=await body(req,2048);
      const time=Date.now();
      if (!store.auth('session',value,time)) throw new Fault(401,'login-required');
      valid(typeof event==='string' && ID.test(event) && typeof ticket==='string' && TOKEN.test(ticket)
        && ['heard','hint','missed'].includes(outcome));
      return json(store.score(event.toLowerCase(),ticket,outcome,dayOf(time,store.account().zone)));
    }
    if (method==='GET' && path.startsWith('/api/audio/')) {
      const id=path.slice(11);
      return serveAudio(TOKEN.test(id) && store.audio(id),req.headers.get('Range'));
    }
    throw new Fault(404,'not-found');
  }

  ensureAccount() {
    if (!this.store.account()) this.store.initAccount({id:token(),salt:'',password:'',zone:'Asia/Shanghai'});
  }
}

export default {
  async fetch(req,env) {
    const url=new URL(req.url);
    const secure=url.protocol==='https:';
    if (!secure && (url.protocol!=='http:' || !['127.0.0.1','localhost','[::1]'].includes(url.hostname))) {
      return json({error:'https-required'},400,PROTECTION);
    }
    try {
      let result;
      const asset=ASSETS.get(url.pathname);
      if (asset && (req.method==='GET' || req.method==='HEAD')) {
        result=await env.ASSETS.fetch(new Request(new URL(asset,url),{method:req.method}));
      } else if (url.pathname.startsWith('/api/')) {
        result=await env.LIBRARY.getByName('single-account').api(req,url.origin);
      } else result=json({error:'not-found'},404);
      const headers=new Headers(result.headers);
      for (const [key,value] of Object.entries(PROTECTION)) headers.set(key,value);
      if (secure) headers.set('Strict-Transport-Security','max-age=31536000');
      return new Response(result.body,{status:result.status,headers});
    } catch (error) {
      console.error('Private review worker failed:',error.name);
      return json({error:'internal-error'},500,PROTECTION);
    }
  },
};
