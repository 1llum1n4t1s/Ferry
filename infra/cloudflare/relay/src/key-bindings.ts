import type { Env } from './index';

/** KV の positive/negative とも移行判断には使わない。未移行の鍵は安全側で閉じる。 */
export async function bindDeviceKey(env: Env, deviceId: string, pubKey: string): Promise<'ok' | 'mismatch' | 'unavailable'> {
  if (!env.DB) return 'unavailable';
  try {
    const state = await env.DB.prepare('SELECT phase FROM auth_key_migration WHERE singleton=1')
      .first<{ phase: string }>();
    if (state?.phase !== 'ready' && state?.phase !== 'importing') return 'unavailable';
    const existing = await env.DB.prepare('SELECT pubkey_spki FROM device_key_bindings WHERE device_id=?')
      .bind(deviceId).first<{ pubkey_spki: string }>();
    if (existing) return existing.pubkey_spki === pubKey ? 'ok' : 'mismatch';
    // phase の不明値・欠損・importing は、新規 ID の登録を許可しない。
    if (state?.phase !== 'ready') return 'unavailable';
    const results = await env.DB.batch([
      env.DB.prepare(`INSERT OR IGNORE INTO device_key_bindings (device_id, pubkey_spki, created_at)
        SELECT ?,?,? WHERE EXISTS (SELECT 1 FROM auth_key_migration WHERE singleton=1 AND phase='ready')`)
        .bind(deviceId, pubKey, Date.now()),
      env.DB.prepare('SELECT pubkey_spki FROM device_key_bindings WHERE device_id=?').bind(deviceId),
    ]);
    const winner = results[1].results?.[0] as { pubkey_spki: string } | undefined;
    if (!winner) return 'unavailable';
    return winner.pubkey_spki === pubKey ? 'ok' : 'mismatch';
  } catch (error) {
    console.error('auth binding D1 unavailable', String(error));
    return 'unavailable';
  }
}
