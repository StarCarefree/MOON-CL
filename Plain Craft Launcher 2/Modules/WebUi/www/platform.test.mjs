// Isolated browser contract tests: DEMO platform data only; no downloads or remote network.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const require=createRequire(import.meta.url);
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const browser=await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
const errors=[];
const item=(source,id,extra={})=>({id:`${source}-${id}`,projectId:id,source,name:`DEMO ${source} ${id}`,description:'DEMO plain intro',minecraftVersions:['1.20.1'],loaders:['Fabric'],authors:['DEMO writer'],categories:['adventure'],website:`https://${source==='curseforge'?'www.curseforge.com/minecraft/modpacks':'modrinth.com/modpack'}/demo`,version:'',...extra});
const cf=item('curseforge','123'); const mr=item('modrinth','abcd1234');
const state={type:'browse',launcher:{presets:[{id:'starrail-harpy-express',name:'SRE DEMO fixture',minecraft:'1.21.1',version:'DEMO',installed:false}],selected:'starrail-harpy-express',task:{},config:{}}};
let page;
async function setup(){
 const p=await browser.newPage({viewport:{width:1280,height:960}});
 p.on('pageerror',e=>errors.push(e.message)); await p.route(/^https?:\/\//,r=>r.abort());
 await p.addInitScript(()=>{const listeners=[];window.__messages=[];window.__receive=data=>listeners.forEach(f=>f({data}));window.chrome={webview:{postMessage:m=>window.__messages.push(m),addEventListener:(_,f)=>listeners.push(f)}}});
 await p.goto(new URL('./index.html',import.meta.url).href); return p;
}
const send=m=>page.evaluate(m=>window.__receive(m),m);
const messages=()=>page.evaluate(()=>window.__messages);
const last=async (op='list')=>(await messages()).filter(m=>m.type==='platform'&&m.operation===op).at(-1);
const card=id=>page.locator(`#cards [data-pack-id="${id}"]`);
const reply=(request,items,totalPages=4)=>send({type:'platformList',source:request.source,page:request.page,totalPages,items,requestId:request.requestId});
try{
 page=await setup(); await page.clock.install();
 assert.deepEqual(await messages(),[{type:'bootstrap'}]);
 assert.equal(await page.locator('#source-curseforge').getAttribute('aria-selected'),'true');
 assert.equal(await page.locator('#include-mcmod').isChecked(),false);
 await send(state); let req=await last();
 assert.equal(req.source,'curseforge'); assert.equal(req.page,1); assert.equal(req.query,'');
 assert.equal((await messages()).some(m=>m.type==='mcmod'),false);
 await reply(req,[cf]); assert.equal(await card(cf.id).count(),1); assert.equal(await card(mr.id).count(),0);
 assert.equal(await page.locator('#cards >button').first().getAttribute('data-pack-id'),'starrail-harpy-express');
 assert.equal(await page.locator('#filter-category').isDisabled(),true);
 await send(state); assert.equal((await messages()).filter(m=>m.type==='platform'&&m.operation==='list').length,1);
 console.log('PASS default CurseForge only; no MCmod requests; source labels and native SRE retained');

 await page.locator('#source-modrinth').click(); let mrReq=await last();
 assert.equal(mrReq.source,'modrinth'); assert.equal(await card(cf.id).count(),0);
 await reply({...mrReq,source:'curseforge'},[cf]); assert.equal(await card(cf.id).count(),0);
 await reply(mrReq,[mr]); assert.equal(await card(mr.id).count(),1);
 assert.equal(await page.locator('#include-mcmod').isChecked(),false);
 await page.locator('#source-curseforge').click(); assert.equal(await card(cf.id).count(),1); assert.equal(await card(mr.id).count(),0);
 await page.locator('#source-modrinth').click(); assert.equal(await card(mr.id).count(),1);
 console.log('PASS platform switches/cache never mix CurseForge, Modrinth or MCmod');

 const beforeSearch=(await messages()).length;
 await page.locator('#search').fill('adventure'); await page.clock.fastForward(349);
 assert.equal((await messages()).length,beforeSearch); await page.clock.fastForward(1);
 req=await last(); assert.equal(req.query,'adventure'); assert.equal(req.source,'modrinth'); assert.equal(req.page,1);
 await reply(req,[mr]);
 await page.locator('#filter-mc').selectOption('1.20.1'); req=await last(); assert.equal(req.minecraft,'1.20.1');
 await page.locator('#filter-loader').selectOption('Fabric'); req=await last(); assert.equal(req.loader,'Fabric');
 await page.locator('#filter-sort').selectOption('updated'); req=await last(); assert.equal(req.sort,'updated');
 await reply(req,[mr],5); await page.locator('#platform-next-page').click(); const second=await last();
 assert.equal(second.page,2); assert.equal(second.query,'adventure'); assert.equal(second.loader,'Fabric'); assert.equal(second.sort,'updated');
 await reply(second,[item('modrinth','bbbb1234')]); await reply(req,[mr]);
 assert.equal(await card('modrinth-bbbb1234').count(),1); assert.equal(await card(mr.id).count(),0);
 await page.locator('#platform-retry').evaluate(node=>node.hidden=false);
 await page.evaluate(()=>requestPlatformPage(2,true)); const retry=await last();
 await send({type:'platformError',source:'modrinth',operation:'list',page:2,requestId:second.requestId,message:'DEMO stale error'});
 assert.notEqual(await page.locator('#platform-status').getAttribute('data-state'),'error');
 await reply(retry,[mr],5);
 console.log('PASS remote debounced query, filters/sort, pagination and stale response/error isolation');

 await page.locator('#search').evaluate(n=>n.dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true})));
 await page.locator('#search').fill('未完成'); let beforeIme=(await messages()).length;
 await page.clock.fastForward(1000); assert.equal((await messages()).length,beforeIme);
 await page.locator('#search').evaluate(n=>n.dispatchEvent(new CompositionEvent('compositionend',{bubbles:true})));
 await page.clock.fastForward(350); req=await last(); assert.equal(req.query,'未完成'); await reply(req,[mr]);
 await page.locator('#reset-filters').click();
 assert.deepEqual(await page.evaluate(()=>platformFilters()),{source:'modrinth',query:'',minecraft:'',loader:'',sort:'relevance'});
 assert.equal(await card(mr.id).count(),1);
 console.log('PASS IME does not submit partial composition; reset clears platform filters');

 await card(mr.id).click(); let detail=await last('detail');
 assert.equal(detail.source,'modrinth'); assert.equal(detail.id,'abcd1234');
 assert.equal((await messages()).some(m=>m.type==='launcher'&&m.operation==='select'&&m.id===mr.id),false);
 await send({type:'platformDetail',source:'curseforge',id:detail.id,requestId:detail.requestId,item:cf});
 assert.match(await page.locator('#detail-name').textContent(),/modrinth/);
 await send({type:'platformError',source:'modrinth',operation:'detail',id:detail.id,requestId:detail.requestId,message:'DEMO detail unavailable'});
 await page.locator('#detail-retry').click(); const newer=await last('detail'); assert.notEqual(newer.requestId,detail.requestId);
 await send({type:'platformDetail',source:'modrinth',id:detail.id,requestId:detail.requestId,item:{...mr,name:'DEMO stale name'}});
 assert.notEqual(await page.locator('#detail-name').textContent(),'DEMO stale name');
 await send({type:'platformDetail',source:'modrinth',id:detail.id,requestId:newer.requestId,item:{...mr,version:'DEMO real 3.0',description:'<img src=x onerror=alert(1)> plain',authors:['DEMO actual author']}});
 assert.equal(await page.locator('#detail-version').textContent(),'DEMO real 3.0');
 assert.equal(await page.locator('#detail-authors').textContent(),'DEMO actual author');
 assert.equal(await page.locator('#detail-description img').count(),0);
 assert.equal(await page.locator('#detail-native').isVisible(),false);
 assert.doesNotMatch(await page.locator('#detail-guidance').textContent(),/暂无 CurseForge/);
 await page.locator('#detail-download').click(); const download=await last('download');
 assert.equal(download.id,'abcd1234'); assert.equal(download.source,'modrinth');
 await send({type:'platformError',source:'modrinth',operation:'download',id:download.id,requestId:download.requestId,message:'DEMO open failed'});
 await page.locator('#download-retry').click(); const download2=await last('download');
 await send({type:'platformError',source:'modrinth',operation:'download',id:download.id,requestId:download.requestId,message:'DEMO stale download error'});
 assert.equal(await page.locator('#download-retry').isVisible(),false);
 assert.notEqual(download2.requestId,download.requestId);
 await page.keyboard.press('Escape');
 console.log('PASS metadata, plain text, detail retry and source-specific native download messages');

 await page.locator('#include-mcmod').check(); const mcRequest=(await messages()).filter(m=>m.type==='mcmod'&&m.operation==='list').at(-1);
 assert.ok(mcRequest); await send({type:'mcmodList',page:mcRequest.page,totalPages:2,requestId:mcRequest.requestId,items:[{id:'mcmod-1426',wikiId:1426,source:'mcmod',name:'DEMO encyclopedia'}]});
 assert.equal(await card('mcmod-1426').count(),1); assert.equal(await card(mr.id).count(),1);
 await page.locator('#source-curseforge').click(); assert.equal(await page.locator('#include-mcmod').isChecked(),false);
 assert.equal(await card('mcmod-1426').count(),0); assert.equal(await card(mr.id).count(),0);
 await page.locator('#source-mcmod').click(); assert.equal(await card('mcmod-1426').count(),1); assert.equal(await card(cf.id).count(),0);
 await page.locator('#search').fill('from encyclopedia'); const lastCount=(await messages()).filter(m=>m.type==='platform').length;
 await page.clock.fastForward(500); assert.equal((await messages()).filter(m=>m.type==='platform').length,lastCount);
 await page.locator('#source-modrinth').click(); req=await last(); assert.equal(req.source,'modrinth'); assert.equal(req.query,'from encyclopedia');
 assert.equal(await page.locator('#include-mcmod').isChecked(),false);
 console.log('PASS encyclopedia opt-in mixing, independent browsing and switching back resets mixing');

 await reply(req,[],1); await page.evaluate(()=>requestPlatformPage(1,true)); req=await last();
 await page.clock.fastForward(60001); assert.match(await page.locator('#platform-message').textContent(),/超时/);
 await page.locator('#platform-retry').click(); req=await last(); await reply(req,[mr],1);
 for(const width of [360,840,1280,1600]) {await page.setViewportSize({width,height:960}); assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true)}
 assert.deepEqual(errors,[]); await page.close();
 console.log('PASS timeout/retry, source tabs responsive layout and no page errors');
 console.log('All platform browser tests passed.');
}finally{await browser.close()}
