// Node 22+ and local Chromium. Tests the real launcher webpage with a mocked native bridge.
// No launcher process/account/game is touched. Output: mooncl-cloud/build/catalog-ui/.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = join(root, 'mooncl-cloud/build/catalog-ui', String(Date.now()));
await mkdir(output, { recursive: true });
const files = new Map();
for (const name of ['index.html', 'app.js', 'app.css']) files.set('/' + name, await readFile(join(root, 'Plain Craft Launcher 2/Modules/WebUi/www', name)));
const server = createServer((request, response) => {
  const path = request.url === '/' ? '/index.html' : request.url;
  if (!files.has(path)) { response.writeHead(404).end(); return; }
  response.setHeader('Content-Type', path.endsWith('.js') ? 'text/javascript' : path.endsWith('.css') ? 'text/css' : 'text/html;charset=utf-8');
  response.end(files.get(path));
});
await new Promise((accept) => server.listen(0, '127.0.0.1', accept));
const origin = `http://127.0.0.1:${server.address().port}`;
const browserPath = process.env.MOONCL_BROWSER || [join(process.env['ProgramFiles(x86)'] || '', 'Microsoft/Edge/Application/msedge.exe'),
  join(process.env.ProgramFiles || '', 'Google/Chrome/Application/chrome.exe'), '/usr/bin/chromium'].find(existsSync);
assert.ok(browserPath, 'Set MOONCL_BROWSER to a Chromium executable');
let browser, socket, browserLog = '', debugOrigin;
const checks = [];
async function until(predicate, label) {
  for (let attempt = 0; attempt < 150; attempt++) {
    try { if (await predicate()) return; } catch { /* startup */ }
    await delay(100);
  }
  throw new Error('Timeout: ' + label);
}
try {
  browser = spawn(browserPath, ['--headless=new', '--disable-gpu', '--disable-gpu-sandbox', '--disable-software-rasterizer', '--no-first-run',
    '--no-default-browser-check', '--remote-debugging-port=0', `--user-data-dir=${join(output, 'profile')}`, 'about:blank'],
  { env: { ...process.env, TEMP: output, TMP: output }, stdio: ['ignore', 'ignore', 'pipe'] });
  browser.on('error', (error) => { browserLog += error.message; });
  browser.stderr.on('data', (chunk) => {
    browserLog += chunk;
    const endpoint = browserLog.match(/DevTools listening on (ws:\/\/[^\s]+)/)?.[1];
    if (endpoint) debugOrigin = 'http://' + new URL(endpoint).host;
  });
  let target;
  await until(async () => { if (!debugOrigin) return false; target = (await (await fetch(debugOrigin + '/json/list')).json()).find(item => item.type === 'page'); return !!target; }, 'browser');
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((accept, reject) => { socket.addEventListener('open', accept, {once:true}); socket.addEventListener('error', reject, {once:true}); });
  let serial = 0;
  const pending = new Map(), exceptions = [];
  socket.addEventListener('message', ({data}) => {
    const message = JSON.parse(data);
    if (message.method === 'Runtime.exceptionThrown') exceptions.push(message.params.exceptionDetails.text);
    const item = pending.get(message.id); if (!item) return;
    pending.delete(message.id); clearTimeout(item.timer);
    if (message.error) item.reject(new Error(message.error.message)); else item.accept(message.result);
  });
  const call = (method, params = {}) => new Promise((accept, reject) => {
    const id = ++serial; const timer = setTimeout(() => { pending.delete(id); reject(new Error('CDP timeout: ' + method)); }, 30000);
    pending.set(id, {accept, reject, timer}); socket.send(JSON.stringify({id, method, params}));
  });
  const evaluate = async (expression) => {
    const result = await call('Runtime.evaluate', {expression, returnByValue:true, awaitPromise:true});
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  };
  const config = {catalogUrl:'https://legacy.example/index.json', quarkServiceUrl:''};
  const snapshot = {type:'browse', launcher:{presets:[{id:'starrail-harpy-express',name:'测试整合包',version:'1.0'}],
    instances:[],selected:'starrail-harpy-express',task:{busy:false,phase:'就绪'},config}};
  await call('Runtime.enable'); await call('Page.enable');
  await call('Page.addScriptToEvaluateOnNewDocument', {source:`window.__messages=[]; window.__bridgeHandlers=[];
    window.__deliver=data=>window.__bridgeHandlers.forEach(handler=>handler({data}));
    window.chrome.webview={addEventListener:(type,handler)=>window.__bridgeHandlers.push(handler),postMessage:message=>{
      window.__messages.push(message); if(message.type==='bootstrap') queueMicrotask(()=>window.__deliver(${JSON.stringify(snapshot)})); }};`});
  await call('Page.navigate', {url:origin});
  await until(() => evaluate("document.body.dataset.link==='live'"), 'native bridge handshake');
  await evaluate("document.getElementById('open-services').click()");
  assert.equal(await evaluate("document.getElementById('catalog-url').tagName"), 'TEXTAREA');
  assert.equal(await evaluate("document.getElementById('catalog-url').value"), config.catalogUrl);
  checks.push('旧单地址显示为多行输入框');
  const setUrls = async (value) => evaluate(`document.getElementById('catalog-url').value=${JSON.stringify(value)}; document.getElementById('catalog-url').dispatchEvent(new Event('input',{bubbles:true}));`);
  const submit = () => evaluate("document.getElementById('services-form').requestSubmit()");
  const messages = () => evaluate("window.__messages.filter(message=>message.type==='launcher' && message.operation==='configure')");
  await setUrls(' https://primary.example/index.json \nhttps://backup.example/index.json\nhttps://primary.example/index.json\n');
  await submit();
  const sent = (await messages()).at(-1);
  assert.deepEqual(sent.catalogUrls, ['https://primary.example/index.json','https://backup.example/index.json']);
  assert.equal(sent.catalogUrl, sent.catalogUrls[0]);
  checks.push('多源按顺序保存、去重并保留旧版首项');
  const deliverConfig = async (value) => evaluate(`window.__deliver(${JSON.stringify({...snapshot,launcher:{...snapshot.launcher,config:value}})})`);
  await deliverConfig({catalogUrl:sent.catalogUrl,catalogUrls:[sent.catalogUrl,'https://wrong.example/index.json'],quarkServiceUrl:''});
  assert.match(await evaluate("document.getElementById('config-status').textContent"), /等待/);
  await deliverConfig({...sent,catalogSource:sent.catalogUrls[1]});
  assert.match(await evaluate("document.getElementById('config-status').textContent"), /确认/);
  assert.match(await evaluate("document.getElementById('catalog-source-status').textContent"), /backup\.example/);
  checks.push('所有备用地址参与保存确认，显示上次成功源');
  for (const value of ['file:///C:/index.json','https://user:pass@example.com/index.json','https://example.com/index.json#',
    Array.from({length:9},(_,i)=>`https://source${i}.example/index.json`).join('\n'),'https://example.com/'+'月'.repeat(1000)]) {
    const count = (await messages()).length;
    await setUrls(value); await submit();
    assert.equal((await messages()).length, count);
    assert.equal(await evaluate("document.getElementById('catalog-url').validity.valid"), false);
  }
  checks.push('非法协议/凭据/片段/超限数量与编码后过长地址被阻止');
  await setUrls(''); await submit();
  const cleared = (await messages()).at(-1);
  assert.deepEqual(cleared.catalogUrls, []); assert.equal(cleared.catalogUrl, '');
  await deliverConfig({...cleared,catalogSource:''});
  checks.push('清空明确发送空数组，不回退旧地址');
  await call('Emulation.setDeviceMetricsOverride', {width:360,height:900,deviceScaleFactor:1,mobile:true});
  assert.ok(await evaluate('document.documentElement.scrollWidth<=window.innerWidth'));
  await writeFile(join(output,'services.png'),Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
  assert.deepEqual(exceptions,[]);
  checks.push('窄屏设置无横向溢出，无脚本异常');
  await writeFile(join(output,'results.json'),JSON.stringify({passed:true,checks},null,2));
  checks.forEach(check=>console.log('PASS: '+check));
  console.log(`${checks.length} checks passed; ${output}`);
} finally {
  socket?.close();
  if (browser?.pid) {
    if (process.platform==='win32') await new Promise(accept=>{const killer=spawn('taskkill',['/PID',String(browser.pid),'/T','/F'],{stdio:'ignore'});killer.on('exit',accept);killer.on('error',accept);});
    else browser.kill('SIGTERM');
  }
  server.close();
  await writeFile(join(output,'browser.log'),browserLog);
}
