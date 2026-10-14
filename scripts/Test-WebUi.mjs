// Hybrid integration smoke. Node 22+, no npm dependencies.
// Start an isolated Debug launcher with --hybrid-smoke, PCL_UI_TEST_DIR and remote-debugging-port.
// Never launches a game, signs into an account, accepts legal terms or installs a pack.
import assert from "node:assert/strict";
import { setTimeout as delay } from "node:timers/promises";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

const port = Number(process.argv[2] || 9435);
const output = process.argv[3];
assert.ok(output, "Provide PCL_UI_TEST_DIR as the third argument");
let target;
for (let i = 0; i < 160; i++) {
  try {
    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    target = targets.find(item => item.url.startsWith("https://pcl.webui/"));
    if (target) break;
  } catch { /* The native-first test hook has not opened Browse yet. */ }
  await delay(250);
}
assert.ok(target, "Browse did not open after the native-first test hook. Check native dialogs/logs.");
const initial = JSON.parse(await readFile(join(output, "native-default.json"), "utf8"));
assert.equal(initial.primaryUi, "native");
assert.equal(initial.view, "native");
assert.equal(initial.browserCreated, false);
assert.equal(initial.nativeVisible, true);
assert.equal(initial.originalLaunchControls, true);
assert.equal(initial.versionsBelowLaunch, true);
console.log("PASS: default WPF shell; original version controls are below the original start button");

const socket = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.addEventListener("open", resolve, {once:true}); socket.addEventListener("error", reject, {once:true}); });
let serial = 0;
const pending = new Map();
socket.addEventListener("message", ({data}) => {
  const message = JSON.parse(data); const item = pending.get(message.id);
  if (!item) return;
  pending.delete(message.id); clearTimeout(item.timer);
  if (message.error) item.reject(new Error(message.error.message)); else item.resolve(message.result);
});
function call(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++serial; const timer = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)); }, 15000);
    pending.set(id, {resolve,reject,timer}); socket.send(JSON.stringify({id,method,params}));
  });
}
async function evaluate(expression) {
  const result = await call("Runtime.evaluate", {expression,returnByValue:true,awaitPromise:true});
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
  return result.result.value;
}
async function waitFor(check, description) {
  for (let i=0; i<100; i++) { if (await check()) return; await delay(120); }
  throw new Error(`Timeout: ${description}`);
}
async function send(message) { await evaluate(`window.chrome.webview.postMessage(${JSON.stringify(message)})`); }
async function status() {
  const baseline = await evaluate("window.__hybridStatusCounter");
  await send({type:"status"});
  await waitFor(() => evaluate(`window.__hybridStatusCounter > ${baseline}`), "native status reply");
  return evaluate("window.__hybridMessages.status");
}
async function nativeReady(page, sub) {
  await waitFor(async () => {
    const current = await status();
    return current.page === page && (sub === undefined || current.sub === sub) && current.nativeVisible && !current.browserVisible &&
      ["ContentStay","LoaderStay","LoaderStayForce"].includes(current.contentState) && !current.modalVisible;
  }, `native page ${page}/${sub}`);
}
try {
  await evaluate(`window.__hybridMessages={}; window.__hybridStatusCounter=0;
    window.chrome.webview.addEventListener('message', e => { const d=e.data; if(d?.type){window.__hybridMessages[d.type]=d; if(d.type==='status')window.__hybridStatusCounter++;} });`);
  await send({type:"bootstrap"});
  await waitFor(() => evaluate("!!window.__hybridMessages.browse && !!window.__hybridMessages.bootstrap"), "browse bootstrap/data");
  const boot = await evaluate("window.__hybridMessages.bootstrap");
  assert.equal(boot.primaryUi, "native"); assert.equal(boot.bundledRuntime, true);
  assert.ok(await evaluate("window.__hybridMessages.browse.launcher.presets.some(p=>p.id==='starrail-harpy-express')"));
  assert.equal(await evaluate("document.querySelectorAll('.rail,.titlebar,#native-view').length"), 0);
  assert.ok(await evaluate("document.querySelector('#pack-badge').textContent.includes('测试')"));
  console.log(`PASS: complementary Browse surface, SRE test entry, bundled WebView2 ${boot.runtimeVersion}`);
  await evaluate("document.querySelector('#search').value='中文搜索'; document.querySelector('#search').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true}))");
  assert.equal(await evaluate("document.querySelector('#search').value"), "中文搜索");
  await evaluate("document.querySelector('#search').value=''; document.querySelector('#search').dispatchEvent(new Event('input',{bubbles:true}))");
  console.log("PASS: IME Escape does not clear the existing search text");
  const capture = await call("Page.captureScreenshot", {format:"png"});
  await writeFile(join(output,"browse.png"), Buffer.from(capture.data,"base64"));
  await evaluate("document.querySelector('#search').value='no-such-pack-ux-test'; document.querySelector('#search').dispatchEvent(new Event('input',{bubbles:true}))");
  assert.equal(await evaluate("document.querySelector('#empty').hidden"), false);
  await evaluate("document.querySelector('#clear-search').click()");
  assert.equal(await evaluate("document.querySelector('#search').value"), "");
  console.log("PASS: search empty state and clear action");
  await evaluate("document.querySelector('#more').click(); document.querySelector('#goto-games').click()");
  await nativeReady(0,0);
  assert.equal((await status()).originalLaunchControls, true);
  await send({type:"testCaptureNative"});
  console.log("PASS: actual Browse return button reveals original native game page");

  for (const [page,number,sub] of [["settings",2,0],["download",1,1],["tools",3,3],["games",0,0]]) {
    await send({type:"testNavigate",page}); await nativeReady(number,sub);
    await send({type:"testCaptureNative"});
    console.log(`PASS: native sidebar ${page}`);
  }
  await send({type:"testNavigate",page:"versions"}); await nativeReady(5);
  await send({type:"testCaptureNative"});
  console.log("PASS: original PCL CE version list opens natively");
  await send({type:"testNavigate",page:"games"}); await nativeReady(0,0);
  const gameStatus = await status();
  if (!gameStatus.versionSettingsEnabled) {
    await send({type:"testNavigate",page:"versionSettings"});
    assert.equal((await status()).page,0);
    console.log("PASS: version settings safely disabled without a selected version");
  }
  // Catalogue drives both native navigation and test expectations; no duplicated enum mapping in production.
  const catalog = await readFile(new URL("../PCL.Core/UI/WebHost/LauncherPageCatalog.cs", import.meta.url),"utf8");
  const routes = [...catalog.matchAll(/new\("((?:download|setup|tools)\.[^"]+)"[^\n]+,\s*(Download|Setup|Tools),\s*(\d+),/g)]
    .map(match=>({id:match[1],page:{Download:1,Setup:2,Tools:3}[match[2]],sub:Number(match[3])}));
  assert.ok(routes.length>=30);
  for (const route of routes) {
    await send({type:"testNavigate",id:route.id}); await nativeReady(route.page,route.sub);
    const current = await status(); assert.ok(current.nativeLeft && current.nativeRight);
    console.log(`PASS: original native route ${route.id}`);
  }
  await send({type:"testNavigate",page:"browse"});
  await waitFor(async()=> (await status()).browserVisible,"return to Browse without destroying its host");
  console.log("Browser re-entry status:", await status());
  await waitFor(async()=> (await status()).browserKeyboardFocus,"keyboard focus restored on browser re-entry");
  console.log("PASS: browser keyboard focus restored on re-entry");
  await send({type:"testNavigate",page:"games"}); await nativeReady(0,0);
  await send({type:"testCaptureNative"});
  console.log(`Hybrid UI smoke passed: native-first, ${routes.length} original routes, native version list, browse/native round trips.`);
} finally { socket.close(); }
