// 実BridgeのURL入口を、副作用のないDOM/URL fixtureで再現する。
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.resolve(__dirname, '../../../infra/cloudflare/relay/public/bridge.js'), 'utf8');
const sandbox = {
  URL, URLSearchParams,
  window: { location: { search: '' } },
  document: { getElementById: () => ({}), addEventListener: () => {} },
};
vm.createContext(sandbox);
vm.runInContext(source, sandbox, { filename: 'bridge.js' });
const results = [];
for (const name of ['RERE_CANARY 100%', 'RERE_CANARY %2F', 'ゆろち % # &']) {
  const query = '?sid=' + 'a'.repeat(32) + '&nonce=' + 'b'.repeat(32) + '&name=' + encodeURIComponent(name);
  sandbox.window.location.search = query;
  sandbox.dummyUrl = 'https://rere-canary.invalid/' + query;
  for (const [entry, expression] of [['getParams', 'getParams()'], ['parseQrUrl', 'parseQrUrl(dummyUrl)']]) {
    try {
      const parsed = vm.runInContext(expression, sandbox);
      results.push({ input: name, entry, sidPresent: parsed.sid !== null, output: parsed.name, passed: parsed.name === name });
    } catch (error) { results.push({ input: name, entry, passed: false, error: error.name }); }
  }
}
console.log(JSON.stringify({ node: process.version, results }, null, 2));
process.exitCode = results.every(result => result.passed && result.sidPresent) ? 0 : 1;
