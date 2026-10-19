// Offline DOM-shaped mocks only. No browser, HTTP server, model or input driver.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const root = new URL('../', import.meta.url);
const html = readFileSync(new URL('src/AutoJev.Local/index.html', root), 'utf8');
const selector = html.match(/<select id="cacheMode">([\s\S]*?)<\/select>/)[1];
assert.deepEqual([...selector.matchAll(/value="([^"]+)"/g)].map(m => m[1]), ['default', 'none', 'options', 'context']);
const elements = new Map([...html.matchAll(/\bid="([^"]+)"/g)].map(m => [m[1], {
  value: '', textContent: '', innerHTML: '', hidden: false,
  addEventListener() {}, classList: { toggle() {} },
}]));
elements.get('cacheMode').value = 'default';
const document = {
  getElementById(id) { assert.ok(elements.has(id), 'DOM id exists: ' + id); return elements.get(id); },
  querySelectorAll() { return []; },
};
let status = { status: 'unloaded', mode: 'cuda', cacheMode: 'context', prefixCacheEnabled: false, prefixCacheSeconds: 300, visionEnabled: true };
let appliedMode = 'context', cachedTokens = '0', calls = [], clipboard = '';
let beforeDashboard;
const context = vm.createContext({
  document, location: { origin: 'http://localhost:8765' }, performance,
  AbortController, setInterval() { return 1; }, clearInterval() {}, setTimeout() {},
  navigator: { clipboard: { async writeText(value) { clipboard = value; } } },
  confirm() { return true; },
  async fetch(url, init) {
    if (url === '/local/status') return { async json() { return status; } };
    if (url === '/local/dashboard-key') {
      beforeDashboard?.();
      return { ok: true, async json() { return { key: 'offline-test-key' }; } };
    }
    assert.equal(url, '/v1/systemone');
    calls.push(init);
    const body = JSON.parse(init.body);
    return {
      ok: true,
      headers: { get(name) { return ({ 'X-AutoJev-Cache-Mode': appliedMode, 'X-AutoJev-Cached-Tokens': cachedTokens, 'X-AutoJev-Checkpoint-Bytes': '1048576' })[name] ?? null; } },
      async json() { return { model: 'offline-test-model', usage: { input_tokens: 100 }, answers: Object.fromEntries(Object.keys(body.questions).map(id => [id, { type: 'noul', noul: 0.75 }])) }; },
    };
  },
});
vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1], context);
const evaluate = code => vm.runInContext(code, context);
const node = id => elements.get(id);
await evaluate('refreshStatus()');
assert.match(node('cacheStatus').textContent, /Server default: context/);
assert.match(node('requestHeaders').textContent, /No cache override header/);

for (const mode of ['default', 'none', 'options', 'context']) {
  node('cacheMode').value = mode;
  evaluate('changed()');
  const headers = JSON.parse(evaluate('JSON.stringify(cacheHeaders())'));
  assert.deepEqual(headers, mode === 'default' ? {} : { 'X-AutoJev-Cache-Mode': mode });
  assert.match(node('requestHeaders').textContent, mode === 'default' ? /No cache override/ : new RegExp('X-AutoJev-Cache-Mode: ' + mode));
  const body = JSON.parse(evaluate('JSON.stringify(request())'));
  assert.equal(Object.hasOwn(body, 'cacheMode'), false, 'mode is not in JSON body');
  assert.equal(Object.keys(body.questions).length, 2, 'one mode covers every question');
  await node('copyRequest').onclick();
  assert.deepEqual(JSON.parse(clipboard), body, 'copied JSON excludes headers');
  await node('run').onclick();
  const sent = calls.at(-1);
  assert.equal(sent.headers.Authorization, 'Bearer offline-test-key');
  assert.equal(sent.headers['Content-Type'], 'application/json');
  assert.equal(sent.headers['X-AutoJev-Cache-Mode'], mode === 'default' ? undefined : mode);
  assert.equal(sent.headers['X-AutoJev-Prefix-Cache'], undefined, 'no legacy header generated');
  assert.deepEqual(JSON.parse(sent.body), body);
  assert.match(node('metadata').textContent, /applied cache mode: context \| 0 cached tokens/, 'cold mode is not confused with a hit or selected value');
  assert.match(node('metadata').textContent, /1\.0 MiB cache/);
}

// Selection changes during dashboard-key fetch must not change this request.
node('cacheMode').value = 'context';
beforeDashboard = () => { node('cacheMode').value = 'none'; };
await node('run').onclick();
assert.equal(calls.at(-1).headers['X-AutoJev-Cache-Mode'], 'context');
beforeDashboard = undefined;

// Images still send the selected mode. Wrapper/native report the actual bypass.
node('cacheMode').value = 'context';
evaluate("images=[{uri:'data:image/png;base64,AQ=='}];visionEnabled=true;");
appliedMode = 'none';
await node('run').onclick();
assert.equal(calls.at(-1).headers['X-AutoJev-Cache-Mode'], 'context');
assert.equal(JSON.parse(calls.at(-1).body).images.length, 1);
assert.match(node('metadata').textContent, /applied cache mode: none/);
evaluate('images=[];');
appliedMode = null;
await node('run').onclick();
assert.match(node('metadata').textContent, /applied cache mode: unknown/, 'missing header must not claim the requested mode');

for (const enabled of [true, false]) {
  status = { ...status, cacheMode: undefined, prefixCacheEnabled: enabled };
  await evaluate('refreshStatus()');
  assert.match(node('cacheStatus').textContent, new RegExp('Server default: ' + (enabled ? 'options' : 'none')));
}
status = { ...status, cacheMode: 'none', prefixCacheEnabled: true };
await evaluate('refreshStatus()');
assert.match(node('cacheStatus').textContent, /Server default: none/, 'new status field takes precedence');
node('cacheMode').value = 'options';
evaluate('changed()');
assert.equal(node('resultStatus').textContent, 'Previous request', 'editing mode marks prior result');

// Verify production wiring without loading a model or touching the live API.
const bridge = readFileSync(new URL('src/AutoJev.Local/Program.cs', root), 'utf8');
assert.match(bridge, /request\.Headers\.Add\(CacheModePolicy\.Header, cacheMode\)/);
assert.match(bridge, /cacheMode = CacheModePolicy\.ForRequest\(cacheMode, body\["images"\] is JsonArray \{ Count: > 0 \}\)/);
assert.match(bridge, /psi\.Environment\["AUTOJEV_CACHE_MODE"\] = cfg\.EffectiveCacheMode/);
assert.match(bridge, /cacheMode = cfg\.EffectiveCacheMode, prefixCacheEnabled = cfg\.PrefixCacheEnabled/);
const tray = readFileSync(new URL('src/AutoJev.Local/TrayHost.cs', root), 'utf8');
assert.match(tray, /AccessibleName = "Default cache mode"/);
assert.match(tray, /next\.CacheMode = new\[\] \{ "none", "options", "context" \}/);
const config = JSON.parse(readFileSync(new URL('config/server.json', root), 'utf8'));
assert.equal(config.cacheMode, 'none');
assert.deepEqual(config.allowedOrigins, ['*']);
console.log('Playground DOM/request/response and wrapper wiring checks passed.');
