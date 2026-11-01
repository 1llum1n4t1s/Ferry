// 検証済み snapshot の明示移行。既定は local、remote は operator が明示した場合だけ。
import { parseArgs } from 'node:util';
import { readFile, writeFile, mkdir, lstat, access, constants } from 'node:fs/promises';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { randomUUID } from 'node:crypto';
import { validateKeySnapshot, keySnapshotImportSql, keySnapshotReadySql } from '../src/key-snapshot.ts';

const { values } = parseArgs({ options: {
  input: { type: 'string' }, sha256: { type: 'string' }, count: { type: 'string' },
  remote: { type: 'boolean' }, 'work-dir': { type: 'string' },
  'persist-to': { type: 'string' }, 'writers-frozen': { type: 'boolean' },
  'snapshot-approved': { type: 'boolean' }, activate: { type: 'boolean' },
} });
if (!values.input || !values.sha256 || values.count === undefined
    || !values['writers-frozen'] || !values['snapshot-approved']) {
  throw new Error('input / sha256 / count / writers-frozen / snapshot-approved are required');
}
if (values.remote ? Boolean(values['persist-to']) || !values['work-dir'] : !values['persist-to']) {
  throw new Error('local requires persist-to; remote requires work-dir and forbids persist-to');
}
const directory = values.remote ? values['work-dir'] : values['persist-to'];
if (!isAbsolute(directory)) throw new Error('work directory must be absolute');
let ancestor = resolve(directory);
while (true) {
  try { if ((await lstat(ancestor)).isSymbolicLink()) throw new Error('linked work directory'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  const parent = dirname(ancestor);
  if (parent === ancestor) break;
  ancestor = parent;
}
await mkdir(directory, { recursive: true });
await access(directory, constants.W_OK);
const text = await readFile(values.input, 'utf8');
const entries = await validateKeySnapshot(text, values.sha256, Number(values.count));
const relay = fileURLToPath(new URL('../', import.meta.url));
const require = createRequire(import.meta.url);
const wrangler = join(dirname(require.resolve('wrangler/package.json')), 'bin/wrangler.js');
const sqlFile = join(directory, 'approved-key-import-' + randomUUID() + '.sql');
await writeFile(sqlFile, '', { encoding: 'utf8', flag: 'wx' });
async function execute(sql, query = false) {
  // remote --file は bulk import API なので SELECT の rows を照合する用途には使わない。
  if (!query) await writeFile(sqlFile, sql, { encoding: 'utf8', flag: 'w' });
  const target = values.remote ? ['--remote'] : ['--local', '--persist-to', directory];
  const statement = query ? ['--command', sql] : ['--file', sqlFile];
  const child = spawnSync(process.execPath, [wrangler, 'd1', 'execute', 'ferry_ledger', ...target, ...statement, '--json'], {
    cwd: relay, encoding: 'utf8', env: { ...process.env, WRANGLER_SEND_METRICS: 'false' },
  });
  if (child.status !== 0) throw new Error('D1 import/query failed; no ready transition');
  return JSON.parse(child.stdout);
}
await execute(await readFile(join(relay, 'schema.sql'), 'utf8'));
const state = await execute('SELECT phase FROM auth_key_migration WHERE singleton=1', true);
if (state[0].results[0]?.phase !== 'importing') throw new Error('migration is not importing; ready cannot be reset');
await execute(keySnapshotImportSql(entries, Date.now()));
const rows = (await execute('SELECT device_id,pubkey_spki FROM device_key_bindings', true))[0].results;
const actualCount = (await execute('SELECT COUNT(*) AS n FROM device_key_bindings', true))[0].results[0].n;
const expected = new Map(entries.map((entry) => [entry.deviceId, entry.pubKeySpki]));
if (actualCount !== entries.length || rows.length !== entries.length || rows.some((row) => expected.get(row.device_id) !== row.pubkey_spki)) {
  throw new Error('D1 full snapshot comparison failed; migration remains importing');
}
if (values.activate) {
  await execute(keySnapshotReadySql(entries, values.sha256));
  const activated = (await execute('SELECT phase FROM auth_key_migration WHERE singleton=1', true))[0].results[0];
  if (activated?.phase !== 'ready') throw new Error('ready transition failed');
}
console.log(JSON.stringify({ remote: Boolean(values.remote), count: entries.length, sha256: values.sha256.toLowerCase(), ready: Boolean(values.activate) }));
