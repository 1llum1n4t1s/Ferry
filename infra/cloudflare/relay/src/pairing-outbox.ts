import type { Env } from './index';
import { notifyInbox } from './device-routes';

export const PAIRING_DELIVERY_TTL_MS = 60 * 60 * 1000;
const BATCH_LIMIT = 50;

interface Delivery {
  event_id: string;
  recipient_id: string;
  pair_id: string;
  payload_json: string;
  expires_at: number;
  attempts: number;
  current_generation: string | null;
}

/** 一回の実行量を固定する。並行 drain による重複は許容し、宛先別に受理後だけ削除する。 */
export async function drainPairingOutbox(env: Env, recipient?: string, now = Date.now()): Promise<void> {
  // OR で本人条件を任意化すると due index だけの全宛先走査になるため、SQL 定数で分ける。
  const condition = recipient === undefined ? 'o.next_attempt_at<=?' : 'o.recipient_id=? AND o.next_attempt_at<=?';
  const parameters = recipient === undefined ? [now, BATCH_LIMIT] : [recipient, now, BATCH_LIMIT];
  const result = await env.DB.prepare(`SELECT o.*, CASE WHEN p.pair_id IS NOT NULL AND g.active=1 THEN g.generation END AS current_generation
    FROM pairing_outbox o
    LEFT JOIN pairing_generations g ON g.pair_id=o.pair_id
    LEFT JOIN pairs p ON p.pair_id=o.pair_id
    WHERE ${condition}
    ORDER BY o.next_attempt_at, o.created_at LIMIT ?`)
    .bind(...parameters).all<Delivery>();
  for (const row of result.results) {
    if (row.expires_at <= now || row.current_generation !== row.event_id) {
      await deleteDelivery(env, row);
      continue;
    }
    try {
      await notifyInbox(env, row.recipient_id, JSON.parse(row.payload_json), row.event_id);
      await deleteDelivery(env, row);
    } catch (error) {
      // accepted 後の削除失敗も再送可能にする。新世代/別宛先の行は変更しない。
      const delay = Math.min(300_000, 30_000 * 2 ** Math.min(row.attempts, 4));
      await env.DB.prepare(`UPDATE pairing_outbox SET attempts=attempts+1, next_attempt_at=?
        WHERE event_id=? AND recipient_id=?`).bind(now + delay, row.event_id, row.recipient_id).run();
      console.error('pairing delivery deferred', String(error));
    }
  }
}

function deleteDelivery(env: Env, row: Delivery): Promise<D1Result> {
  return env.DB.prepare('DELETE FROM pairing_outbox WHERE event_id=? AND recipient_id=?')
    .bind(row.event_id, row.recipient_id).run();
}

/** nonce claim が生きている同じ batch 内だけで世代と両宛先の配送予定を確定する。 */
export function pairingDeliveryStatements(
  env: Env, eventId: string, pairId: string, sidA: string, sidB: string, event: object,
  now: number, claimSql: string, claimArgs: unknown[],
): D1PreparedStatement[] {
  const guard = `EXISTS (${claimSql})`;
  return [
    env.DB.prepare(`INSERT INTO pairing_generations (pair_id, generation)
      SELECT ?,? WHERE ${guard}
      ON CONFLICT(pair_id) DO UPDATE SET generation=excluded.generation, revision=revision+1, active=1`)
      .bind(pairId, eventId, ...claimArgs),
    ...[sidA, sidB].map((recipient) => env.DB.prepare(`INSERT INTO pairing_outbox
      (event_id, recipient_id, pair_id, payload_json, created_at, expires_at, next_attempt_at)
      SELECT ?,?,?,?,?,?,? WHERE ${guard}`)
      .bind(eventId, recipient, pairId, JSON.stringify(event), now, now + PAIRING_DELIVERY_TTL_MS, now, ...claimArgs)),
  ];
}

/** 一時障害は確定済みのペア成立を取り消さず、cron / inbox 接続から復旧する。 */
export async function tryDrainPairingOutbox(env: Env, recipient?: string): Promise<void> {
  try { await drainPairingOutbox(env, recipient); }
  catch (error) { console.error('pairing outbox unavailable; retry pending', String(error)); }
}
