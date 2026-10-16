// Discovery redesign contracts. DEMO fixtures only; no external requests, installs or downloads.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import path from 'node:path';
import {mkdir} from 'node:fs/promises';
import {onlyExplorer} from './test-explorers.mjs';
const {chromium} = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const browser = await chromium.launch({channel: 'msedge', headless: true});
const errors = [];
try {
  const page = await browser.newPage({viewport: {width: 1280, height: 960}});
  page.on('pageerror', e => errors.push(e.message));
  await page.route(/^https?:\/\//, route => route.abort());
  await page.addInitScript(() => {
    const listeners = [];
    window.__messages = [];
    window.__receive = data => listeners.forEach(f => f({data}));
    window.chrome = {webview: {postMessage: m => window.__messages.push(m), addEventListener: (_, f) => listeners.push(f)}};
  });
  await page.goto(new URL('./index.html', import.meta.url).href);
  await page.clock.install();
  const send = data => page.evaluate(data => window.__receive(data), data);
  const messages = () => page.evaluate(() => window.__messages);
  const last = async (type, operation = 'list', source) => (await messages()).findLast(m => m.type === type && m.operation === operation && (!source || m.source === source));
  const card = id => page.locator(`#cards [data-pack-id="${id}"]`);
  const sre = {id: 'starrail-harpy-express', name: 'SRE · DEMO 本地预设', version: 'DEMO-1', minecraft: '1.21.1'};
  const snapshot = task => send({type: 'browse', launcher: {presets: [sre], selected: sre.id, config: {}, task: task || {}}});
  const versions = ['1.21.1', '1.16.5', '1.20.1', '1.18.2', '1.19.2', '1.17.1', '1.20.1'];
  const wiki = {id: 'mcmod-1426', wikiId: 1426, source: 'mcmod', name: '机械与远方 · DEMO', description: '从第一台机器到新的世界，在探索中建立属于你的自动化工厂。DEMO 测试数据。', minecraftVersions: versions, loaders: ['Forge'], authors: ['DEMO author']};
  const cf = {id: 'curseforge-123', projectId: '123', source: 'curseforge', name: '星海旅途 · DEMO', description: 'DEMO 平台项目简介', minecraftVersions: ['1.9', '1.10', '1.21.1'], loaders: ['Fabric']};
  assert.equal(await page.locator('#include-mcmod').isChecked(), true);
  assert.equal(await page.locator('.discover-controls #search').count(), 1);
  assert.equal(await page.locator('.discover-controls #filters').count(), 1);
  assert.equal(await page.locator('.discover-controls .explorer-picker').count(), 1);
  await snapshot();
  const mcRequest = await last('mcmod');
  const cfRequest = await last('platform', 'list', 'curseforge');
  await send({type: 'mcmodList', requestId: mcRequest.requestId, page: 1, totalPages: 2, items: [wiki, {...wiki, id: 'mcmod-1427', wikiId: 1427, name: '林间生活 · DEMO', minecraftVersions: ['1.20.1']}]});
  await send({type: 'platformList', source: 'curseforge', requestId: cfRequest.requestId, page: 1, totalPages: 2, items: [cf]});
  assert.equal(await page.locator('#pack-name').textContent(), wiki.name);
  assert.equal(await page.locator('#meta-mc').textContent(), '1.16.5–1.21.1');
  assert.equal(await card(wiki.id).locator('small').textContent(), 'MC 1.16.5–1.21.1 · Forge');
  assert.match(await card(wiki.id).locator('small').getAttribute('title'), /1.18.2/);
  assert.equal(await page.locator('#more-wrap').isVisible(), false);
  assert.equal(await page.locator('#primary').textContent(), '查看详情');
  await page.locator('#primary').click();
  assert.equal((await last('mcmod', 'detail')).id, wiki.wikiId);
  assert.equal(await page.locator('#detail-mc').textContent(), '1.16.5–1.21.1');
  assert.equal((await messages()).some(m => m.type === 'launcher' && ['primary', 'select'].includes(m.operation)), false);
  await page.locator('#detail-versions-wrap summary').click();
  assert.match(await page.locator('#detail-versions').textContent(), /1.17.1/);
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#primary').evaluate(n => n === document.activeElement), true);
  const summaries = await page.evaluate(() => [
    versionSummary({minecraftVersions: ['1.10', '1.9', '1.10']}),
    versionSummary({minecraftVersions: ['1.21.1']}),
    versionSummary({minecraftVersions: []}),
    versionSummary({minecraftVersions: ['1.20.1', '24w14a', '1.21-pre1']}),
    versionSummary({minecraftVersions: ['24w14a', '24w15a', '24w16a', '24w17a']})
  ]);
  assert.deepEqual(summaries.slice(0, 4), ['1.9–1.10', '1.21.1', '', '1.20.1 · 24w14a / 1.21-pre1']);
  assert.match(summaries[4], /4 个预览/);
  assert.equal(await page.locator('#filter-mc option[value="1.18.2"]').count(), 1);
  console.log('PASS default MCmod, unified controls, source-driven hero, safe detail action and compact version spans/full disclosure');

  for (const width of [360, 560, 840, 1000, 1280, 1600]) {
    await page.setViewportSize({width, height: 960});
    const overflows = await page.evaluate(() => {
      const bad = [];
      if (document.documentElement.scrollWidth > innerWidth + 1) bad.push('document');
      for (const n of document.querySelectorAll('.discover-controls, .search-entry, .filters, .card, .banner, .rec')) {
        if (!n.getClientRects().length) continue;
        const r = n.getBoundingClientRect();
        if (r.left < 0 || r.right > innerWidth + 1 || n.scrollWidth > n.clientWidth + 2) bad.push(n.className);
      }
      return bad;
    });
    assert.deepEqual(overflows, [], `layout at ${width}px`);
    if (process.env.DISCOVERY_SCREENSHOTS && [360, 1280].includes(width)) {
      await mkdir(process.env.DISCOVERY_SCREENSHOTS, {recursive: true});
      await page.screenshot({path: path.join(process.env.DISCOVERY_SCREENSHOTS, `discovery-${width}.png`), fullPage: true, animations: 'disabled'});
    }
  }
  await page.setViewportSize({width: 1280, height: 960});
  await page.locator('#include-mcmod').uncheck();
  assert.equal(await page.locator('#pack-name').textContent(), cf.name);
  assert.equal(await page.locator('#meta-mc').textContent(), '1.9–1.21.1');
  assert.equal(await card(wiki.id).count(), 0);
  await page.locator('#source-curseforge').uncheck();
  assert.equal(await page.locator('#pack-name').textContent(), sre.name);
  assert.equal(await page.locator('#more-wrap').isVisible(), true);
  await snapshot({busy: true, phase: 'DEMO downloading', progress: .42});
  assert.equal(await page.locator('#primary').isDisabled(), true);
  assert.equal(await page.locator('#task-progress').evaluate(n => n.value), 42);
  await snapshot();
  await page.locator('#source-mcl').uncheck();
  assert.equal(await page.locator('.stage').isVisible(), false);
  assert.equal(await page.locator('#empty').isVisible(), true);
  assert.match(await page.locator('#empty-text').textContent(), /尚未选择资源来源/);
  console.log('PASS source deselection changes hero, native locks/progress preserved, no-source guidance and 360–1600px layouts');

  await onlyExplorer(page, 'mcmod', false);
  await page.locator('#search').fill('没有匹配的名字');
  assert.equal(await page.locator('.stage').isVisible(), false);
  assert.equal(await page.locator('#empty').isVisible(), true);
  await page.locator('#reset-filters').click();
  assert.equal(await page.locator('#pack-name').textContent(), wiki.name);
  await page.locator('#filter-mc').selectOption('1.18.2');
  const filtered = await last('mcmod');
  assert.equal(filtered.minecraft, '1.18.2');
  assert.equal(await page.locator('.stage').isVisible(), false);
  await send({type: 'mcmodList', requestId: filtered.requestId, page: 1, totalPages: 1, items: [{...wiki, name: '精确版本筛选 · DEMO'}]});
  assert.equal(await page.locator('#pack-name').textContent(), '精确版本筛选 · DEMO');
  await page.locator('#type-world').click();
  assert.equal(await page.locator('.stage').isVisible(), false);
  assert.match(await page.locator('#sources-summary-message').textContent(), /此类型不可用/);
  assert.deepEqual(errors, []);
  console.log('PASS search/reset, exact-version request, no stale hero on filters/types, unsupported source and no browser errors');
} finally {
  await browser.close();
}
