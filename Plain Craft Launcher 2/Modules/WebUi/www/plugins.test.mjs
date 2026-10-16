// Plugin browse uses the existing host/native download contract. DEMO fixtures, no external traffic.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE||'playwright');
const browser=await chromium.launch({channel:'msedge',headless:true});
try{
 const page=await browser.newPage({viewport:{width:1280,height:960}});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route(/^https?:\/\//,r=>r.abort());
 await page.addInitScript(()=>{const listeners=[];window.__messages=[];window.__receive=m=>listeners.forEach(f=>f({data:m}));window.chrome={webview:{postMessage:m=>window.__messages.push(m),addEventListener:(_,f)=>listeners.push(f)}}});
 await page.goto(new URL('./index.html',import.meta.url).href);
 assert.equal(await page.locator('[aria-label="资源类型"] button').first().getAttribute('id'),'type-modpack');
 assert.equal(await page.locator('[aria-label="资源类型"] button').count(),6);
 const send=m=>page.evaluate(m=>window.__receive(m),m);
 const last=async(op='list')=>page.evaluate(op=>window.__messages.filter(m=>m.type==='platform'&&m.operation===op).at(-1),op);
 await send({type:'browse',launcher:{presets:[],selected:'',config:{},task:{}}});
 await send({type:'browsePlugins',minecraft:'1.21.1'});
 assert.equal(await page.locator('#type-plugin').getAttribute('aria-selected'),'true');assert.equal(await page.locator('#filter-mc').inputValue(),'1.21.1');
 let req=await last();assert.equal(req.resourceType,'plugin');assert.equal(req.minecraft,'1.21.1');assert.equal(req.loader,'');
 const item={id:'curseforge-123',projectId:'123',source:'curseforge',resourceType:'plugin',name:'DEMO server plugin',description:'DEMO executable JAR',minecraftVersions:['1.21.1'],loaders:['Paper'],authors:['DEMO author']};
 await send({type:'platformList',source:'curseforge',resourceType:'plugin',page:1,totalPages:1,requestId:req.requestId,items:[item]});
 assert.equal(await page.locator('#cards [data-pack-id="starrail-harpy-express"]').count(),0);
 await page.locator('#cards [data-pack-id="curseforge-123"]').click();req=await last('detail');assert.equal(req.resourceType,'plugin');
 await send({type:'platformDetail',source:'curseforge',resourceType:'plugin',id:'123',requestId:req.requestId,item});
 assert.match(await page.locator('#detail-guidance').textContent(),/plugins/);assert.match(await page.locator('#detail-guidance').textContent(),/PCL CE/);
 assert.equal(await page.locator('#detail-import').isVisible(),false);assert.equal(await page.locator('#detail-download').isVisible(),true);
 await page.locator('#detail-download').click();req=await last('download');assert.equal(req.resourceType,'plugin');assert.equal(req.id,'123');assert.equal(req.source,'curseforge');
 await page.keyboard.press('Escape');await page.locator('#type-modpack').click();assert.equal(await page.locator('#type-modpack').getAttribute('aria-selected'),'true');
 for(const width of [360,840,1280,1600]){await page.setViewportSize({width,height:960});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true)}
 assert.deepEqual(errors,[]);console.log('PASS modpack-first order, server-version deep link, plugin metadata/native download and responsive layout');
}finally{await browser.close()}
