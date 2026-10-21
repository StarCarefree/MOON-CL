// Multi-explorer browser contracts. DEMO data only; no real downloads, installs or external network.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE||'playwright');
const browser=await chromium.launch({channel:'msedge',headless:true});
const errors=[];
try {
 const page=await browser.newPage({viewport:{width:1280,height:960}}); page.on('pageerror',e=>errors.push(e.message));
 await page.route(/^https?:\/\//,r=>r.abort());
 await page.addInitScript(()=>{const listeners=[];window.__messages=[];window.__receive=m=>listeners.forEach(f=>f({data:m}));window.chrome={webview:{postMessage:m=>window.__messages.push(m),addEventListener:(_,f)=>listeners.push(f)}}});
 await page.goto(new URL('./index.html',import.meta.url).href); await page.clock.install();
 const send=m=>page.evaluate(m=>window.__receive(m),m);
 const messages=()=>page.evaluate(()=>window.__messages);
 const last=async(source,operation='list',type='platform')=>(await messages()).filter(m=>m.type===type&&m.operation===operation&&(!source||m.source===source)).at(-1);
 const card=id=>page.locator(`#cards [data-pack-id="${id}"]`);
 const pack=(source,id,slug,name)=>({id:`${source}-${id}`,source,projectId:id,slug,name,resourceType:'modpack',website:`https://${source==='curseforge'?'www.curseforge.com/minecraft/modpacks':'modrinth.com/modpack'}/${slug}`});
 const cf=pack('curseforge','123','real-pack','DEMO CF'), mr=pack('modrinth','abcd1234','other-pack','DEMO MR');
 const index={id:'mcl-demo',name:'DEMO MCL index row',version:'DEMO-1',minecraft:'1.21.1',author:'DEMO index author',loader:'NeoForge'};
 const sre={id:'starrail-harpy-express',name:'SRE DEMO fixture',version:'DEMO',minecraft:'1.21.1',author:'DEMO author',loader:'NeoForge'};
 const snapshot=(presets=[index],config={})=>({type:'browse',launcher:{presets,selected:index.id,task:{},config}});
 const reply=(req,items,totalPages=3)=>send({type:'platformList',source:req.source,page:req.page,resourceType:req.resourceType||'modpack',requestId:req.requestId,totalPages,items});
 assert.equal(await page.locator('#source-mcl').isChecked(),true); assert.equal(await page.locator('#source-curseforge').isChecked(),true);
 assert.equal(await page.locator('#source-modrinth').isChecked(),false); assert.equal(await page.locator('#include-mcmod').isChecked(),true);
 assert.equal(await page.locator('.explorer-option input').first().getAttribute('id'),'source-mcl');
 await send(snapshot([sre,index])); let cfReq=await last('curseforge'); await reply(cfReq,[cf]);
 // round-robin：curseforge 桶在 mcl 桶之前，[cf, sre, index]
 assert.equal(await page.locator('#cards [data-pack-id]').nth(0).getAttribute('data-pack-id'),cf.id);
 assert.equal(await page.locator('#cards [data-pack-id]').nth(2).getAttribute('data-pack-id'),index.id);
 await page.locator('#source-modrinth').check(); let mrReq=await last('modrinth'); await reply(mrReq,[mr]);
 assert.equal(await page.locator('#source-curseforge').isChecked(),true); assert.equal(await card(cf.id).count(),1); assert.equal(await card(mr.id).count(),1);
 await page.locator('#next-page').click(); cfReq=await last('curseforge'); assert.equal(cfReq.page,2);
 assert.match(await page.locator('#page-label').textContent(), /Modrinth · 第 1 页/);
 assert.doesNotMatch(await page.locator('#page-label').textContent(),/CurseForge · 第 2 页/);
 await reply(cfReq,[cf]); await page.locator('#next-page').click(); mrReq=await last('modrinth'); await reply(mrReq,[mr]);
 assert.match(await page.locator('#page-label').textContent(),/CurseForge · 第 2 页/); assert.match(await page.locator('#page-label').textContent(),/Modrinth · 第 2 页/);
 assert.equal(await page.locator('.pagination').count(),1);
 await page.locator('#source-curseforge').uncheck(); assert.equal(await card(cf.id).count(),0); assert.equal(await card(mr.id).count(),1);
 await page.locator('#source-curseforge').check(); assert.equal(await card(cf.id).count(),1); assert.match(await page.locator('#page-label').textContent(),/Modrinth · 第 2 页/);
 console.log('PASS default MCL-first selection, simultaneous CF/MR aggregation and independent pagination/cache');

 await page.locator('#include-mcmod').check(); let mcReq=await last(null,'list','mcmod');
 const mc={id:'mcmod-1426',wikiId:1426,source:'mcmod',resourceType:'modpack',name:'DEMO wiki authoritative',links:[{url:cf.website+'/files/1?from=wiki'},{url:mr.website+'#download'}]};
 await send({type:'mcmodList',page:mcReq.page,totalPages:2,requestId:mcReq.requestId,items:[mc]});
 assert.equal(await card(mc.id).count(),1); assert.equal(await card(cf.id).count(),0); assert.equal(await card(mr.id).count(),0); assert.equal(await card(index.id).count(),1);
 assert.match(await page.locator('#sources-summary-message').textContent(), /当前页 3 个整合包/);
 await page.locator('#include-mcmod').uncheck(); assert.equal(await card(cf.id).count(),1); assert.equal(await card(mr.id).count(),1);
 await page.locator('#include-mcmod').check(); assert.equal(await card(cf.id).count(),0);
 console.log('PASS actual project links prefer MC百科 over both providers; MCL survives and deselecting wiki restores provider rows');

 await page.locator('#next-page').click(); mcReq=await last(null,'list','mcmod');
 const unknown={id:'mcmod-777',wikiId:777,source:'mcmod',resourceType:'modpack',name:'DEMO unknown association',links:[]};
 await send({type:'mcmodList',page:mcReq.page,totalPages:2,requestId:mcReq.requestId,items:[unknown]});
 await send({type:'presentation',visible:false});
 const beforeHiddenLinks=(await messages()).filter(m=>m.type==='mcmod'&&m.operation==='links').length;
 await page.clock.fastForward(1000); assert.equal((await messages()).filter(m=>m.type==='mcmod'&&m.operation==='links').length,beforeHiddenLinks);
 await send({type:'presentation',visible:true});
 assert.equal(await card(cf.id).count(),1); await page.clock.fastForward(500);
 let links=await last(null,'links','mcmod'); assert.equal(links.id,777);
 assert.equal(await page.locator('#association-status').isVisible(), false);
 assert.equal(await page.locator('#sources-summary').isVisible(), true);
 const beforePause=links.requestId;
 await send({type:'presentation',visible:false}); await page.clock.fastForward(50000);
 await send({type:'mcmodLinks',id:777,requestId:beforePause,item:{...unknown,links:[{url:cf.website}]}});
 assert.equal(await card(cf.id).count(),1);
 await send({type:'presentation',visible:true}); await page.clock.fastForward(500); links=await last(null,'links','mcmod');
 assert.notEqual(links.requestId,beforePause);
 let count=(await messages()).filter(m=>m.type==='mcmod'&&m.operation==='links').length;
 await page.clock.fastForward(1000); assert.equal((await messages()).filter(m=>m.type==='mcmod'&&m.operation==='links').length,count);
 await send({type:'mcmodError',operation:'links',id:777,requestId:links.requestId,message:'DEMO rate limited'});
 await page.clock.fastForward(10000); assert.equal((await messages()).filter(m=>m.type==='mcmod'&&m.operation==='links').length,count);
 assert.equal(await page.locator('#association-retry').isVisible(),true);
 await page.locator('#association-retry').click(); await page.clock.fastForward(500); links=await last(null,'links','mcmod');
 await send({type:'mcmodLinks',resourceType:'modpack',id:777,requestId:links.requestId,item:{...unknown,links:[{url:cf.website}]}});
 assert.equal(await card(cf.id).count(),0); assert.equal(await card(mr.id).count(),1);
 assert.equal(await page.locator('#association-retry').isVisible(),false);
 console.log('PASS bounded association lookup, no concurrent burst, rate-limit pause/retry and exact progressive de-duplication');

 await page.evaluate(()=>requestPage(2,true)); mcReq=await last(null,'list','mcmod');
 const mrOnly={id:'mcmod-778',wikiId:778,source:'mcmod',resourceType:'modpack',name:'DEMO MR-only wiki',links:[{url:mr.website}]};
 await send({type:'mcmodList',page:2,totalPages:2,requestId:mcReq.requestId,items:[mrOnly]});
 assert.equal(await card(mr.id).count(),0); await card(mrOnly.id).click();
 await send({type:'mcmodDetail',item:{...mrOnly,modrinthUrl:mr.website}});
 assert.equal(await page.locator('#detail-download').isVisible(),true);
 assert.match(await page.locator('#detail-guidance').textContent(),/Modrinth/);
 await page.locator('#detail-download').click(); assert.equal((await last(null,'download','mcmod')).id,778);
 await page.keyboard.press('Escape');
 console.log('PASS wiki-only presentation retains the native download entry for an MR-only association');

 await page.locator('#include-mcmod').uncheck(); await page.locator('#source-modrinth').uncheck();
 await page.locator('#type-world').click(); cfReq=await last('curseforge'); assert.equal(cfReq.resourceType,'world');
 await reply(cfReq,[{...cf,id:'curseforge-999',projectId:'999',resourceType:'world',name:'DEMO map'}]);
 assert.equal(await card(index.id).count(),0); assert.equal(await page.locator('.stage').isVisible(),true); assert.equal(await page.locator('#pack-name').textContent(),'DEMO map');
 await page.locator('#type-modpack').click(); assert.equal(await card(index.id).count(),1);
 assert.equal(await page.locator('#next-page').isDisabled(),false);
 console.log('PASS MCL stays modpack-only and cached resource/source page totals restore');

 await page.locator('#source-curseforge').uncheck();
 const url='https://catalog.example/index.json';
 await send(snapshot([sre,index],{catalogUrls:[url],catalogUrl:url}));
 assert.equal((await messages()).at(-1).operation,'refresh'); const refreshes=(await messages()).filter(m=>m.type==='launcher'&&m.operation==='refresh').length;
 await send(snapshot([sre,index],{catalogUrls:[url],catalogUrl:url})); assert.equal((await messages()).filter(m=>m.type==='launcher'&&m.operation==='refresh').length,refreshes);
 const updated={...index,name:'DEMO actual refreshed index',version:'DEMO-2'};
 await send(snapshot([sre,{...index,name:'DEMO actual refreshed index',version:'DEMO-2'}],{catalogUrls:[url],catalogUrl:url,catalogSource:url}));
 assert.match(await card(index.id).textContent(),/DEMO actual refreshed index/); assert.match(await page.locator('#mcl-message').textContent(),/catalog\.example/);
 assert.equal(await card(sre.id).count(),1);
 await page.locator('#source-mcl').uncheck(); assert.equal(await card(index.id).count(),0); assert.equal(await page.locator('#cards >button').count(),0);
 assert.equal(await page.locator('#cards >button').count(),0);
 await page.locator('#source-mcl').check(); assert.equal(await card(index.id).count(),1);
 console.log('PASS remote-index request once, authoritative snapshot refresh, MCL default priority and no-explorer empty state');

 await page.locator('#source-curseforge').check(); await page.locator('#source-modrinth').check();
 await page.locator('#search').fill('shared query'); await page.clock.fastForward(350);
 assert.equal((await last('curseforge')).query,'shared query'); assert.equal((await last('modrinth')).query,'shared query');
 assert.equal((await last('curseforge')).page,1); assert.equal((await last('modrinth')).page,1);
 for(const width of [360,840,1280,1600]) {await page.setViewportSize({width,height:960}); assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true);}
 assert.deepEqual(errors,[]); console.log('PASS shared query fan-out, responsive explorer controls and no browser errors');
} finally {await browser.close();}
