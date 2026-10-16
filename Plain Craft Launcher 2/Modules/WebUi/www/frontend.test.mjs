import {onlyExplorer} from './test-explorers.mjs';
// Browser-only contract tests. All packs below are DEMO fixtures, never app data.
// Run: node frontend.test.mjs (requires playwright; PLAYWRIGHT_MODULE may name an
// external installation). BROWSER_CHANNEL defaults to installed Microsoft Edge.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { readFile } from 'node:fs/promises';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const browser = await chromium.launch({ channel: process.env.BROWSER_CHANNEL || 'msedge', headless: true });
const errors = [];
const url = new URL('./index.html', import.meta.url).href;
const sre = { id: 'starrail-harpy-express', name: 'SRE DEMO fixture', minecraft: '1.21.1', version: 'DEMO-4', loader: 'NeoForge', author: 'DEMO author', description: 'DEMO built-in pack', installed: false };
const native = { id: 'native-demo', name: 'Native DEMO fixture', minecraft: '1.19.2', loader: 'Fabric', author: 'Native DEMO author', version: 'DEMO-2' };
const remote = (id, extra = {}) => ({ id: `mcmod-${id}`, wikiId: id, source: 'mcmod', name: `DEMO encyclopedia pack ${id}`, englishName: 'DEMO fixture', description: 'DEMO plain description', minecraftVersions: ['1.20.1'], loaders: ['Forge'], categories: ['科技'], website: `https://www.mcmod.cn/modpack/${id}.html`, ...extra });
const snapshot = (extra = {}) => ({ type: 'browse', launcher: { presets: [native, sre], selected: sre.id, config: {}, task: {}, ...extra } });
async function setup(live = true, source = 'mcmod') {
  const page = await browser.newPage({ viewport: { width: 1280, height: 960 } });
  page.on('pageerror', error => errors.push(error.message));
  await page.route(/^https?:\/\//, route => route.abort()); // No external network in tests.
  if (live) await page.addInitScript(() => {
    const listeners = [];
    window.__messages = [];
    window.__receive = data => listeners.forEach(listener => listener({ data }));
    window.chrome = { webview: { postMessage: message => window.__messages.push(message), addEventListener: (name, listener) => listeners.push(listener) } };
  });
  await page.goto(url);
  // Legacy encyclopedia regressions explicitly select their source; default-platform behavior is tested separately.
  if (source === 'mcmod') await onlyExplorer(page, 'mcmod');
  return page;
}
const send = async (page, message) => {
  // Mock the host echo, not a frontend fallback: explicit requestId is preserved
  // for race/uncorrelated tests; ordinary replies echo their originating request.
  if ((message.type === 'mcmodList' || (message.type === 'mcmodError' && message.operation === 'list')) && !Object.hasOwn(message, 'requestId')) {
    const request = (await messages(page)).findLast(m => m.type === 'mcmod' && m.operation === 'list' && (message.page == null || m.page === message.page));
    message = { ...message, requestId: request?.requestId };
  }
  return page.evaluate(message => window.__receive(message), message);
};
function listPayload(message) {
  const { requestId, ...payload } = message;
  assert.equal(typeof requestId, 'string');
  assert.ok(requestId.length > 0);
  return payload;
}
const messages = page => page.evaluate(() => window.__messages);
const card = (page, id) => page.locator(`#cards [data-pack-id="${id}"]`);
async function checkLayout(page) {
  const failures = await page.evaluate(() => {
    const failures = [];
    if (document.documentElement.scrollWidth > innerWidth + 1) failures.push('document horizontal overflow');
    for (const node of document.querySelectorAll('.toolbar, .filters, .card, .banner, dialog[open], .detail-heading, .detail-meta')) {
      const rect = node.getBoundingClientRect();
      if (!rect.width || !rect.height) continue;
      if (node.scrollWidth > node.clientWidth + 2) failures.push(`${node.className}: horizontal overflow`);
      if (rect.left < -1 || rect.right > innerWidth + 1) failures.push(`${node.className}: outside viewport`);
    }
    return failures;
  });
  assert.deepEqual(failures, []);
}
try {
  const offline = await setup(false);
  assert.equal(await card(offline, sre.id).count(), 1);
  assert.equal(await offline.locator('#primary').isDisabled(), true);
  assert.equal(await offline.locator('#meta-ver').textContent(), '未提供');
  await card(offline, sre.id).click();
  assert.equal(await offline.locator('#pack-detail').evaluate(node => node.open), true);
  assert.equal(await offline.locator('#detail-authors').textContent(), '未提供');
  await offline.keyboard.press('Escape');
  await checkLayout(offline);
  await offline.close();
  console.log('PASS offline preview: unknown metadata, no fake install/download');

  const page = await setup();
  assert.deepEqual(await messages(page), [{ type: 'bootstrap' }]);
  await send(page, { type: 'bootstrap', language: 'zh' });
  assert.equal((await messages(page)).filter(m => m.type === 'mcmod').length, 0);
  await send(page, snapshot());
  assert.deepEqual(listPayload((await messages(page)).at(-1)), { type: 'mcmod', operation: 'list', page: 1 });
  assert.equal(await page.locator('#mcmod-status').getAttribute('data-state'), 'loading');
  await send(page, snapshot());
  assert.equal((await messages(page)).filter(m => m.type === 'mcmod').length, 1);
  await send(page, { type: 'mcmodList', page: 1, totalPages: 700, items: [remote(1426), remote(1427, { loaders: ['Fabric'], categories: ['冒险'], minecraftVersions: ['1.19.2'] })] });
  assert.equal(await page.locator('#cards > button').first().getAttribute('data-pack-id'), sre.id);
  assert.match(await page.locator('#page-label').textContent(), /500/);
  console.log('PASS live-only bounded initial page; no duplicate polling requests; SRE pinned');

  assert.deepEqual(await page.locator('#filter-loader option').evaluateAll(nodes => nodes.slice(1).map(n => n.value)), ['Forge', 'Fabric', 'Quilt', 'NeoForge', 'LiteLoader', 'Rift', '数据包', '行为包']);
  assert.deepEqual(await page.locator('#filter-category option').evaluateAll(nodes => nodes.slice(1).map(n => [n.value, n.textContent])), ['科技','魔法','冒险','建筑','地图','任务','硬核','休闲','大型','轻量','剧情','水槽','空岛','PvP','国创'].map((name, i) => [String(i + 1), name]));
  for (const version of ['1.21.1', '1.20.1', '1.19.2', '1.18.2', '1.16.5', '1.12.2']) assert.equal(await page.locator(`#filter-mc option[value="${version}"]`).count(), 1);
  await page.locator('#filter-loader').selectOption('Forge');
  assert.equal(await page.locator('#cards > button').count(), 1); // old filter results cleared
  assert.deepEqual(listPayload((await messages(page)).at(-1)), { type: 'mcmod', operation: 'list', page: 1, loader: 'Forge' });
  await page.locator('#filter-mc').selectOption('1.20.1');
  await page.locator('#filter-category').selectOption('1');
  assert.deepEqual(listPayload((await messages(page)).at(-1)), { type: 'mcmod', operation: 'list', page: 1, minecraft: '1.20.1', loader: 'Forge', category: 1 });
  // Real list records have no structured metadata. The server has filtered them.
  await send(page, { type: 'mcmodList', page: 1, totalPages: 2, items: [remote(1426, { minecraftVersions: [], loaders: [], categories: [] })] });
  assert.equal(await card(page, 'mcmod-1426').count(), 1);
  await page.locator('#search').fill('no-demo-match');
  assert.equal(await page.locator('#cards > button').count(), 1);
  assert.equal(await page.locator('#empty').isVisible(), true);
  await page.locator('#search').evaluate(node => node.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', isComposing: true, bubbles: true })));
  assert.equal(await page.locator('#search').inputValue(), 'no-demo-match');
  await page.locator('#search').evaluate(node => node.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 229, bubbles: true })));
  assert.equal(await page.locator('#search').inputValue(), 'no-demo-match');
  await page.locator('#reset-filters').click();
  console.log('PASS text/version/loader/category filters, empty/reset, IME Escape guards');

  await card(page, 'mcmod-1426').focus();
  await page.keyboard.press('Enter');
  assert.deepEqual((await messages(page)).at(-1), { type: 'mcmod', operation: 'detail', id: 1426 });
  assert.equal(await page.locator('#detail-download').isVisible(), false);
  assert.equal(await page.locator('#detail-native').isVisible(), false);
  await send(page, snapshot({ selected: native.id }));
  assert.match(await page.locator('#detail-name').textContent(), /1426/);
  assert.equal(await card(page, 'mcmod-1426').getAttribute('aria-pressed'), 'true');
  await send(page, { type: 'mcmodError', operation: 'detail', id: 1426, message: 'DEMO detail failure' });
  await page.locator('#detail-retry').click();
  await send(page, { type: 'mcmodDetail', item: remote(1426, { version: 'DEMO actual release', authors: ['DEMO writer'], curseForgeUrl: 'https://www.curseforge.com/minecraft/modpacks/demo-fixture' }) });
  assert.equal(await page.locator('#detail-version').textContent(), 'DEMO actual release');
  assert.equal(await page.locator('#detail-authors').textContent(), 'DEMO writer');
  assert.equal(await page.locator('#detail-mc').textContent(), '1.20.1');
  assert.equal(await page.locator('#detail-loader').textContent(), 'Forge');
  await page.locator('#detail-download').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'mcmod', operation: 'download', id: 1426 });
  await send(page, { type: 'mcmodError', operation: 'download', id: 1426, message: 'DEMO download failure' });
  await page.locator('#download-retry').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'mcmod', operation: 'download', id: 1426 });
  await page.keyboard.press('Escape');
  assert.equal(await card(page, 'mcmod-1426').evaluate(node => node === document.activeElement), true);
  console.log('PASS keyboard modal, live metadata, host-poll isolation, retry, CurseForge hand-off');

  await card(page, 'mcmod-1427').click();
  await page.locator('#close-detail').click();
  await card(page, 'mcmod-1426').click();
  await send(page, { type: 'mcmodDetail', item: remote(1427, { name: '<img src=x onerror=alert(1)> DEMO', description: '<script>DEMO unsafe text</script>', authors: ['DEMO manual author'], links: [{ name: 'DEMO manual download', url: 'https://example.org/demo.zip' }, { name: 'DEMO invalid', url: 'javascript:alert(1)' }] }) });
  assert.match(await page.locator('#detail-name').textContent(), /1426/);
  await page.locator('#close-detail').click();
  const beforeCache = (await messages(page)).filter(m => m.operation === 'detail').length;
  await card(page, 'mcmod-1427').click();
  assert.equal((await messages(page)).filter(m => m.operation === 'detail').length, beforeCache);
  assert.match(await page.locator('#detail-name').textContent(), /<img/);
  assert.equal(await page.locator('#detail-name img, #detail-description script').count(), 0);
  assert.equal(await page.locator('#detail-version').textContent(), '未提供');
  assert.equal(await page.locator('#detail-download').isVisible(), false);
  assert.equal(await page.locator('#detail-links button').count(), 1);
  await page.locator('#detail-links button').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'openExternal', url: 'https://example.org/demo.zip' });
  await page.locator('#detail-website').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'openExternal', url: 'https://www.mcmod.cn/modpack/1427.html' });
  await page.locator('#detail-import').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'launcher', operation: 'import' });
  assert.equal((await messages(page)).filter(m => m.type === 'launcher' && m.operation === 'select' && /^mcmod-/.test(m.id)).length, 0);
  for (const width of [1280, 840, 560, 360]) {
    await page.setViewportSize({ width, height: 850 });
    await checkLayout(page);
  }
  await page.locator('#close-detail').click();
  await page.setViewportSize({ width: 1280, height: 960 });
  console.log('PASS stale detail caching, safe text/URLs, manual sources/import, 360–1280px detail layout');

  await page.locator('#next-page').click(); // request 2, retaining page 1
  assert.deepEqual(listPayload((await messages(page)).at(-1)), { type: 'mcmod', operation: 'list', page: 2 });
  assert.equal(await card(page, 'mcmod-1426').count(), 1);
  await page.locator('#next-page').click(); // request 3 before 2 finishes
  await send(page, { type: 'mcmodList', page: 2, totalPages: 500, items: [remote(2222)] });
  assert.match(await page.locator('#page-label').textContent(), /第 3 页/);
  assert.equal(await card(page, 'mcmod-2222').count(), 0);
  await send(page, { type: 'mcmodError', operation: 'list', page: 3, message: 'DEMO list failure' });
  assert.equal(await page.locator('#mcmod-retry').isVisible(), true);
  assert.equal(await card(page, 'mcmod-1426').count(), 1);
  await page.locator('#mcmod-retry').click();
  await send(page, { type: 'mcmodList', page: 3, totalPages: 500, items: [] });
  assert.match(await page.locator('#mcmod-message').textContent(), /暂无/);
  await page.locator('#previous-page').click(); // cached 2
  assert.equal(await card(page, 'mcmod-2222').count(), 1);
  const beforeHide = (await messages(page)).length;
  await page.locator('#include-mcmod').uncheck();
  await send(page, snapshot());
  await page.evaluate(() => { requestPage(4); requestDetail({ wikiId: 5555 }); });
  assert.equal((await messages(page)).length, beforeHide);
  assert.equal(await card(page, 'mcmod-2222').count(), 0);
  await page.locator('#include-mcmod').check();
  assert.equal((await messages(page)).length, beforeHide);
  await page.evaluate(() => { requestPage(0); requestPage(501); });
  assert.equal((await messages(page)).length, beforeHide);
  console.log('PASS page races/cache, retained error data, retry/empty, source hide, hard page bounds');

  // Existing launcher operations and services keep their original messages.
  await onlyExplorer(page, 'mcl'); // Native mutation checks must exercise the native hero, not a platform detail button.
  await send(page, snapshot());
  await page.locator('#primary').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'launcher', operation: 'primary' });
  await send(page, snapshot({ task: { phase: 'DEMO host completed' } }));
  await page.locator('#more').click();
  await page.locator('#install-quark').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'launcher', operation: 'installQuark' });
  await send(page, snapshot({ task: { phase: 'DEMO quark host completed' } }));
  await page.locator('#more').click();
  await page.locator('#check-updates').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'launcher', operation: 'checkUpdates' });
  await send(page, snapshot({ task: { phase: 'DEMO check host completed' } }));
  await page.locator('#more').click();
  await page.locator('#goto-games').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'native', page: 'games' });
  await card(page, native.id).click();
  assert.equal(await page.locator('#detail-authors').textContent(), native.author);
  assert.equal(await page.locator('#detail-loader').textContent(), native.loader);
  assert.deepEqual((await messages(page)).at(-1), { type: 'launcher', operation: 'select', id: native.id });
  await page.locator('#close-detail').click();
  await send(page, snapshot());
  await page.locator('#open-services').click();
  await page.locator('#catalog-url').fill('https://example.org/demo-index.json\nhttps://example.org/demo-backup.json');
  await page.locator('#quark-url').fill('http://127.0.0.1:5000/');
  await page.locator('#save-services').click();
  const configured = (await messages(page)).at(-1);
  assert.equal(configured.operation, 'configure');
  assert.deepEqual(configured.catalogUrls, ['https://example.org/demo-index.json', 'https://example.org/demo-backup.json']);
  assert.equal(configured.quarkServiceUrl, 'http://127.0.0.1:5000/');
  await send(page, snapshot({ config: { ...configured } }));
  assert.match(await page.locator('#config-status').textContent(), /确认/);
  await page.locator('#close-services').click();
  console.log('PASS native primary/installQuark/checkUpdates/select/games/configure and metadata');

  await send(page, snapshot({ task: { busy: true, phase: 'DEMO downloading', progress: 0.42 } }));
  assert.equal(await page.locator('#primary').isDisabled(), true);
  assert.equal(await page.locator('#task-progress').evaluate(node => node.value), 42);
  const beforeBusyBrowse = (await messages(page)).length;
  await card(page, native.id).click();
  assert.equal(await page.locator('#detail-native').isDisabled(), true);
  assert.equal(await page.locator('#detail-import').isDisabled(), true);
  assert.equal((await messages(page)).length, beforeBusyBrowse);
  await page.locator('#close-detail').click();
  await page.locator('#cancel').click();
  assert.deepEqual((await messages(page)).at(-1), { type: 'launcher', operation: 'cancel' });
  await send(page, snapshot({ task: { gameRunning: true } }));
  assert.equal(await page.locator('#primary').isDisabled(), true);
  await send(page, snapshot());
  await page.locator('#include-mcmod').check(); // Restore the encyclopedia for the refresh/retained-page regression.
  await page.locator('#refresh').click();
  const refreshMessages = (await messages(page)).slice(-2);
  assert.deepEqual(refreshMessages[0], { type: 'launcher', operation: 'refresh' });
  assert.deepEqual(listPayload(refreshMessages[1]), { type: 'mcmod', operation: 'list', page: 2 });
  await send(page, { type: 'mcmodError', operation: 'list', message: 'DEMO refresh failure without page' });
  assert.match(await page.locator('#mcmod-message').textContent(), /DEMO refresh failure/);
  assert.equal(await card(page, 'mcmod-2222').count(), 1);
  await send(page, snapshot({ task: { phase: 'DEMO refresh done' } }));
  console.log('PASS native busy/progress/cancel/game-running, browsing during tasks, refresh retention');

  const prehidden = await setup();
  await prehidden.locator('#include-mcmod').uncheck();
  await send(prehidden, snapshot());
  assert.deepEqual(await messages(prehidden), [{ type: 'bootstrap' }]);
  await prehidden.locator('#include-mcmod').check();
  await prehidden.locator('#include-mcmod').uncheck();
  await send(prehidden, { type: 'mcmodList', page: 1, totalPages: 1, items: [remote(7777)] });
  assert.equal(await card(prehidden, 'mcmod-7777').count(), 0);
  await prehidden.locator('#include-mcmod').check();
  assert.equal(await card(prehidden, 'mcmod-7777').count(), 1);
  assert.equal((await messages(prehidden)).length, 2);
  await prehidden.close();

  const filtered = await setup();
  await send(filtered, snapshot());
  await send(filtered, { type: 'mcmodList', page: 1, totalPages: 9, items: [remote(8000)] });
  await filtered.locator('#next-page').click();
  await filtered.locator('#filter-loader').selectOption('Forge');
  const forgeRequest = (await messages(filtered)).at(-1);
  assert.equal(forgeRequest.page, 1); // filter changes reset paging
  assert.equal(await card(filtered, 'mcmod-8000').count(), 0);
  await filtered.locator('#filter-loader').selectOption('Fabric');
  const fabricRequest = (await messages(filtered)).at(-1);
  await send(filtered, { type: 'mcmodList', page: 1, totalPages: 4, requestId: forgeRequest.requestId, items: [remote(8001)] });
  assert.equal(await card(filtered, 'mcmod-8001').count(), 0); // same page, other filter
  await send(filtered, { type: 'mcmodError', operation: 'list', page: 1, requestId: forgeRequest.requestId, message: 'DEMO stale error' });
  assert.equal(await filtered.locator('#mcmod-status').getAttribute('data-state'), 'loading');
  await send(filtered, { type: 'mcmodList', page: 1, requestId: '', totalPages: 1, items: [remote(8999)] });
  await send(filtered, { type: 'mcmodError', operation: 'list', requestId: '', message: 'DEMO uncorrelated error' });
  assert.equal(await filtered.locator('#mcmod-status').getAttribute('data-state'), 'loading');
  assert.equal(await card(filtered, 'mcmod-8999').count(), 0);
  await filtered.evaluate(() => requestPage(1, true));
  const retryRequest = (await messages(filtered)).at(-1);
  assert.notEqual(retryRequest.requestId, fabricRequest.requestId);
  await send(filtered, { type: 'mcmodList', page: 1, totalPages: 9, requestId: fabricRequest.requestId, items: [remote(8002)] });
  await send(filtered, { type: 'mcmodError', operation: 'list', page: 1, requestId: fabricRequest.requestId, message: 'DEMO superseded failure' });
  assert.equal(await card(filtered, 'mcmod-8002').count(), 0);
  assert.equal(await filtered.locator('#mcmod-status').getAttribute('data-state'), 'loading');
  await send(filtered, { type: 'mcmodError', operation: 'list', page: 1, requestId: retryRequest.requestId, message: 'DEMO current failure' });
  assert.match(await filtered.locator('#mcmod-message').textContent(), /DEMO current failure/);
  await filtered.locator('#mcmod-retry').click();
  const unknown = remote(8003, { minecraftVersions: [], loaders: [], categories: [] });
  await send(filtered, { type: 'mcmodList', page: 1, totalPages: 3, items: [unknown] });
  assert.equal(await card(filtered, unknown.id).count(), 1);
  assert.equal(await card(filtered, native.id).count(), 1); // local Fabric match
  await filtered.locator('#next-page').click();
  assert.deepEqual(listPayload((await messages(filtered)).at(-1)), { type: 'mcmod', operation: 'list', page: 2, loader: 'Fabric' });
  assert.match(await filtered.locator('#mcmod-message').textContent(), /相同筛选的第 1 页/);
  await filtered.locator('#filter-mc').selectOption('1.12.2');
  assert.equal(await card(filtered, native.id).count(), 0); // local mismatch
  assert.equal(await card(filtered, sre.id).count(), 1);
  assert.equal(await card(filtered, unknown.id).count(), 0);
  assert.equal((await messages(filtered)).at(-1).page, 1);
  await filtered.locator('#include-mcmod').uncheck();
  const hiddenCount = (await messages(filtered)).length;
  assert.equal(await filtered.locator('#filter-category').isDisabled(), true);
  await send(filtered, snapshot());
  assert.equal((await messages(filtered)).length, hiddenCount);
  await filtered.locator('#include-mcmod').check();
  await filtered.locator('#filter-category').selectOption('15');
  assert.deepEqual(listPayload((await messages(filtered)).at(-1)), { type: 'mcmod', operation: 'list', page: 1, loader: 'Fabric', minecraft: '1.12.2', category: 15 });
  await send(filtered, { type: 'mcmodList', page: 1, totalPages: 2, items: [unknown] });
  assert.equal(await card(filtered, unknown.id).count(), 1);
  const beforeCachedToggle = (await messages(filtered)).length;
  await filtered.locator('#include-mcmod').uncheck();
  await filtered.locator('#include-mcmod').check();
  await filtered.locator('#filter-category').selectOption('15');
  assert.equal((await messages(filtered)).length, beforeCachedToggle);
  assert.equal(await card(filtered, unknown.id).count(), 1);
  const beforeText = (await messages(filtered)).length;
  await filtered.locator('#search').fill('no-demo-match');
  assert.equal(await card(filtered, unknown.id).count(), 0);
  assert.equal((await messages(filtered)).length, beforeText);
  await filtered.locator('#reset-filters').click();
  assert.equal(await filtered.locator('#search').inputValue(), '');
  assert.equal(await card(filtered, 'mcmod-8000').count(), 1);
  assert.match(await filtered.locator('#page-label').textContent(), /第 1 页/);
  assert.equal((await messages(filtered)).length, beforeText); // cached unfiltered page
  await filtered.locator('#filter-loader').selectOption('Forge');
  assert.equal(await card(filtered, 'mcmod-8001').count(), 1); // cached late other-filter reply
  assert.equal((await messages(filtered)).length, beforeText);
  await filtered.close();
  console.log('PASS whole-site filter contract, metadata-free results, same-page/out-of-order ids, stale errors, keyed cache, filtered pagination/reenable/reset');

  if (process.env.MCMOD_LIVE_FIXTURE) {
    const fixture = JSON.parse(await readFile(process.env.MCMOD_LIVE_FIXTURE, 'utf8'));
    const actual = await setup();
    await send(actual, snapshot());
    await send(actual, { type: 'mcmodList', ...fixture.page });
    assert.equal(await actual.locator('#cards > button').count(), fixture.page.items.length + 2);
    for (const width of [1280, 840, 360]) {
      await actual.setViewportSize({ width, height: 900 });
      await checkLayout(actual);
    }
    await actual.setViewportSize({ width: 1280, height: 960 });
    if (process.env.UI_SCREENSHOT_DIR) await actual.screenshot({ path: path.join(process.env.UI_SCREENSHOT_DIR, 'mcmod-catalog.png'), fullPage: false });
    await card(actual, fixture.detail.id).click();
    await send(actual, { type: 'mcmodDetail', item: fixture.detail });
    assert.equal(await actual.locator('#detail-version').textContent(), fixture.detail.version || '未提供');
    assert.equal(await actual.locator('#detail-mc').textContent(), fixture.detail.minecraftVersions.join(' / ') || '未提供');
    assert.equal(await actual.locator('#detail-introduction').evaluate(node => node.open), false);
    assert.equal(await actual.locator('#detail-downloads').evaluate(node => node.getBoundingClientRect().top < innerHeight), true);
    if (process.env.UI_SCREENSHOT_DIR) await actual.screenshot({ path: path.join(process.env.UI_SCREENSHOT_DIR, 'mcmod-detail.png'), fullPage: false });
    await actual.locator('#detail-introduction summary').focus();
    await actual.keyboard.press('Enter');
    assert.equal(await actual.locator('#detail-description').textContent(), fixture.detail.description);
    for (const width of [1280, 840, 360]) {
      await actual.setViewportSize({ width, height: 900 });
      await checkLayout(actual);
    }
    await actual.close();
    console.log('PASS real parsed MC百科 fixture: 32 metadata-free records and long detail layout');
  }

  await page.clock.install();
  await page.evaluate(() => requestPage(4));
  await page.clock.fastForward(35001);
  assert.equal(await page.locator('#mcmod-status').getAttribute('data-state'), 'loading');
  await page.clock.fastForward(10000);
  assert.match(await page.locator('#mcmod-message').textContent(), /超时/);
  await page.locator('#mcmod-retry').click();
  const stress = remote(4444, { name: 'DEMO_LongPack_' + 'x'.repeat(200), description: 'DEMO long text '.repeat(200), authors: ['DEMO_' + 'a'.repeat(200)], minecraftVersions: Array.from({ length: 24 }, (_, i) => `DEMO-1.${i}`) });
  await send(page, { type: 'mcmodList', page: 4, totalPages: 4, items: [stress] });
  await card(page, 'mcmod-4444').click();
  await page.evaluate(() => document.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true })));
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#pack-detail').evaluate(node => node.open), true);
  await page.evaluate(() => document.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true })));
  await page.clock.fastForward(45001);
  assert.match(await page.locator('#detail-message').textContent(), /超时/);
  await page.locator('#detail-retry').click();
  await send(page, { type: 'mcmodDetail', item: stress });
  assert.equal(await page.locator('#detail-introduction').evaluate(node => node.open), false);
  assert.equal(await page.locator('#detail-mc').evaluate(node => !!(node.compareDocumentPosition(document.getElementById('detail-description')) & Node.DOCUMENT_POSITION_FOLLOWING)), true);
  await page.locator('#detail-introduction summary').click();
  assert.equal(await page.locator('#detail-description').isVisible(), true);
  for (const width of [1280, 840, 360]) {
    await page.setViewportSize({ width, height: 850 });
    await checkLayout(page);
  }
  await page.locator('#close-detail').click();
  await page.waitForFunction(() => catalog.detailId === '');
  await card(page, sre.id).focus();
  await page.keyboard.press('ArrowRight');
  assert.equal(await page.locator('#cards button[tabindex="0"]').count(), 1);
  assert.equal(await card(page, native.id).evaluate(node => node === document.activeElement), true);
  console.log('PASS hidden-before-live/in-flight source toggle, list/detail timeout/retry, IME modal guard, long-content reflow and roving focus');

  await page.emulateMedia({ reducedMotion: 'reduce' });
  assert.equal(await card(page, sre.id).evaluate(node => getComputedStyle(node).animationName), 'none');
  await page.locator('#search').focus();
  await page.keyboard.press('Tab');
  assert.equal(await page.evaluate(() => getComputedStyle(document.activeElement).outlineStyle), 'solid');
  for (const width of [1600, 1024, 840, 560, 360]) {
    await page.setViewportSize({ width, height: 900 });
    await checkLayout(page);
  }
  if (process.env.UI_SCREENSHOT_DIR) {
    await page.setViewportSize({ width: 1280, height: 960 });
    await page.screenshot({ path: path.join(process.env.UI_SCREENSHOT_DIR, 'mooncl-catalog.png'), fullPage: true });
  }
  {
    const quarkPage = await setup();
    await send(quarkPage, snapshot());
    const share = 'https://pan.quark.cn/s/ff96d10800cd?pwd=ab12';
    await send(quarkPage, { type: 'mcmodList', page: 1, totalPages: 1, items: [remote(1500)] });
    await card(quarkPage, 'mcmod-1500').click();
    await send(quarkPage, { type: 'mcmodDetail', item: remote(1500, { links: [{ name: '夸克网盘', url: share }, { name: 'DEMO manual download', url: 'https://example.org/demo.zip' }, { name: 'DEMO evil quark', url: 'https://pan.quark.cn.evil/s/ff96d10800cd' }] }) });
    const buttons = quarkPage.locator('#detail-links button');
    assert.equal(await buttons.count(), 3);
    await buttons.nth(1).click();
    assert.deepEqual((await messages(quarkPage)).at(-1), { type: 'openExternal', url: 'https://example.org/demo.zip' });
    await buttons.nth(2).click();
    assert.deepEqual((await messages(quarkPage)).at(-1), { type: 'openExternal', url: 'https://pan.quark.cn.evil/s/ff96d10800cd' });
    const before = (await messages(quarkPage)).length;
    await buttons.nth(0).click();
    assert.equal((await messages(quarkPage)).length, before);
    assert.equal(await quarkPage.locator('#quark-choice').evaluate(node => node.open), true);
    await quarkPage.locator('#quark-choice-cancel').click();
    assert.equal((await messages(quarkPage)).length, before);
    await buttons.nth(0).click();
    await quarkPage.locator('#quark-jump').click();
    assert.deepEqual((await messages(quarkPage)).at(-1), { type: 'openExternal', url: share });
    await buttons.nth(0).click();
    await quarkPage.locator('#quark-direct').click();
    const listed = (await messages(quarkPage)).at(-1);
    assert.equal(listed.type, 'mcmod');
    assert.equal(listed.operation, 'quarkList');
    assert.equal(listed.id, 1500);
    assert.equal(listed.shareId, 'ff96d10800cd');
    assert.equal(listed.path, '');
    assert.equal(listed.resourceType, 'modpack');
    assert.equal(JSON.stringify(listed).includes('ab12'), false);
    assert.equal(JSON.stringify(listed).includes('pan.quark.cn'), false);
    assert.equal(await quarkPage.locator('#quark-download').evaluate(node => node.open), true);
    assert.match(await quarkPage.locator('#quark-download-title').textContent(), /选择版本/);
    await send(quarkPage, { type: 'quarkVersions', id: 1500, shareId: 'ff96d10800cd', path: '', truncated: false, requestId: listed.requestId, entries: [{ name: '1.20.1', path: '1.20.1', directory: true, size: 0 }, { name: 'pack.mrpack', path: 'pack.mrpack', directory: false, size: 1048576 }, { name: 'notes.txt', path: 'notes.txt', directory: false, size: 12 }] });
    assert.equal(await quarkPage.locator('#quark-versions button').count(), 2);
    await quarkPage.locator('#quark-versions button').nth(0).click();
    const nested = (await messages(quarkPage)).at(-1);
    assert.equal(nested.operation, 'quarkList');
    assert.equal(nested.path, '1.20.1');
    assert.equal(JSON.stringify(nested).includes('ab12'), false);
    await send(quarkPage, { type: 'quarkVersions', id: 1500, shareId: 'ff96d10800cd', path: '1.20.1', requestId: nested.requestId, entries: [{ name: 'demo.mrpack', path: '1.20.1/demo.mrpack', directory: false, size: 2048 }] });
    await quarkPage.locator('#quark-versions button').click();
    const picked = (await messages(quarkPage)).at(-1);
    assert.equal(picked.operation, 'quarkDownload');
    assert.equal(picked.path, '1.20.1/demo.mrpack');
    assert.equal(JSON.stringify(picked).includes('pwd'), false);
    assert.equal(JSON.stringify(picked).includes('pan.quark.cn'), false);
    await quarkPage.locator('#close-quark-download').click();
    await quarkPage.close();
    console.log('PASS quark source chooses jump or version page without posting the share URL');
  }
  assert.deepEqual(errors, []);
  console.log('PASS responsive catalog, visible keyboard focus, reduced motion; no page errors');
  await page.close();
  console.log(`All frontend browser tests passed (${fileURLToPath(url)}).`);
} finally {
  await browser.close();
}
