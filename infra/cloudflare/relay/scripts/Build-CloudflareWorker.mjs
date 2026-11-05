// 製品コードのビルドだけを担当する。Cloudflare API・認証情報は扱わない。
// esbuild が既存依存にある製品は bundle、それ以外は Node 22.18+ の型除去を使う。
import { createRequire } from 'node:module';
import { spawn, spawnSync } from 'node:child_process';
import { readFile, writeFile, mkdir, lstat } from 'node:fs/promises';
import { existsSync, writeSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { resolve, dirname, extname, relative, sep, delimiter, isAbsolute, join } from 'node:path';
import { parseArgs } from 'node:util';
const { values } = parseArgs({ options: { main: { type: 'string' }, out: { type: 'string' }, root: { type: 'string' }, project: { type: 'string' }, minify: { type: 'boolean', default: false } } });
if (!values.main || !values.out || !values.project) throw new Error('--main --out --project が必要です。');
const fallbackToken = process.env.FERRY_NODE_FALLBACK;
const supportedNode = version => { const match = /^(\d+)\.(\d+)\.\d+(?:[-+].*)?$/.exec(version ?? ''); if (!match) return false; const major = Number(match[1]), minor = Number(match[2]); return major > 22 || (major === 22 && minor >= 18); };
if (fallbackToken && (process.versions.bun || !supportedNode(process.versions.node) || typeof (await import('node:module')).stripTypeScriptTypes !== 'function')) throw new Error('Node.js の型除去を使用できません。');
const main = resolve(values.main), output = resolve(values.out), project = resolve(values.project);
const outputRoot=dirname(output);
async function checkOutput(path) {
  path=resolve(path);
  if(path!==outputRoot&&!path.startsWith(outputRoot+sep)) throw new Error(`ビルド先の範囲外: ${path}`);
  for(let current=path;;current=dirname(current)) {
    try {if((await lstat(current)).isSymbolicLink()) throw new Error(`ビルド先にリンクがあります: ${current}`);}
    catch(error){if(error.code!=='ENOENT')throw error;}
    if(dirname(current)===current)break;
  }
}
for(const path of [output,output+'.map',resolve(outputRoot,'modules.json')]) await checkOutput(path);
const require = createRequire(resolve(project, 'package.json'));
let esbuild;
try { esbuild = require('esbuild'); } catch {
  try { esbuild = createRequire(require.resolve('wrangler/package.json'))('esbuild'); } catch {}
}
if (!esbuild && process.versions.bun) {
  const challenge = randomUUID();
  const probe = `const m=require('node:module');console.log(JSON.stringify({challenge:${JSON.stringify(challenge)},execPath:process.execPath,node:process.versions.node,bun:!!process.versions.bun,strip:typeof m.stripTypeScriptTypes==='function'}))`;
  let node;
  for (const entry of (process.env.PATH ?? process.env.Path ?? '').split(delimiter)) {
    const dir = resolve(entry.replace(/^"(.*)"$/, '$1') || '.');
    const candidate = join(dir, process.platform === 'win32' ? 'node.exe' : 'node');
    if (!existsSync(candidate)) continue;
    const result = spawnSync(candidate, ['-e', probe], { encoding: 'utf8', timeout: 3000, maxBuffer: 16384, windowsHide: true });
    if (result.status !== 0 || result.error) continue;
    try { const found = JSON.parse(result.stdout.trim()); if (found.challenge === challenge && found.bun === false && found.strip === true && supportedNode(found.node) && typeof found.execPath === 'string' && isAbsolute(found.execPath)) { node = found.execPath; break; } } catch {}
  }
  if (!node) throw new Error('esbuild がないビルドには Node 22.18+ が必要です。');
  const token = randomUUID();
  const child = spawn(node, process.argv.slice(1), { stdio: ['inherit', 'inherit', 'inherit', 'pipe'], env: { ...process.env, FERRY_NODE_FALLBACK: token } });
  const completion = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('close', (code) => resolve(code));
  });
  let confirmation = '';
  child.stdio[3]?.setEncoding('utf8');
  child.stdio[3]?.on('data', chunk => { confirmation += chunk; if (confirmation.length > 1024) child.kill(); });
  const status = await completion;
  if (status !== 0) process.exit(status ?? 1);
  if (confirmation !== token) throw new Error('Node.js のビルド完了を確認できません。');
  process.exit(0);
}
await mkdir(dirname(output), { recursive: true });
if (esbuild) {
  await esbuild.build({ entryPoints: [main], outfile: output, bundle: true, format: 'esm', platform: 'neutral', target: 'es2022', conditions: ['workerd', 'worker', 'browser'], external: ['cloudflare:*', 'node:*'], loader: { '.sql': 'text', '.txt': 'text' }, sourcemap: true, minify: values.minify });
  await writeFile(resolve(dirname(output),'modules.json'),JSON.stringify([relative(dirname(output),output).replaceAll('\\','/')]));
} else {
  if(values.minify) throw new Error('minify には製品の既存 esbuild が必要です。');
  const root = resolve(values.root ?? dirname(main));
  const seen = new Set();
  async function emit(path) {
    path = resolve(path);
    if (path !== root && !path.startsWith(root + sep)) throw new Error(`ビルド範囲外: ${path}`);
    if (seen.has(path)) return;
    seen.add(path);
    let code = await readFile(path, 'utf8');
    if (extname(path) === '.ts') {
      const stripTypeScriptTypes = (await import('node:module')).stripTypeScriptTypes;
      if (typeof stripTypeScriptTypes !== 'function') throw new Error('esbuild がない TypeScript のビルドには Node 22.18+ が必要です。');
      code = stripTypeScriptTypes(code, { mode: 'strip', sourceUrl: path });
    }
    const imports = [...code.matchAll(/(?:\bfrom\s*|\bimport\s*\(\s*|\bimport\s*)["']([^"']+)["']/g)];
    for (const match of imports) {
      const specifier = match[1];
      if (specifier.startsWith('.')) await emit(resolve(dirname(path), specifier));
      else if (!specifier.startsWith('node:') && !specifier.startsWith('cloudflare:')) throw new Error(`依存をbundleする既存esbuildが必要です: ${specifier}`);
    }
    if (extname(path) === '.ts') code = code.replace(/(["'])(\.{1,2}\/[^"']+)\.ts\1/g, '$1$2.js$1');
    // JS importers can also reference TypeScript.
    else code = code.replace(/(["'])(\.{1,2}\/[^"']+)\.ts\1/g, '$1$2.js$1');
    const target = resolve(dirname(output), relative(root, path).replace(/\.ts$/, '.js'));
    await checkOutput(target);
    await mkdir(dirname(target), { recursive: true });
    await writeFile(target, code);
  }
  await emit(main);
  const emitted = resolve(dirname(output), relative(root, main).replace(/\.ts$/, '.js'));
  if (emitted !== output) await writeFile(output, `export { default } from ${JSON.stringify('./' + relative(dirname(output), emitted).replaceAll('\\', '/'))};\nexport * from ${JSON.stringify('./' + relative(dirname(output), emitted).replaceAll('\\', '/'))};\n`);
  await writeFile(resolve(dirname(output),'modules.json'),JSON.stringify([...new Set([relative(dirname(output),output).replaceAll('\\','/'), ...[...seen].map(path=>relative(root,path).replace(/\.ts$/,'.js').replaceAll('\\','/'))])]));
}
if (fallbackToken) writeSync(3, fallbackToken);
