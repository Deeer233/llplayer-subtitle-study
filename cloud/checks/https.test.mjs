import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { request as httpRequest } from 'node:http';
import { createServer as createTlsServer, request as tlsRequest } from 'node:https';
import { join } from 'node:path';
import { openStore, token } from '../store.mjs';
import { makeServer } from '../server.mjs';
import { card, tmp, tone } from './fixtures.mjs';

test('HTTPS loopback relay: trusted TLS, secure sessions, protected ranges and upload size', {timeout:30000}, async t => {
  const base = tmp(), keyPath = join(base,'key.pem'), certPath = join(base,'cert.pem');
  const openssl = spawnSync(process.env.REVIEW_OPENSSL ?? 'openssl', [
    'req','-x509','-newkey','rsa:2048','-nodes','-days','1','-subj','/CN=127.0.0.1',
    '-addext','subjectAltName=IP:127.0.0.1','-keyout',keyPath,'-out',certPath],
    {encoding:'utf8',timeout:10000,windowsHide:true});
  assert.equal(openssl.status,0,openssl.error?.message ?? openssl.stderr);
  const cert = readFileSync(certPath);
  let upstreamPort;
  const relayLimit = 6_500_000;
  const edge = createTlsServer({key:readFileSync(keyPath),cert},async(req,res) => {
    const chunks=[];
    let size=0;
    try {
      for await (const chunk of req) {
        size+=chunk.length;
        if (size<=relayLimit) chunks.push(chunk);
      }
      if (size>relayLimit) { res.writeHead(413).end(); return; }
      const headers={...req.headers,'x-forwarded-proto':'https',connection:'close'};
      const upstream=httpRequest({host:'127.0.0.1',port:upstreamPort,path:req.url,method:req.method,headers,agent:false},reply=>{
        res.writeHead(reply.statusCode,reply.headers);
        reply.pipe(res);
        reply.on('error',error=>res.destroy(error));
      });
      upstream.on('error',()=>{ if (!res.headersSent) res.writeHead(502).end(); });
      upstream.end(Buffer.concat(chunks));
    } catch (error) {
      res.destroy(error);
    }
  });
  edge.listen(0,'127.0.0.1'); await once(edge,'listening');
  t.after(() => new Promise(resolve => edge.close(resolve)));
  const origin = `https://127.0.0.1:${edge.address().port}`;
  const store = openStore(join(base,'review.sqlite'));
  const password = token();
  store.init(password,'Asia/Shanghai');
  let now = Date.now();
  const backend = makeServer(store,{origin,now:()=>now,sessionMs:30000,ffmpeg:process.env.REVIEW_FFMPEG});
  backend.listen(0,'127.0.0.1'); await once(backend,'listening');
  upstreamPort = backend.address().port;
  t.after(async()=>{ await new Promise(resolve=>backend.close(resolve)); store.db.close(); });

  function call(path,{method='GET',headers={},body,trust=true}={}) {
    return new Promise((resolve,reject)=>{
      const req = tlsRequest(origin+path,{method,ca:trust?cert:undefined,agent:false,
        headers:{Origin:origin,...(body!==undefined ? {'Content-Type':'application/json','Content-Length':Buffer.byteLength(body)} : {}),...headers},
        signal:AbortSignal.timeout(10000)}, res=>{
        const chunks=[];
        res.on('data',chunk=>chunks.push(chunk));
        res.on('end',()=>resolve({status:res.statusCode,headers:res.headers,body:Buffer.concat(chunks)}));
        res.on('error',error=>{ error.message=`response ${method} ${path.split('/').slice(0,3).join('/')}: ${error.message}`; reject(error); });
      });
      req.on('error',error=>{ error.message=`${method} ${path.split('/').slice(0,3).join('/')}: ${error.message}`; reject(error); });
      req.end(body);
    });
  }
  await assert.rejects(call('/',{trust:false}),{code:'DEPTH_ZERO_SELF_SIGNED_CERT'});
  const home=await call('/');
  assert.equal(home.status,200);
  assert.match(home.headers['strict-transport-security'],/max-age=31536000/);
  assert.equal((await call('/sw.js')).status,200);
  assert.equal((await call('/api/library')).status,401);
  assert.equal((await call('/api/login',{method:'POST',headers:{Origin:'https://unrelated.invalid'},body:'{}'})).status,403);
  const session=await call('/api/login',{method:'POST',body:JSON.stringify({password})});
  assert.equal(session.status,200);
  assert.match(session.headers['set-cookie'][0],/^__Host-review=[\w-]+; Path=\/; HttpOnly; SameSite=Strict; Max-Age=30; Secure$/);
  const auth={Cookie:session.headers['set-cookie'][0].split(';')[0]};
  const code=store.grant('pair',now+600000,'https-check',now);
  const paired=await call('/api/pair',{method:'POST',body:JSON.stringify({code})});
  assert.equal(paired.status,200);
  const upload={Authorization:`Bearer ${JSON.parse(paired.body).uploadToken}`};
  const sound=tone(), baseBody=Buffer.from(JSON.stringify(card(sound)));
  const uploadSize=(size)=>call('/api/cards',{method:'PUT',headers:upload,
    body:Buffer.concat([baseBody,Buffer.alloc(size-baseBody.length,0x20)])});
  assert.equal((await uploadSize(6_500_000)).status,200);
  assert.equal((await uploadSize(6_500_001)).status,413);
  const today=await call('/api/today',{headers:auth});
  assert.equal(today.status,200);
  const audio=JSON.parse(today.body).items[0].audio;
  assert.equal((await call(audio,{headers:{Range:'bytes=0-63'}})).status,401);
  const range=await call(audio,{headers:{...auth,Range:'bytes=0-63'}});
  assert.equal(range.status,206);
  assert.equal(range.headers['content-range'],`bytes 0-63/${sound.length}`);
  assert.equal(range.headers['cache-control'],'private, no-store');
  assert.deepEqual(range.body,sound.subarray(0,64));
  const logout=await call('/api/logout',{method:'POST',headers:auth,body:'{}'});
  assert.equal(logout.status,200);
  assert.equal((await call(audio,{headers:auth})).status,401);
  const expiring=await call('/api/login',{method:'POST',body:JSON.stringify({password})});
  assert.equal(expiring.status,200);
  const fresh={Cookie:expiring.headers['set-cookie'][0].split(';')[0]};
  assert.equal((await call(audio,{headers:fresh})).status,200);
  now+=30001;
  assert.equal((await call(audio,{headers:fresh})).status,401);
});
