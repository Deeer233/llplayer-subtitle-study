import test from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { setTimeout as delay } from 'node:timers/promises';
import { boot,call,card,login,pair,tmp,tone } from './fixtures.mjs';

async function until(fn,label,ms=12000) {
  const end=Date.now()+ms;
  let last;
  while (Date.now()<end) {
    try { last=await fn(); if (last) return last; } catch (e) { last=e.message; }
    await delay(100);
  }
  throw new Error(`${label}: ${last}`);
}
test('Chromium mobile layout, listen-first, cold offline playback, reconnect and logout',
  {skip:!process.env.REVIEW_PLAYWRIGHT,timeout:60000},async t=>{
  let now=Date.now();
  const site=await boot(join(tmp(),'review.sqlite'),{now:()=>now});
  t.after(()=>site.close());
  const bearer=await pair(site,now);
  const data=card(tone());
  assert.equal((await call(site,'/api/cards',{method:'PUT',bearer,data})).status,200);
  const {chromium}=createRequire(import.meta.url)(process.env.REVIEW_PLAYWRIGHT);
  const browser=await chromium.launch({executablePath:process.env.REVIEW_CHROME,timeout:12000});
  t.after(()=>browser.close());
  const context=await browser.newContext({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
  let page=await context.newPage();
  page.setDefaultTimeout(10000);
  const read=expression=>page.evaluate(expression);
  const visible=id=>`document.getElementById('${id}').hidden === false`;
  const click=id=>page.locator('#'+id).click();
  const text=id=>`document.getElementById('${id}').textContent`;
  const network=offline=>context.setOffline(offline);
  await page.goto(site.origin+'/');
  await until(()=>read(visible('login')),'login screen');
  await page.locator('#password').fill(site.password);
  await page.getByRole('button',{name:'登录',exact:true}).click();
  await until(()=>read(visible('today')),'today screen');
  assert.equal(await read('document.documentElement.scrollWidth <= window.innerWidth'),true);
  if (process.env.REVIEW_CHECK_SCREENSHOTS) {
    await page.screenshot({path:join(process.env.REVIEW_CHECK_SCREENSHOTS,'review-mobile.png')});
    const desktop=await browser.newContext({viewport:{width:1280,height:800}});
    await desktop.addCookies(await context.cookies(site.origin));
    const desktopPage=await desktop.newPage();
    await desktopPage.goto(site.origin);
    await desktopPage.locator('#today').waitFor();
    assert.equal(await desktopPage.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),true);
    await desktopPage.screenshot({path:join(process.env.REVIEW_CHECK_SCREENSHOTS,'review-desktop.png')});
    await desktop.close();
  }
  await click('consent');
  await until(()=>read(`${text('offline')}==='本组可离线' && !!navigator.serviceWorker.controller`),'audio verified and cached');
  const date=await read(text('date'));
  await network(true);
  await page.close(); page=await context.newPage(); page.setDefaultTimeout(10000);
  await page.goto(site.origin+'/');
  await until(()=>read(`${visible('today')} && ${text('offline')}.includes('离线中')`),'cold offline launch');
  await click('start');
  await until(()=>read(`${text('play-state')}==='播放完成'`),'offline audio plays');
  assert.equal(await read(`${visible('quiz')} && !document.querySelector('main').innerText.includes('bail on')`),true);
  await click('hint');
  assert.equal(await read(text('hint-value')),'bail on');
  assert.equal(await read(visible('answer')),false);
  await click('reveal');
  assert.equal(await read(text('meaning')),'放鸽子');
  await click('heard');
  await until(()=>read(`${text('position')}.includes('补听')`),'local score leads to makeup');
  assert.equal(site.store.db.prepare('SELECT count(*) n FROM scores').get().n,0);
  now+=86400000;
  await network(false);
  await until(()=>site.store.db.prepare('SELECT day,outcome FROM scores').get()?.outcome==='hint','offline score sent');
  const stored=site.store.db.prepare('SELECT day,outcome FROM scores').get();
  assert.equal(stored.day,date);
  await until(()=>read(`${visible('today')} && ${text('date')}!==${JSON.stringify(date)}`),'today refreshed');
  await click('logout');
  await until(()=>read(`${visible('login')} && ${text('notice')}.includes('已清除')`),'logout');
  await network(true);
  await page.reload();
  await until(()=>read(`${visible('login')} && ${text('notice')}.includes('没有有效')`),'logout removes private offline content');
  assert.equal(await read(visible('today')),false);
  const other=await login(site);
  const result=await call(site,'/api/library',{cookie:other});
  assert.equal(result.body[0].due,(await call(site,'/api/today',{cookie:other})).body.day);
  assert.equal(site.store.db.prepare('SELECT count(*) n FROM scores').get().n,1);
});
