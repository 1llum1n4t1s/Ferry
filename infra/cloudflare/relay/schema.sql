-- Ferry ペア台帳 D1 スキーマ (CF 単独完結移行 Step 2)。
-- 適用: pnpm dlx wrangler d1 execute ferry_ledger --remote --file=schema.sql
--
-- sessions / pairing_nonces は QR ペアリングの一時データ (1h で stale)。
-- pairs は永続 SSoT (明示 unpair でのみ削除。remote-unpair を 404 で検出する基盤)。

CREATE TABLE IF NOT EXISTS sessions (
  sid          TEXT PRIMARY KEY,
  display_name TEXT NOT NULL DEFAULT '',
  public_key   TEXT NOT NULL DEFAULT '',
  created_at   INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS pairing_nonces (
  sid        TEXT PRIMARY KEY,
  nonce      TEXT NOT NULL,
  created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS pairs (
  pair_id    TEXT PRIMARY KEY,
  name_a     TEXT NOT NULL DEFAULT '',
  name_b     TEXT NOT NULL DEFAULT '',
  created_at INTEGER NOT NULL
);

-- 1h stale 掃除 (sessions/pairing_nonces) を created_at で引くためのインデックス。
CREATE INDEX IF NOT EXISTS idx_sessions_created ON sessions(created_at);
CREATE INDEX IF NOT EXISTS idx_nonces_created ON pairing_nonces(created_at);
