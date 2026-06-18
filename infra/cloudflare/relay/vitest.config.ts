import { defineConfig } from 'vitest/config';

// Ferry Workers の単体テスト設定。
//
// Cloudflare bindings (KV / RateLimit) や Firebase REST に依存しない **純関数 / pure handler ロジック**
// を Node.js (Web Crypto API 内蔵) でそのまま走らせる軽量構成。フル Workers 統合は将来
// @cloudflare/vitest-pool-workers の導入時に追加する。
export default defineConfig({
    test: {
        include: ['tests/**/*.test.ts'],
        environment: 'node',
        globals: false,
    },
});
