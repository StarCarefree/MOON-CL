// Source summary + dynamic taxonomy contracts. All metadata is DEMO; no external traffic.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE||'playwright');
const browser=await chromium.launch({channel:'msedge',headless:true});
const errors=[];
try{
 const page=await browser.newPage({viewport:{width:1280,height:960}}); page.on('pageerror',e=>errors.push(e.message));
 await page.route(/^https?:\/\//,r=>r.abort());
 await page.addInitScript(()=>{const listeners=[];window.__messages=[];window.__receive=m=>listeners.forEach(f=>f({data:m}));window.chrome={webview:{postMessage:m=>window.__messages.push(m),addEventListener:(_,f)=>listeners.push(f)}}});
 await page.goto(new URL('./index.html',import.meta.url).href); await page.clock.install();
 const send=m=>page.evaluate(m=>window.__receive(m),m);
 const messages=()=>page.evaluate(()=>window.__messages);
 const last=async(source,operation='list')=>(await messages()).filter(m=>m.source===source&&m.operation===operation).at(-1);
 const mcLast=async(op='list')=>(await messages()).filter(m=>m.type==='mcmod'&&m.operation===op).at(-1);
 const summary=()=>page.locator('#sources-summary-message').textContent();
 const cats=(source,type,items,req)=>send({type:source==='mcmod'?'mcmodCategories':'platformCategories',source,resourceType:type,requestId:req.requestId,items});
 const list=(req,items)=>send({type:'platformList',source:req.source,resourceType:req.resourceType||'modpack',page:req.page,totalPages:2,requestId:req.requestId,items});
 const item=(source,id,type,name='DEMO resource')=>({id:`${source}-${id}`,projectId:id,source,resourceType:type,name,website:`https://${source==='curseforge'?'curseforge.com/minecraft':'modrinth.com'}/${type==='world'?'worlds':type}/${id}`});
 await send({type:'browse',launcher:{presets:[],selected:'',config:{},task:{}}});
 await page.locator('#source-mcl').uncheck(); await page.locator('#source-modrinth').check(); await page.locator('#include-mcmod').check();
 let cf=await last('curseforge'),mr=await last('modrinth'),mc=await mcLast();
 await list(cf,[item('curseforge','123','modpack')]); await list(mr,[item('modrinth','abcd1234','modpack')]);
 await send({type:'mcmodList',page:1,totalPages:1,requestId:mc.requestId,items:[{id:'mcmod-1',wikiId:1,source:'mcmod',resourceType:'modpack',name:'DEMO wiki',links:[{url:'https://curseforge.com/minecraft/modpack/123'}]}]});
 assert.match(await summary(),/当前页 3 个整合包/); assert.match(await summary(),/CurseForge 1/);assert.match(await summary(),/Modrinth 1/);assert.match(await summary(),/MC百科 1/);
 for(const id of ['platform-status','modrinth-status','mcmod-status','association-status','mcl-status']) assert.equal(await page.locator('#'+id).isVisible(),false);
 assert.equal(await page.locator('#sources-summary').isVisible(),true);
 await page.evaluate(()=>requestPlatformPage(1,true,'modrinth')); mr=await last('modrinth');
 await send({type:'platformError',source:'modrinth',operation:'list',page:1,requestId:mr.requestId,message:'DEMO server failure'});
 assert.match(await summary(),/DEMO server failure/);assert.equal(await page.locator('#modrinth-retry').isVisible(),true);
 assert.equal(await page.locator('#modrinth-retry').evaluate(n=>n.parentElement.id),'sources-summary-actions');
 console.log('PASS three providers share one de-duplicated-count message, retaining per-source retry/error states');

 await page.locator('#type-datapack').click();
 let cfc=await last('curseforge','categories'),mrc=await last('modrinth','categories'),mcc=await mcLast('categories');
 assert.equal(cfc.resourceType,'datapack');assert.equal(mrc.resourceType,'datapack');assert.equal(mcc.resourceType,'datapack');
 await cats('curseforge','datapack',[{id:'6000',name:'DEMO CF structures'}],cfc);
 await cats('modrinth','datapack',[{id:'adventure',name:'DEMO MR adventure'}],mrc);
 await cats('mcmod','datapack',[{id:'35',name:'结构'}],mcc);
 assert.deepEqual(await page.locator('#filter-category option').evaluateAll(ns=>ns.slice(1).map(n=>n.value)),['curseforge:6000','modrinth:adventure','mcmod:35']);
 await page.locator('#filter-category').selectOption('curseforge:6000');cf=await last('curseforge');mr=await last('modrinth');mc=await mcLast();
 assert.equal(cf.category,'6000');assert.equal(mr.category,undefined);assert.equal(mc.category,undefined);
 await page.locator('#filter-category').selectOption('modrinth:adventure');mr=await last('modrinth');assert.equal(mr.category,'adventure');assert.equal((await last('curseforge')).category,'6000');
 // The CF unfiltered key may be served from cache/pending; inspect authoritative filter snapshot, not last dispatched message.
 assert.equal(await page.evaluate(()=>platformFilters('curseforge').category),undefined);
 await page.locator('#filter-category').selectOption('mcmod:35');mc=await mcLast();assert.equal(mc.category,35);assert.equal(mc.resourceType,'datapack');
 await page.locator('#source-curseforge').uncheck();assert.equal(await page.locator('#filter-category option[value="curseforge:6000"]').count(),0);
 assert.equal(await page.locator('#filter-category option[value="modrinth:adventure"]').count(),1);
 console.log('PASS per-source datapack tags, distinct numeric/slug namespaces and source-scoped request parameters');

 await page.locator('#type-world').click();await page.locator('#source-curseforge').check();cfc=await last('curseforge','categories');assert.equal(cfc.resourceType,'world');
 await cats('curseforge','world',[{id:'253',name:'Survival'}],cfc);
 assert.deepEqual(await page.locator('#filter-category option').evaluateAll(ns=>ns.slice(1).map(n=>n.value)),['curseforge:253']);
 await page.locator('#filter-category').selectOption('curseforge:253');assert.equal((await last('curseforge')).category,'253');
 assert.match(await summary(),/Modrinth：此类型不可用/);
 await page.locator('#type-resourcepack').click();cfc=await last('curseforge','categories');mrc=await last('modrinth','categories');
 await page.locator('#type-shader').click();let shaderCF=await last('curseforge','categories'),shaderMR=await last('modrinth','categories');
 await cats('curseforge','resourcepack',[{id:'393',name:'16x'}],cfc);await cats('modrinth','resourcepack',[{id:'16x',name:'16x'}],mrc);
 assert.equal(await page.locator('#filter-category option[value="curseforge:393"]').count(),0);assert.equal(await page.locator('#filter-category option[value="modrinth:16x"]').count(),0);
 await cats('curseforge','shader',[{id:'6553',name:'DEMO CF fantasy'}],shaderCF);
 await cats('modrinth','shader',[{id:'fantasy',name:'DEMO MR fantasy'}],shaderMR);
 await page.locator('#filter-category').selectOption('modrinth:fantasy');assert.equal((await last('modrinth')).category,'fantasy');
 await page.locator('#source-modrinth').uncheck();assert.equal(await page.locator('#filter-category').inputValue(),'');assert.equal(await page.locator('#filter-category option[value="modrinth:fantasy"]').count(),0);
 await page.locator('#source-modrinth').check();await page.locator('#type-resourcepack').click();
 assert.equal(await page.locator('#filter-category option[value="modrinth:16x"]').count(),1);assert.equal(await page.locator('#filter-category option[value="modrinth:fantasy"]').count(),0);
 console.log('PASS world/shader/resourcepack taxonomies, late-response isolation and cached source/type regeneration');

 await page.evaluate(()=>{categoryState.items.delete('curseforge:resourcepack');ensureCategories(true)});cfc=await last('curseforge','categories');
 await send({type:'platformError',source:'curseforge',resourceType:'resourcepack',operation:'categories',requestId:cfc.requestId,message:'DEMO tags failed'});
 assert.equal(await page.locator('#category-retry').isVisible(),true);assert.match(await page.locator('#category-help').textContent(),/CurseForge/);
 await page.locator('#category-retry').click();cfc=await last('curseforge','categories');await cats('curseforge','resourcepack',[{id:'393',name:'16x'}],cfc);
 assert.equal(await page.locator('#filter-category option[value="curseforge:393"]').count(),1);
 for(const width of [360,840,1280,1600]){await page.setViewportSize({width,height:960});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true)}
 assert.deepEqual(errors,[]);console.log('PASS taxonomy retry, responsive combined summary and no page errors');
}finally{await browser.close()}
