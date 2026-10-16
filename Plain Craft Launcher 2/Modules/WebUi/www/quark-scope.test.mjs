// Scoped error presentation only. No live network, authorization or downloads.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE||'playwright');
const browser=await chromium.launch({channel:'msedge',headless:true});
try {
 const page=await browser.newPage(); const errors=[]; page.on('pageerror',e=>errors.push(e.message));
 await page.route(/^https?:\/\//,r=>r.abort());
 await page.addInitScript(()=>{const listeners=[];window.__receive=m=>listeners.forEach(f=>f({data:m}));window.chrome={webview:{postMessage:()=>{},addEventListener:(_,f)=>listeners.push(f)}}});
 await page.goto(new URL('./index.html',import.meta.url).href);
 const send=m=>page.evaluate(m=>window.__receive(m),m);
 const message='已转存，但没有找到目标文件，已停止下载。';
 const snapshot=task=>send({type:'browse',launcher:{presets:[],selected:'starrail-harpy-express',task,config:{catalogSource:'https://catalog.example/index.json',catalogUrls:['https://catalog.example/index.json']}}});
 await snapshot({scope:'browseQuark',busy:false,error:message,phase:'操作失败'});
 assert.doesNotMatch(await page.locator('#sources-summary-message').textContent(),/已转存|MCL：/);
 assert.equal(await page.locator('#task-error').textContent(),'');
 assert.doesNotMatch(await page.locator('#phase').textContent(),/操作失败/);
 await page.evaluate(()=>{Object.assign(quark,{wikiId:1426,shareId:'demo',requestId:'list-demo',downloadRequestId:'download-demo',pending:true,error:'',note:''});document.getElementById('quark-download').showModal()});
 await send({type:'mcmodError',operation:'quarkDownload',id:1426,requestId:'download-demo',message});
 assert.equal(await page.locator('#quark-download-message').textContent(),message);
 assert.equal(await page.locator('#quark-download-status').getAttribute('data-state'),'error');
 await send({type:'mcmodError',operation:'quarkDownload',id:999,requestId:'stale-download',message:'旧操作错误'});
 assert.equal(await page.locator('#quark-download-message').textContent(),message);
 await page.locator('#close-quark-download').click();
 await page.locator('#type-world').click();
 assert.doesNotMatch(await page.locator('#sources-summary-message').textContent(),/已转存/);
 assert.equal(await page.locator('#task-error').textContent(),'');
 await page.locator('#type-modpack').click();
 await snapshot({scope:'catalog',busy:false,error:'目录源暂不可用',phase:'操作失败'});
 assert.match(await page.locator('#sources-summary-message').textContent(),/MCL：目录源暂不可用/);
 await snapshot({scope:'library',busy:false,error:'整合包导入失败',phase:'操作失败'});
 assert.doesNotMatch(await page.locator('#sources-summary-message').textContent(),/整合包导入失败/);
 assert.equal(await page.locator('#task-error').textContent(),'整合包导入失败');
 await snapshot({busy:false,error:'旧协议任务错误'});
 assert.equal(await page.locator('#task-error').textContent(),'旧协议任务错误');
 assert.deepEqual(errors,[]);
 console.log('PASS Quark errors stay in their download panel; catalog/library/legacy task scopes remain independent');
} finally {await browser.close()}
