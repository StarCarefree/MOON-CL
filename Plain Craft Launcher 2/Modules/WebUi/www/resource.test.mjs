import {onlyExplorer} from './test-explorers.mjs';
// Resource-kind contract regression. DEMO metadata only; external traffic/downloads are blocked.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const require=createRequire(import.meta.url);
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const browser=await chromium.launch({channel:'msedge',headless:true});
const errors=[];
try {
 const page=await browser.newPage({viewport:{width:1280,height:960}});
 page.on('pageerror',e=>errors.push(e.message)); await page.route(/^https?:\/\//,r=>r.abort());
 await page.addInitScript(()=>{const listeners=[];window.__messages=[];window.__receive=data=>listeners.forEach(f=>f({data}));window.chrome={webview:{postMessage:m=>window.__messages.push(m),addEventListener:(_,f)=>listeners.push(f)}}});
 await page.goto(new URL('./index.html',import.meta.url).href);
 const send=m=>page.evaluate(m=>window.__receive(m),m);
 const messages=()=>page.evaluate(()=>window.__messages);
 const last=async(type='platform',op='list')=>(await messages()).filter(m=>m.type===type&&m.operation===op).at(-1);
 const card=id=>page.locator(`#cards [data-pack-id="${id}"]`);
 const item=(resourceType,source='curseforge',id='123')=>({id:`${source}-${id}`,projectId:id,source,resourceType,name:`DEMO ${resourceType}`,description:'DEMO plain resource',authors:[`DEMO ${resourceType} writer`],minecraftVersions:['1.20.1'],version:`DEMO ${resourceType} release`});
 const listReply=(req,items)=>send({type:'platformList',source:req.source,resourceType:req.resourceType||'modpack',requestId:req.requestId,page:req.page,totalPages:2,items});
 const snap={type:'browse',launcher:{presets:[{id:'starrail-harpy-express',name:'SRE DEMO fixture',version:'DEMO',installed:false}],selected:'starrail-harpy-express',task:{},config:{}}};
 await send(snap); let request=await last(); await listReply(request,[item('modpack')]);
 assert.equal(await page.locator('[aria-label="资源类型"] button').count(),5);
 let stale;
 for(const kind of ['datapack','world','shader','resourcepack']) {
  await page.locator(`#type-${kind}`).click(); request=await last(); assert.equal(request.resourceType,kind);
  assert.equal(request.loader,''); assert.equal(await card('starrail-harpy-express').count(),0);
  assert.equal(await page.locator('.stage').isVisible(),false); assert.equal(await page.locator('#filter-loader').isDisabled(),true);
  if(stale) await listReply(stale,[item(stale.resourceType)]);
  await send({type:'platformList',source:'curseforge',resourceType:'modpack',requestId:request.requestId,page:1,totalPages:2,items:[item('modpack')]});
  assert.equal(await card('curseforge-123').count(),0);
  await listReply(request,[item(kind),item('modpack','curseforge','999')]);
  assert.equal(await card('curseforge-999').count(),0); assert.equal(await card('curseforge-123').count(),1);
  await card('curseforge-123').click(); let detail=await last('platform','detail');
  assert.equal(detail.resourceType,kind); assert.equal(detail.id,'123');
  await send({type:'platformDetail',source:'curseforge',resourceType:kind,id:'123',requestId:detail.requestId,item:item(kind)});
  assert.equal(await page.locator('#detail-authors').textContent(),`DEMO ${kind} writer`);
  assert.equal(await page.locator('#detail-import').isVisible(),false);
  await page.locator('#detail-download').click(); const download=await last('platform','download');
  assert.equal(download.resourceType,kind); assert.equal(download.id,'123');
  await page.keyboard.press('Escape'); stale=request;
 }
 await page.locator('#type-modpack').click();
 assert.equal(await card('starrail-harpy-express').count(),1); assert.equal(await page.locator('.stage').isVisible(),true);
 await card('curseforge-123').click(); assert.equal(await page.locator('#detail-authors').textContent(),'DEMO modpack writer');
 assert.equal(await page.locator('#detail-import').isVisible(),true); await page.keyboard.press('Escape');
 console.log('PASS five lists, type-scoped caches, stale/wrong-kind isolation, pack-only SRE/import and typed download');

 await onlyExplorer(page,'modrinth'); await page.locator('#type-shader').click();
 request=await last(); assert.equal(request.source,'modrinth'); assert.equal(request.resourceType,'shader');
 await listReply(request,[item('shader','modrinth','abcd1234')]); assert.equal(await card('modrinth-abcd1234').count(),1);
 let count=(await messages()).length; await page.locator('#type-world').click(); assert.equal((await messages()).length,count);
 assert.equal(await page.locator('#modrinth-status').getAttribute('data-state'),'unsupported');
 assert.equal(await card('modrinth-abcd1234').count(),0); assert.match(await page.locator('#modrinth-message').textContent(),/其他探索器/);
 await onlyExplorer(page,'mcmod'); assert.equal((await messages()).length,count);
 assert.equal(await page.locator('#mcmod-status').getAttribute('data-state'),'unsupported');
 await page.locator('#type-shader').click(); assert.equal((await messages()).length,count);
 console.log('PASS Modrinth supported types and explicit unavailable map/MCmod states with no network request');

 await page.locator('#type-datapack').click(); const dpRequest=await last('mcmod'); assert.equal(dpRequest.resourceType,'datapack');
 const dp={id:'mcmod-datapack-24437',wikiId:24437,source:'mcmod',resourceType:'datapack',name:'DEMO encyclopedia datapack',authors:['DEMO DP writer'],curseForgeUrl:'https://www.curseforge.com/minecraft/data-packs/demo',links:[]};
 await send({type:'mcmodList',page:1,totalPages:1,requestId:dpRequest.requestId,resourceType:'datapack',items:[dp]});
 await card(dp.id).click(); const dpDetail=await last('mcmod','detail'); assert.equal(dpDetail.id,24437); assert.equal(dpDetail.resourceType,'datapack');
 await send({type:'mcmodDetail',resourceType:'datapack',item:dp}); assert.equal(await page.locator('#detail-authors').textContent(),'DEMO DP writer');
 assert.equal(await page.locator('#detail-import').isVisible(),false); assert.equal(await page.locator('#detail-download').isVisible(),true);
 await page.locator('#detail-download').click(); assert.equal((await last('mcmod','download')).resourceType,'datapack'); await page.keyboard.press('Escape');
 await page.locator('#type-modpack').click(); const packRequest=await last('mcmod');
 const pack={id:'mcmod-24437',wikiId:24437,source:'mcmod',resourceType:'modpack',name:'DEMO encyclopedia pack',authors:['DEMO pack writer']};
 await send({type:'mcmodList',page:1,totalPages:1,requestId:packRequest.requestId,resourceType:'modpack',items:[pack]});
 await card(pack.id).click(); await send({type:'mcmodDetail',resourceType:'modpack',item:pack}); assert.equal(await page.locator('#detail-authors').textContent(),'DEMO pack writer');
 await page.keyboard.press('Escape'); await page.locator('#type-datapack').click(); await card(dp.id).click(); assert.equal(await page.locator('#detail-authors').textContent(),'DEMO DP writer');
 await page.keyboard.press('Escape');
 console.log('PASS encyclopedia datapack/class vs modpack identity collision and type-aware author/CF links');

 for(const width of [360,840,1280,1600]) {await page.setViewportSize({width,height:960}); assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true)}
 assert.deepEqual(errors,[]); console.log('PASS five-type responsive layout and no browser errors');
}finally {await browser.close()}
