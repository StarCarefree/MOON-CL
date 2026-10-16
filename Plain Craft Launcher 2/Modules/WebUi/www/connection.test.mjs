// Connection startup regression: real page scripts, mocked WebView2 transport, no network.
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium} = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const browser = await chromium.launch({channel: 'msedge', headless: true});
try {
  for (const mode of ['reply', 'timeout', 'offline']) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.stack || error.message));
    await page.route(/^https?:\/\//, route => route.abort());
    if (mode !== 'offline') await page.addInitScript(() => {
      const listeners = [];
      window.__messages = [];
      window.__receive = data => listeners.forEach(listener => listener({data}));
      window.chrome = {webview: {
        postMessage: message => window.__messages.push(message),
        addEventListener: (_, listener) => listeners.push(listener)
      }};
    });
    await page.clock.install();
    await page.goto(new URL('./index.html', import.meta.url).href);
    assert.deepEqual(errors, [], `${mode}: page initialization must finish without script errors`);
    if (mode === 'offline') {
      assert.equal(await page.locator('body').getAttribute('data-link'), 'offline');
      assert.equal(await page.locator('#primary').isDisabled(), true);
    } else {
      assert.equal(await page.evaluate(() => window.__messages.filter(message => message.type === 'bootstrap').length), 1,
        'page must register the bridge listener and send the initial handshake');
      assert.equal(await page.locator('body').getAttribute('data-link'), 'connecting');
      if (mode === 'reply') {
        await page.evaluate(() => {
          window.__receive({type: 'bootstrap', language: 'zh'});
          window.__receive({type: 'browse', launcher: {presets: [], config: {}, task: {}}});
        });
        assert.equal(await page.locator('body').getAttribute('data-link'), 'live');
        assert.equal(await page.locator('#connection-text').textContent(), '已连接本地启动器');
        assert.ok(await page.evaluate(() => window.__messages.some(message => message.type === 'mcmod')),
          'connected page must request the resource catalog');
        await page.evaluate(() => {
          const request = window.__messages.findLast(message => message.type === 'mcmod' && message.operation === 'list');
          window.__receive({type: 'mcmodList', requestId: request.requestId, page: 1, totalPages: 1,
            items: [{id: 'mcmod-1426', wikiId: 1426, source: 'mcmod', name: '连接验证 · DEMO', minecraftVersions: ['1.21.1']}]});
        });
        assert.equal(await page.locator('#cards [data-pack-id="mcmod-1426"]').count(), 1);
        await page.locator('#type-server').click();
        assert.equal(await page.locator('.stage').isVisible(), false);
        await page.locator('#type-modpack').click();
        assert.equal(await page.locator('#connection-text').textContent(), '已连接本地启动器');
      } else {
        await page.clock.fastForward(9000);
        assert.equal(await page.locator('body').getAttribute('data-link'), 'lost', 'missing host reply must not leave the page connecting forever');
        assert.match(await page.locator('#error').textContent(), /没有连上启动器/);
      }
    }
    assert.deepEqual(errors, [], `${mode}: connection updates must not throw`);
    await page.close();
    console.log(`PASS resource catalog connection: ${mode}`);
  }
} finally {
  await browser.close();
}
