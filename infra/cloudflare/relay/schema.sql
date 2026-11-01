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

-- 認証鍵の強整合な正本。既存 KV は検証済み snapshot からのみ移行し、削除しない。
CREATE TABLE IF NOT EXISTS device_key_bindings (
  device_id TEXT PRIMARY KEY NOT NULL,
  pubkey_spki TEXT NOT NULL,
  created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS auth_key_migration (
  singleton INTEGER PRIMARY KEY CHECK (singleton=1),
  phase TEXT NOT NULL CHECK (phase IN ('importing','ready')),
  snapshot_sha256 TEXT,
  snapshot_count INTEGER
);
INSERT OR IGNORE INTO auth_key_migration (singleton, phase) VALUES (1, 'importing');

-- pairs PUT は created_at を更新するため、配送世代は独立して保持する。
CREATE TABLE IF NOT EXISTS pairing_generations (
  pair_id TEXT PRIMARY KEY,
  generation TEXT NOT NULL,
  revision INTEGER NOT NULL DEFAULT 1,
  active INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0,1))
);
CREATE TABLE IF NOT EXISTS pairing_outbox (
  event_id TEXT NOT NULL,
  recipient_id TEXT NOT NULL,
  pair_id TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  next_attempt_at INTEGER NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (event_id, recipient_id)
);
CREATE INDEX IF NOT EXISTS idx_outbox_due ON pairing_outbox(next_attempt_at, created_at);
CREATE INDEX IF NOT EXISTS idx_outbox_recipient ON pairing_outbox(recipient_id, next_attempt_at);
CREATE INDEX IF NOT EXISTS idx_outbox_pair ON pairing_outbox(pair_id);
