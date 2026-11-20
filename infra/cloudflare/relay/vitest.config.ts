import { defineConfig } from 'vitest/config';

// Ferry Workers の単体テスト設定。
//
// Cloudflare bindings (D1 / Durable Objects / RateLimit) に依存しない **純関数 / pure handler ロジック**
// を Node.js (Web Crypto API 内蔵) でそのまま走らせる軽量構成（D1 はテスト内の SQLite フィクスチャで代替）。
// 実 workerd / D1 / WebSocket を使う統合検証は tests/workerd-regressions.mjs が担当する。
export default defineConfig({
    test: {
        include: ['tests/**/*.test.ts'],
        environment: 'node',
        globals: false,
    },
});
