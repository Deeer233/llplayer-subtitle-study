import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { join } from 'node:path';
import { writeFileSync } from 'node:fs';
import { boot,call,ffmpeg,login,tmp } from './fixtures.mjs';

const dll=process.env.REVIEW_DESKTOP_DLL;
const dotnet=process.env.REVIEW_DOTNET ?? 'dotnet';
const execute=input=>new Promise((ok,fail)=>{
  const proc=spawn(dotnet,[dll],{stdio:['pipe','pipe','pipe'],windowsHide:true});
  let out='',err='';
  proc.stdout.setEncoding('utf8').on('data',s=>out+=s);
  proc.stderr.setEncoding('utf8').on('data',s=>err+=s);
  proc.stdin.on('error',fail); proc.on('error',fail);
  proc.on('close',code=>code===0?ok(out.trim()):fail(Error(err || `Exit ${code}`)));
  proc.stdin.end(JSON.stringify(input));
});
test('Windows dialogue/clip extraction and outbox across real processes against HTTP server',{skip:!dll,timeout:45000},async t=>{
  const base=tmp(), video=join(base,'fixture.mkv'), words=join(base,'words.csv');
  ffmpeg('-f','lavfi','-i','color=s=64x64:d=2.5','-f','lavfi','-i','anullsrc=r=44100:d=2.5',
    '-f','lavfi','-i','sine=frequency=660:duration=2.5','-map','0:v','-map','1:a','-map','2:a',
    '-c:v','mpeg4','-c:a','aac','-metadata:s:a:0','language=zho','-metadata:s:a:1','language=eng','-n',video);
  writeFileSync(words,'word,meaning,example\nexternal,外部,An external list.\nspare,,\n',{flag:'wx'});
  const site=await boot(join(base,'review.sqlite'));
  t.after(()=>site.close());
  const cookie=await login(site);
  const run=async (phase,extra={})=>execute({root:join(base,'desktop'),origin:site.origin,video,words,phase,...extra});
  const newCode=()=>{const now=Date.now();return site.store.grant('pair',now+60000,'desktop-check',now);};
  const items=async()=>(await call(site,'/api/today',{cookie})).body.items;
  assert.equal(await run('capture',{code:newCode()}),'validated-capture-and-offline');
  assert.equal((await items()).length,0);
  assert.equal(await run('lost-reply'),'validated-restart-and-lost-reply');
  const first=(await items())[0];
  assert.ok(first);
  assert.equal(await run('resend'),'validated-resend-correction-import');
  const newer=await items();
  assert.equal(newer.length,1); assert.equal(newer[0].meaning,'爽约');
  assert.equal((await call(site,first.audio,{cookie})).status,404);
  const lib=(await call(site,'/api/library',{cookie})).body;
  assert.equal(lib.filter(x=>x.textOnly).length,2);
  assert.equal(lib.find(x=>!x.textOnly).count,2);
  site.store.revokeDevice('desktop-check');
  assert.equal(await run('revoke',{code:newCode()}),'validated-repair-and-withdrawal');
  assert.equal((await items()).length,0);
});
