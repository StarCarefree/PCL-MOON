// Node 22+; no npm packages. Starts its own Rust service and isolated headless Chromium.
// Usage: node scripts/test-admin-ui.mjs [path/to/pcl-moon-cloud]
// PCL_MOON_BROWSER may point to Chrome/Edge on other platforms. Output is under build/ui-smoke/.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = join(root, 'build', 'ui-smoke', String(Date.now()));
await mkdir(output, { recursive: true });
const token = randomBytes(32).toString('hex');
const bin = resolve(process.argv[2] || join(root, 'target', 'release', process.platform === 'win32' ? 'pcl-moon-cloud.exe' : 'pcl-moon-cloud'));
const browserPath = process.env.PCL_MOON_BROWSER || [
  join(process.env['ProgramFiles(x86)'] || '', 'Microsoft/Edge/Application/msedge.exe'),
  join(process.env.ProgramFiles || '', 'Google/Chrome/Application/chrome.exe'),
  '/usr/bin/chromium', '/usr/bin/google-chrome'
].find(existsSync);
assert.ok(browserPath, 'Set PCL_MOON_BROWSER to a Chromium browser executable');
assert.ok(existsSync(bin), 'Build release binary first (cargo build --release)');
async function freePort() {
  const server = createServer();
  await new Promise((accept) => server.listen(0, '127.0.0.1', accept));
  const port = server.address().port;
  await new Promise((accept) => server.close(accept));
  return port;
}
const publicPort = await freePort();
let adminPort = await freePort();
while (adminPort === publicPort) adminPort = await freePort();
const debugPort = await freePort();
const origin = `http://127.0.0.1:${adminPort}`;
const publicOrigin = `http://127.0.0.1:${publicPort}`;
const environment = { ...process.env, TEMP: output, TMP: output,
  PCL_MOON_ADMIN_TOKEN: token, PCL_MOON_DATA_DIR: join(output, 'data'),
  PCL_MOON_ADMIN_BIND: '127.0.0.1', PCL_MOON_ADMIN_PORT: String(adminPort),
  PCL_MOON_PUBLIC_BIND: '127.0.0.1', PCL_MOON_PUBLIC_PORT: String(publicPort) };
let app, browser, socket;
let appLog = '', browserLog = '';
const checks = [];
const check = (name) => { checks.push(name); console.log(`PASS: ${name}`); };
async function until(predicate, label) {
  for (let attempt = 0; attempt < 150; attempt++) {
    try { if (await predicate()) return; } catch { /* startup / navigation */ }
    await delay(100);
  }
  throw new Error(`Timeout: ${label}`);
}
async function adminApi(path, method = 'GET', payload, tag) {
  const response = await fetch(origin + path, { method,
    headers: { Authorization: `Bearer ${token}`, ...(payload ? { 'Content-Type': 'application/json' } : {}), ...(tag ? { 'If-Match': tag } : {}) },
    ...(payload ? { body: JSON.stringify(payload) } : {}) });
  assert.equal(response.status, 200);
  return { data: await response.json(), tag: response.headers.get('etag') };
}
try {
  app = spawn(bin, [], { env: environment, cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
  app.on('error', (error) => { appLog += error.message; });
  app.stdout.on('data', (chunk) => { appLog += chunk; });
  app.stderr.on('data', (chunk) => { appLog += chunk; });
  await until(async () => (await fetch(publicOrigin + '/healthz')).ok, 'PCL-MOON Cloud service startup');
  assert.equal((await fetch(publicOrigin + '/api/admin/packs')).status, 404);
  assert.equal((await fetch(origin + '/api/admin/packs')).status, 401);
  check('真实双端口启动、路由隔离和令牌校验');

  browser = spawn(browserPath, ['--headless=new', '--disable-gpu', '--disable-gpu-sandbox', '--disable-software-rasterizer', '--no-first-run', '--no-default-browser-check',
    `--remote-debugging-port=${debugPort}`, `--user-data-dir=${join(output, 'browser-profile')}`, 'about:blank'],
  { env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
  browser.on('error', (error) => { browserLog += error.message; });
  browser.stderr.on('data', (chunk) => { browserLog += chunk; });
  let target;
  await until(async () => {
    const list = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json();
    target = list.find((entry) => entry.type === 'page');
    return Boolean(target);
  }, 'headless browser startup');
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((accept, reject) => { socket.addEventListener('open', accept, { once: true }); socket.addEventListener('error', reject, { once: true }); });
  let serial = 0;
  const pending = new Map();
  const exceptions = [], apiRequests = [];
  socket.addEventListener('message', ({ data }) => {
    const message = JSON.parse(data);
    if (message.method === 'Runtime.exceptionThrown') exceptions.push(message.params.exceptionDetails.text);
    if (message.method === 'Network.requestWillBeSent' && message.params.request.url.includes('/api/admin/')) apiRequests.push(message.params.request.url);
    const item = pending.get(message.id);
    if (!item) return;
    pending.delete(message.id); clearTimeout(item.timer);
    if (message.error) item.reject(new Error(message.error.message)); else item.accept(message.result);
  });
  function call(method, params = {}) {
    return new Promise((accept, reject) => {
      const id = ++serial;
      const timer = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)); }, 30000);
      pending.set(id, { accept, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
    });
  }
  async function evaluate(expression) {
    const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  }
  async function click(id) { await evaluate(`document.getElementById(${JSON.stringify(id)}).click()`); }
  async function fillForm(values) {
    await evaluate(`(() => { const form = document.getElementById('pack-form'); const values = ${JSON.stringify(values)};
      for (const [key, value] of Object.entries(values)) { const input = form.elements.namedItem(key);
        if (input.type === 'checkbox') input.checked = value; else input.value = value;
        input.dispatchEvent(new Event('input', {bubbles:true})); } })()`);
  }
  await call('Runtime.enable'); await call('Network.enable'); await call('Page.enable');
  await call('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await call('Page.navigate', { url: origin + '/' });
  await until(() => evaluate("Boolean(document.getElementById('login-form') && document.readyState === 'complete')"), 'login screen');
  assert.equal(apiRequests.length, 0);
  await evaluate(`document.getElementById('token').value = ${JSON.stringify(token)}; document.getElementById('login-form').requestSubmit()`);
  await until(() => evaluate("!document.getElementById('app').hidden && !document.getElementById('add').disabled"), 'login');
  assert.equal(await evaluate("document.getElementById('public-port').textContent"), String(publicPort));
  check('网页加载不访问管理 API；登录后正确显示获取端口');

  await click('add');
  await fillForm({ id: 'ui-test', name: '<img src=x onerror=alert(1)> 月光包', version: '1.0.0', minecraft: '1.21.1',
    description: '浏览器实际联调：封面、备用下载与发布状态。', coverUrl: 'https://cdn.example.com/cover.webp',
    downloadUrl: 'https://cdn.example.com/pack.mrpack', backupUrls: 'https://backup.example.com/pack.mrpack', fileName: 'pack.mrpack', enabled: false });
  await evaluate("document.getElementById('pack-form').requestSubmit()");
  await until(() => evaluate("!document.getElementById('editor').open && document.getElementById('metric-total').textContent === '1'"), 'create draft');
  assert.equal((await (await fetch(publicOrigin + '/catalog.json')).json()).packs.length, 0);
  assert.equal(await evaluate("document.querySelectorAll('#pack-list img').length"), 0);
  check('网页新建、草稿隐藏、用户文本不会作为 HTML 执行');

  await evaluate("[...document.querySelectorAll('#pack-list button')].find(button => button.textContent === '编辑').click()");
  await fillForm({ enabled: true });
  await evaluate("document.getElementById('pack-form').requestSubmit()");
  await until(() => evaluate("!document.getElementById('editor').open && document.getElementById('metric-published').textContent === '1'"), 'publish');
  const catalog = await fetch(publicOrigin + '/catalog.json');
  const published = await catalog.json();
  assert.equal(published.packs[0].backupUrls.length, 1);
  assert.equal(published.packs[0].icon, 'https://cdn.example.com/cover.webp');
  const mirror = await fetch(publicOrigin + '/api/v1/packs/ui-test/download?mirror=1', { redirect: 'manual' });
  assert.equal(mirror.status, 302); assert.equal(mirror.headers.get('location'), 'https://backup.example.com/pack.mrpack');
  check('网页发布、封面兼容字段及真实备用地址 302');

  await evaluate("[...document.querySelectorAll('#pack-list button')].find(button => button.textContent === '编辑').click()");
  await fillForm({ name: '冲突后保留的名称' });
  const old = await adminApi('/api/admin/packs');
  await adminApi('/api/admin/packs/ui-test', 'PUT', { ...old.data.packs[0], name: '另一管理员的修改' }, old.tag);
  await evaluate("document.getElementById('pack-form').requestSubmit()");
  await until(() => evaluate("document.getElementById('editor').open && !document.getElementById('editor-notice').hidden && !document.getElementById('save').disabled"), '412 recovery');
  assert.equal(await evaluate("document.getElementById('pack-form').elements.namedItem('name').value"), '冲突后保留的名称');
  await evaluate("document.getElementById('pack-form').requestSubmit()");
  await until(() => evaluate("!document.getElementById('editor').open"), 'confirmed conflict retry');
  assert.equal((await adminApi('/api/admin/packs')).data.packs[0].name, '冲突后保留的名称');
  check('实际 412 编辑冲突保留表单，二次确认后保存');

  await writeFile(join(output, 'dashboard.png'), Buffer.from((await call('Page.captureScreenshot', { format: 'png' })).data, 'base64'));
  await call('Emulation.setDeviceMetricsOverride', { width: 360, height: 900, deviceScaleFactor: 1, mobile: true });
  assert.ok(await evaluate('document.documentElement.scrollWidth <= window.innerWidth'), 'Mobile page overflows');
  await writeFile(join(output, 'mobile.png'), Buffer.from((await call('Page.captureScreenshot', { format: 'png' })).data, 'base64'));
  check('360px 移动布局无横向溢出，保存桌面/手机截图');
  await evaluate("[...document.querySelectorAll('#pack-list button')].find(button => button.textContent === '删除').click()");
  await click('delete-confirm');
  await until(() => evaluate("document.getElementById('metric-total').textContent === '0' && !document.getElementById('delete-dialog').open"), 'delete');
  assert.equal((await (await fetch(publicOrigin + '/catalog.json')).json()).packs.length, 0);
  check('网页删除确认及公开目录同步移除');
  await click('disconnect');
  assert.equal(await evaluate("document.getElementById('token').value"), '');
  assert.equal(await evaluate('localStorage.length + sessionStorage.length'), 0);
  assert.equal(await evaluate('document.cookie'), '');
  assert.equal(await evaluate("document.getElementById('app').hidden"), true);
  assert.deepEqual(exceptions, []);
  check('断开清除令牌，无浏览器存储/Cookie/运行异常');
  await writeFile(join(output, 'results.json'), JSON.stringify({ checks, publicPort, adminPort, passed: true }, null, 2));
  console.log(`Result: ${checks.length} checks passed; ${output}`);
} catch (error) {
  await writeFile(join(output, 'failure.txt'), String(error.stack || error));
  throw error;
} finally {
  socket?.close();
  if (browser?.pid) {
    if (process.platform === 'win32') {
      await new Promise((accept) => { const killer = spawn('taskkill', ['/PID', String(browser.pid), '/T', '/F'], { stdio: 'ignore' }); killer.on('exit', accept); killer.on('error', accept); });
    } else browser.kill('SIGTERM');
  }
  if (app && app.exitCode === null) {
    app.kill('SIGTERM');
    if (process.platform === 'win32') {
      await Promise.race([
        new Promise((accept) => app.once('exit', accept)),
        delay(500).then(() => { if (app.exitCode === null) app.kill(); }),
      ]);
    }
  }
  await writeFile(join(output, 'service.log'), appLog);
  await writeFile(join(output, 'browser.log'), browserLog);
}
