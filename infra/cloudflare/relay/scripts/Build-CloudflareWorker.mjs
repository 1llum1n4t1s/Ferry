// 製品コードのビルドだけを担当する。Cloudflare API・認証情報は扱わない。
// esbuild が既存依存にある製品は bundle、それ以外は Node 22.18+ の型除去を使う。
import { createRequire, stripTypeScriptTypes } from 'node:module';
import { readFile, writeFile, mkdir, lstat } from 'node:fs/promises';
import { resolve, dirname, extname, relative, sep } from 'node:path';
import { parseArgs } from 'node:util';
const { values } = parseArgs({ options: { main: { type: 'string' }, out: { type: 'string' }, root: { type: 'string' }, project: { type: 'string' }, minify: { type: 'boolean', default: false } } });
if (!values.main || !values.out || !values.project) throw new Error('--main --out --project が必要です。');
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
    if (extname(path) === '.ts') code = stripTypeScriptTypes(code, { mode: 'strip', sourceUrl: path });
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
