import type { Env } from './index';

/** QR/コードペアリングの一時データは、認証判定と同じ 1 時間で stale になる。 */
export const PAIRING_TEMP_TTL_MS = 60 * 60 * 1000;

export interface PairingCleanupResult {
  sessionsDeleted: number;
  noncesDeleted: number;
}

/**
 * 期限切れの一時行だけを D1 の単一 batch で削除する。
 * `pairs` は remote-unpair 検出に使う永続 SSoT なので対象に含めない。
 */
export async function cleanupExpiredPairingData(
  env: Pick<Env, 'DB'>,
  now = Date.now(),
): Promise<PairingCleanupResult> {
  if (!env.DB) throw new Error('DB binding is not configured');
  const cutoff = now - PAIRING_TEMP_TTL_MS;
  const results = await env.DB.batch([
    env.DB.prepare('DELETE FROM sessions WHERE created_at < ?').bind(cutoff),
    env.DB.prepare('DELETE FROM pairing_nonces WHERE created_at < ?').bind(cutoff),
  ]);
  return {
    sessionsDeleted: changesOf(results[0]),
    noncesDeleted: changesOf(results[1]),
  };
}

function changesOf(result: unknown): number {
  const changes = (result as { meta?: { changes?: unknown } } | undefined)?.meta?.changes;
  return typeof changes === 'number' && Number.isFinite(changes) && changes >= 0 ? changes : 0;
}
