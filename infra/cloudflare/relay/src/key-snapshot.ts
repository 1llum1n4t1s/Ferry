export interface KeySnapshotEntry { deviceId: string; pubKeySpki: string }

const quote = (value: string) => "'" + value.replaceAll("'", "''") + "'";

/** 検証済み入力だけを渡す。競合一件でも NOT NULL 違反で INSERT 全体を取り消す。 */
export function keySnapshotImportSql(entries: KeySnapshotEntry[], now: number): string {
  return `INSERT INTO device_key_bindings (device_id,pubkey_spki,created_at)
    SELECT json_extract(value,'$.deviceId'),json_extract(value,'$.pubKeySpki'),${now}
    FROM json_each(${quote(JSON.stringify(entries))})
    WHERE EXISTS (SELECT 1 FROM auth_key_migration WHERE singleton=1 AND phase='importing')
    ON CONFLICT(device_id) DO UPDATE SET pubkey_spki=CASE
      WHEN device_key_bindings.pubkey_spki=excluded.pubkey_spki THEN device_key_bindings.pubkey_spki ELSE NULL END`;
}

/** 全件比較直後にも同じ条件を SQL 内で再確認してからだけ ready へ進める。 */
export function keySnapshotReadySql(entries: KeySnapshotEntry[], sha256: string): string {
  return `UPDATE auth_key_migration SET phase='ready', snapshot_sha256=${quote(sha256.toLowerCase())},
    snapshot_count=${entries.length} WHERE singleton=1 AND phase='importing'
    AND (SELECT COUNT(*) FROM device_key_bindings)=${entries.length}
    AND NOT EXISTS (SELECT 1 FROM device_key_bindings d LEFT JOIN json_each(${quote(JSON.stringify(entries))}) j
      ON d.device_id=json_extract(j.value,'$.deviceId')
      WHERE j.value IS NULL OR d.pubkey_spki<>json_extract(j.value,'$.pubKeySpki'));`;
}

/** operator が検証・承認した固定入力だけを受ける。KV scan や待機から ready を推測しない。 */
export async function validateKeySnapshot(text: string, expectedSha256: string, expectedCount: number): Promise<KeySnapshotEntry[]> {
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  const actual = Array.from(new Uint8Array(hash), (byte) => byte.toString(16).padStart(2, '0')).join('');
  if (!/^[a-f0-9]{64}$/i.test(expectedSha256) || actual !== expectedSha256.toLowerCase()) throw new Error('snapshot SHA-256 mismatch');
  const parsed = JSON.parse(text) as { bindings?: unknown };
  if (!Array.isArray(parsed.bindings) || !Number.isSafeInteger(expectedCount) || expectedCount < 0
      || parsed.bindings.length !== expectedCount) throw new Error('snapshot count mismatch');
  const seen = new Set<string>();
  for (const entry of parsed.bindings as KeySnapshotEntry[]) {
    if (!entry || typeof entry.deviceId !== 'string' || !/^[a-f0-9]{32}$/.test(entry.deviceId)
        || typeof entry.pubKeySpki !== 'string' || entry.pubKeySpki.length === 0 || entry.pubKeySpki.length > 256
        || seen.has(entry.deviceId)) throw new Error('invalid or duplicate snapshot binding');
    seen.add(entry.deviceId);
  }
  return parsed.bindings as KeySnapshotEntry[];
}
