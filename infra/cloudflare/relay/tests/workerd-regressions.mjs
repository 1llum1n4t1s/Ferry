// 実 workerd / D1 / DeviceDO / WebSocket を通すローカル回帰。製品に障害注入経路は追加しない。
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile, writeFile, mkdir, lstat } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { resolve, dirname, isAbsolute, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
const proxyNames = Object.keys(process.env).filter(name => /^(?:https?_proxy|all_proxy|no_proxy|npm_config_(?:https?_proxy|proxy|noproxy|no_proxy))$/i.test(name));
if (process.env.FERRY_LOCAL_NO_PROXY === '1' && proxyNames.length) throw new Error('proxy settings remained in the local regression process');
if (proxyNames.length) {
  const env = { ...process.env, FERRY_LOCAL_NO_PROXY: '1' };
  for (const name of proxyNames) delete env[name];
  const child = spawn(process.execPath, [...process.execArgv, ...process.argv.slice(1)], { env, stdio: 'inherit' });
  const status = await new Promise((resolve, reject) => { child.once('error', reject); child.once('close', resolve); });
  process.exit(status ?? 1);
}
const { values } = parseArgs({ options: { 'work-dir': { type: 'string' } } });
assert(values['work-dir'] && isAbsolute(values['work-dir']), '--work-dir must be absolute');
const work = values['work-dir'];
for (let path = resolve(work);;) {
  try { assert(!(await lstat(path)).isSymbolicLink(), 'linked work directory'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  if (dirname(path) === path) break;
  path = dirname(path);
}
// 既存の D1 / DO 状態や他作業の出力を再利用・上書きしない。
await mkdir(work);
const require = createRequire(import.meta.url);
const wranglerRequire = createRequire(require.resolve('wrangler/package.json'));
const { Miniflare, Log, LogLevel, convertV4MiniflareOptions } = wranglerRequire('miniflare');
const { build } = wranglerRequire('esbuild');
const relay = fileURLToPath(new URL('../', import.meta.url));
const source = join(relay, 'src/index.ts').replaceAll('\\', '/');
const outbox = join(relay, 'src/pairing-outbox.ts').replaceAll('\\', '/');
const device = join(relay, 'src/device-routes.ts').replaceAll('\\', '/');
const entry = join(work, 'test-worker.mjs');
await writeFile(entry, `
import worker, { DeviceDO } from ${JSON.stringify(source)};
import { drainPairingOutbox } from ${JSON.stringify(outbox)};
import { notifyInbox } from ${JSON.stringify(device)};
export { DeviceDO };
let fault = '';
export default { ...worker, async fetch(req, env, ctx) {
  const url = new URL(req.url);
  if (url.pathname === '/__fault') { fault = await req.text(); return new Response('OK'); }
  if (url.pathname === '/__drain') { await drainPairingOutbox(env, undefined, Date.now()+300001); return new Response('OK'); }
  if (url.pathname === '/__delayed') {
    const data=await req.json(); await notifyInbox(env,data.recipient,data.event,data.generation); return new Response('OK');
  }
  const original=env.DEVICE;
  const wrapped={ ...env, DEVICE:{
    idFromName(name) { return original.idFromName(name); },
    get(id) { const stub=original.get(id); return { fetch(request) {
      if (fault && new URL(request.url).pathname==='/notify' && request.headers.get('X-Ferry-Recipient')===fault) {
        fault=''; return Promise.resolve(new Response('temporary failure',{status:503}));
      }
      return stub.fetch(request);
    } }; }
  } };
  return worker.fetch(req,wrapped,ctx);
} };
`, 'utf8');
const bundle = join(work, 'worker.mjs');
await build({ entryPoints: [entry], bundle: true, outfile: bundle, format: 'esm', platform: 'browser', target: 'es2022', external: ['cloudflare:workers'] });
const mf = new Miniflare(convertV4MiniflareOptions({ rootPath: work, host: '127.0.0.1', cf: false, modules: true, scriptPath: bundle, compatibilityDate: '2026-05-26',
  log: new Log(LogLevel.ERROR), d1Databases: { DB: 'ferry-local-regression' }, kvNamespaces: ['DEVICE_KEY_BINDING'],
  durableObjects: { DEVICE: { className: 'DeviceDO', useSQLite: true } },
  d1Persist: join(work, 'd1'), durableObjectsPersist: join(work, 'do'),
  bindings: { SALT: 'local-regression-salt', SESSION_HMAC_SECRET: 'local-regression-hmac-secret-0123456789' },
}));
const origin = await mf.ready;
assert.equal(origin.protocol, 'http:');
assert(['127.0.0.1', 'localhost', '[::1]'].includes(origin.hostname), 'Miniflare must bind to loopback');
const A = 'a'.repeat(32), B = 'b'.repeat(32), C = 'c'.repeat(32), pairId = A + '_' + B;
const fetch = (path, body, token, method = 'POST') => mf.dispatchFetch(new URL(path, origin), {
  method, headers: { 'content-type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) },
  ...(body === undefined ? {} : { body: JSON.stringify(body) }),
});
async function authBody(id) {
  const key = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
  const pubKeySpki = Buffer.from(await crypto.subtle.exportKey('spki', key.publicKey)).toString('base64url');
  const ts = Date.now();
  const message = new TextEncoder().encode('ferry-auth-v1|' + id + '|' + pubKeySpki + '|' + ts);
  const sig = Buffer.from(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key.privateKey, message)).toString('base64url');
  return { deviceId: id, pubKeySpki, ts, sig };
}
async function connect(token) {
  const response = await mf.dispatchFetch(new URL('/inbox', origin), { headers: { Upgrade: 'websocket', Authorization: 'Bearer ' + token } });
  assert.equal(response.status, 101);
  const socket = response.webSocket;
  const messages = [], waiters = [];
  socket.addEventListener('message', (event) => { messages.push(JSON.parse(event.data)); waiters.splice(0).forEach((wake) => wake()); });
  socket.accept();
  const count = async (expected) => {
    const deadline = AbortSignal.timeout(5000);
    while (messages.length < expected) await new Promise((done, reject) => {
      const abort = () => reject(new Error('WebSocket delivery timeout'));
      deadline.addEventListener('abort', abort, { once: true });
      waiters.push(() => { deadline.removeEventListener('abort', abort); done(); });
    });
  };
  return { socket, messages, count };
}
try {
  const db = await mf.getD1Database('DB');
  const schema = await readFile(join(relay, 'schema.sql'), 'utf8');
  for (const statement of schema.split(';').map((sql) => sql.replace(/^\s*--.*$/gm, '').trim()).filter(Boolean)) await db.prepare(statement).run();
  const a = await authBody(A), b = await authBody(B);
  const kv = await mf.getKVNamespace('DEVICE_KEY_BINDING');
  await kv.put('device-pubkey:' + A, a.pubKeySpki);
  assert.equal((await fetch('/auth/token', a)).status, 503); // positive KV を lazy seed しない
  await db.prepare('INSERT INTO device_key_bindings VALUES (?,?,?)').bind(A, a.pubKeySpki, Date.now()).run();
  assert.equal((await fetch('/auth/token', await authBody(A))).status, 401);
  const tokenA = (await (await fetch('/auth/token', a)).json()).cfToken;
  assert(tokenA);
  await db.prepare("UPDATE auth_key_migration SET phase='ready'").run();
  const race = await Promise.all([authBody(C), authBody(C)]);
  const responses = await Promise.all(race.map((body) => fetch('/auth/token', body)));
  assert.deepEqual(responses.map((res) => res.status).sort(), [200, 401]);
  const tokenB = (await (await fetch('/auth/token', b)).json()).cfToken;
  assert(tokenB);
  console.log('workerd D1: importing / immutable key / concurrent first binding OK');
  const wsA = await connect(tokenA), wsB = await connect(tokenB);
  const nonceA = 'a1'.repeat(16), nonceB = 'b1'.repeat(16);
  async function sessions() {
    for (const [token, nonce, key] of [[tokenA, nonceA, 'KEY_A'], [tokenB, nonceB, 'KEY_B']]) {
      assert.equal((await fetch('/pair/session', { displayName: key, publicKey: key, pairingNonce: nonce }, token)).status, 200);
    }
  }
  await sessions();
  await db.prepare(`CREATE TRIGGER fail_delivery BEFORE INSERT ON pairing_outbox WHEN NEW.recipient_id='${B}'
    BEGIN SELECT RAISE(ABORT,'injected outbox failure'); END`).run();
  const request = { sidA: A, sidB: B, nonceA, nonceB, nameA: 'A', nameB: 'B', pkA: 'untrusted' };
  assert.equal((await fetch('/pair/create', request)).status, 503);
  assert.equal((await db.prepare('SELECT COUNT(*) n FROM pairing_nonces').first()).n, 2);
  assert.equal((await db.prepare('SELECT COUNT(*) n FROM pairs').first()).n, 0);
  await db.prepare('DROP TRIGGER fail_delivery').run();
  await mf.dispatchFetch(new URL('/__fault', origin), { method: 'POST', body: B });
  assert.equal((await fetch('/pair/create', request)).status, 200);
  await wsA.count(1);
  assert.equal(wsB.messages.length, 0);
  assert.equal((await db.prepare('SELECT COUNT(*) n FROM pairing_nonces').first()).n, 0);
  assert.deepEqual((await db.prepare('SELECT recipient_id,attempts FROM pairing_outbox').all()).results,
    [{ recipient_id: B, attempts: 1 }]);
  const oldPair = wsA.messages[0];
  const first = await db.prepare('SELECT * FROM pairing_generations WHERE pair_id=?').bind(pairId).first();
  await fetch('/__drain');
  await wsB.count(1);
  assert.deepEqual(wsB.messages[0], oldPair);
  assert.equal(wsB.messages[0].pkA, 'KEY_A');
  assert.equal((await db.prepare('SELECT COUNT(*) n FROM pairing_outbox').first()).n, 0);
  assert.equal((await fetch('/pairs/' + pairId, undefined, tokenA, 'DELETE')).status, 200);
  await wsA.count(2); await wsB.count(2);
  const cancelled = await db.prepare('SELECT * FROM pairing_generations WHERE pair_id=?').bind(pairId).first();
  assert.equal(cancelled.active, 0);
  await sessions();
  assert.equal((await fetch('/pair/link', { sidB: B, nonceB, nameA: 'new A', nameB: 'new B' }, tokenA)).status, 200);
  await wsA.count(3); await wsB.count(3);
  const current = await db.prepare('SELECT * FROM pairing_generations WHERE pair_id=?').bind(pairId).first();
  assert.equal(current.revision, 3);
  for (const [event, generation] of [[oldPair, first.generation], [wsA.messages[1], cancelled.generation],
    [oldPair, undefined], [wsA.messages[1], undefined]]) {
    assert.equal((await fetch('/__delayed', { recipient: B, event, generation })).status, 200);
  }
  assert.equal(wsB.messages.length, 3); // 遅延した旧 pair / unpair は送らない
  wsB.socket.close();
  const reconnect = await connect(tokenB);
  await reconnect.count(1);
  assert.equal(reconnect.messages[0]._pairingRevision, 3);
  assert.equal(reconnect.messages[0].nameA, 'new A');
  assert.equal(reconnect.messages.length, 1);
  reconnect.socket.close(); wsA.socket.close();
  console.log('workerd D1 + real WebSocket: atomic rollback / one-sided failure / retry / unpair / new generation / stale replay rejection OK');
} finally { await mf.dispose(); }
