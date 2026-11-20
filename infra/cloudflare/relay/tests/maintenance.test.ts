import { describe, expect, it, vi } from 'vitest';
import { cleanupExpiredPairingData, PAIRING_TEMP_TTL_MS } from '../src/maintenance';
import { SqliteD1 } from './d1-fixture';

describe('D1 pairing 一時データ cleanup', () => {
  it('1時間より古い sessions/nonces だけを消し、境界の行・新しい行・pairs は残す', async () => {
    const now = Date.UTC(2026, 7, 29, 3, 17, 0);
    const cutoff = now - PAIRING_TEMP_TTL_MS;
    const db = new SqliteD1();
    const batch = vi.spyOn(db, 'batch');
    const addSession = db.sqlite.prepare('INSERT INTO sessions (sid, created_at) VALUES (?,?)');
    const addNonce = db.sqlite.prepare('INSERT INTO pairing_nonces (sid, nonce, created_at) VALUES (?,?,?)');
    for (const [sid, createdAt] of [['old1', cutoff - 1], ['old2', cutoff - 10_000], ['edge', cutoff], ['fresh', now]] as const) {
      addSession.run(sid, createdAt);
    }
    for (const [sid, createdAt] of [['old1', cutoff - 1], ['edge', cutoff], ['fresh', now]] as const) {
      addNonce.run(sid, 'n', createdAt);
    }
    // pairs は remote-unpair 検出に使う永続 SSoT なので、古くても消さない。
    db.sqlite.prepare('INSERT INTO pairs VALUES (?,?,?,?)').run('p-old', '', '', cutoff - 1_000_000);

    const result = await cleanupExpiredPairingData({ DB: db as never }, now);

    expect(result).toEqual({ sessionsDeleted: 2, noncesDeleted: 1 });
    expect(batch).toHaveBeenCalledTimes(1); // 2 文は単一 batch
    const sids = (table: string) => (db.sqlite.prepare(`SELECT sid FROM ${table} ORDER BY sid`).all() as Array<{ sid: string }>).map((r) => r.sid);
    expect(sids('sessions')).toEqual(['edge', 'fresh']);
    expect(sids('pairing_nonces')).toEqual(['edge', 'fresh']);
    expect(db.sqlite.prepare('SELECT COUNT(*) AS n FROM pairs').get()).toEqual({ n: 1 });
  });

  it('DB binding が無ければ fail closed', async () => {
    await expect(cleanupExpiredPairingData({ DB: undefined as never })).rejects.toThrow('DB binding');
  });
});
