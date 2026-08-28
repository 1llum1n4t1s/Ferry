import { describe, expect, it, vi } from 'vitest';
import { cleanupExpiredPairingData, PAIRING_TEMP_TTL_MS } from '../src/maintenance';

describe('D1 pairing 一時データ cleanup', () => {
  it('1時間より古い sessions/nonces だけを同じ cutoff の batch で削除する', async () => {
    const now = Date.UTC(2026, 7, 29, 3, 17, 0);
    const cutoff = now - PAIRING_TEMP_TTL_MS;
    const statements: Array<{ sql: string; args: unknown[] }> = [];
    const db = {
      prepare: vi.fn((sql: string) => ({
        bind: (...args: unknown[]) => {
          const statement = { sql, args };
          statements.push(statement);
          return statement;
        },
      })),
      batch: vi.fn(async () => [
        { meta: { changes: 3 } },
        { meta: { changes: 5 } },
      ]),
    };

    const result = await cleanupExpiredPairingData({ DB: db as never }, now);

    expect(statements).toEqual([
      { sql: 'DELETE FROM sessions WHERE created_at < ?', args: [cutoff] },
      { sql: 'DELETE FROM pairing_nonces WHERE created_at < ?', args: [cutoff] },
    ]);
    expect(db.batch).toHaveBeenCalledTimes(1);
    expect(result).toEqual({ sessionsDeleted: 3, noncesDeleted: 5 });
  });

  it('DB binding が無ければ fail closed', async () => {
    await expect(cleanupExpiredPairingData({ DB: undefined as never })).rejects.toThrow('DB binding');
  });
});
