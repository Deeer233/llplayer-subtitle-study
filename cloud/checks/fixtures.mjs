import { spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import assert from 'node:assert/strict';
import { digest,openStore,token } from '../store.mjs';
import { makeServer } from '../server.mjs';
import { createServer } from 'node:net';

export const tmp = () => mkdtempSync(join(tmpdir(),'private-review-check-'));
export const fmt = day => Date.parse(day+'T00:00:00+08:00');
export function ffmpeg(...args) {
  const p = spawnSync(process.env.REVIEW_FFMPEG ?? 'ffmpeg',['-hide_banner','-loglevel','error','-nostdin',...args],{maxBuffer:8*1024*1024});
  assert.equal(p.status,0,p.error?.message ?? p.stderr.toString());
  return p.stdout;
}
export function tone(filter='sine=frequency=440:duration=0.45') {
  return ffmpeg('-f','lavfi','-i',filter,'-c:a','libmp3lame','-write_xing','0','-f','mp3','pipe:1');
}
export function entry(mp3,expression='bail on',english='Please don’t bail on me.') {
  const exampleId=randomUUID();
  return {meta:{exampleId,english,target:expression,source:'自制样例',verified:true,sha256:digest(mp3)},blob:{id:exampleId,mp3:mp3.toString('base64')}};
}
export function card(mp3,expression='bail on',english,extra=[]) {
  const examples=[entry(mp3,expression,english),...extra];
  return {card:{libraryId:randomUUID(),cardId:randomUUID(),revision:1,status:'active',expression,meaning:'放鸽子',count:1,reason:'manual',examples:examples.map(e=>e.meta)},media:examples.map(e=>e.blob)};
}
export async function boot(path,opts={},port) {
  if (!port) {
    const p=createServer(); p.listen(0,'127.0.0.1'); await once(p,'listening');
    port=p.address().port; await new Promise(resolve=>p.close(resolve));
  }
  const origin=`http://127.0.0.1:${port}`;
  const store=openStore(path);
  const password=opts.password ?? token();
  if (!store.account()) store.init(password,'Asia/Shanghai');
  const server=makeServer(store,{...opts,origin,ffmpeg:process.env.REVIEW_FFMPEG});
  server.listen(port,'127.0.0.1'); await once(server,'listening');
  return {origin,port,store,password,close:async()=>{ await new Promise(resolve=>server.close(resolve)); store.db.close(); }};
}
export async function call(site,path,{data,body,method,cookie,bearer,origin=site.origin,headers={}}={}) {
  const res=await fetch(site.origin+path,{method:method ?? (data===undefined ? 'GET' : 'POST'),headers:{Connection:'close',...headers,
    ...(origin?{Origin:origin}:{}),...(data!==undefined?{'Content-Type':'application/json'}:{}),
    ...(cookie?{Cookie:cookie}:{}),...(bearer?{Authorization:`Bearer ${bearer}`}:{})},body:body ?? (data===undefined?undefined:JSON.stringify(data))});
  return {status:res.status,headers:res.headers,body:res.headers.get('content-type')?.includes('json')?await res.json():Buffer.from(await res.arrayBuffer())};
}
export async function login(site) {
  const r=await call(site,'/api/login',{data:{password:site.password}});
  assert.equal(r.status,200); return r.headers.get('set-cookie').split(';')[0];
}
export async function pair(site,now=Date.now()) {
  const code=site.store.grant('pair',now+600000,'test',now);
  const first=await call(site,'/api/pair',{data:{code}});
  assert.equal(first.status,200);
  assert.equal((await call(site,'/api/pair',{data:{code}})).status,401);
  return first.body.uploadToken;
}
