import { describe, it, expect } from 'vitest';
import { validateKeySnapshot, keySnapshotImportSql, keySnapshotReadySql } from '../src/key-snapshot';
import { SqliteD1 } from './d1-fixture';
import { readFileSync } from 'node:fs';

async function sha(text: string) {
  return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text))),
    (byte) => byte.toString(16).padStart(2, '0')).join('');
}
describe('承認済み snapshot と不変 D1 鍵', () => {
  it('raw snapshot の SHA / 件数 / 重複を検査する', async () => {
    const entry = { deviceId: 'a'.repeat(32), pubKeySpki: 'public-key' };
    const text = JSON.stringify({ bindings: [entry] });
    await expect(validateKeySnapshot(text, await sha(text), 1)).resolves.toEqual([entry]);
    await expect(validateKeySnapshot(text, '0'.repeat(64), 1)).rejects.toThrow('SHA-256');
    await expect(validateKeySnapshot(text, await sha(text), 0)).rejects.toThrow('count');
    const duplicate = JSON.stringify({ bindings: [entry, entry] });
    await expect(validateKeySnapshot(duplicate, await sha(duplicate), 2)).rejects.toThrow('duplicate');
  });
  it('schema の再適用は ready / snapshot 証跡 / 既存鍵を戻さず、NULL device ID を拒否する', () => {
    const db = new SqliteD1();
    db.seedKey('a'.repeat(32), 'known');
    db.sqlite.exec("UPDATE auth_key_migration SET snapshot_sha256='approved', snapshot_count=1");
    db.sqlite.exec(readFileSync(new URL('../schema.sql', import.meta.url), 'utf8'));
    expect(db.sqlite.prepare('SELECT phase,snapshot_sha256,snapshot_count FROM auth_key_migration').get())
      .toMatchObject({ phase: 'ready', snapshot_sha256: 'approved', snapshot_count: 1 });
    expect(db.sqlite.prepare('SELECT pubkey_spki FROM device_key_bindings').get()?.pubkey_spki).toBe('known');
    expect(() => db.sqlite.exec("INSERT INTO device_key_bindings VALUES (NULL,'key',0)")).toThrow();
  });
  it('実際の import SQL は競合一件で全件を rollback し、異なる snapshot では ready にしない', () => {
    const db = new SqliteD1(false);
    const a = { deviceId: 'a'.repeat(32), pubKeySpki: 'original' };
    const b = { deviceId: 'b'.repeat(32), pubKeySpki: 'second' };
    db.seedKey(a.deviceId, a.pubKeySpki);
    expect(() => db.sqlite.exec(keySnapshotImportSql([b, { ...a, pubKeySpki: 'attacker' }], 1))).toThrow();
    expect(db.sqlite.prepare('SELECT COUNT(*) n FROM device_key_bindings').get()?.n).toBe(1);
    db.sqlite.exec(keySnapshotReadySql([a, b], 'a'.repeat(64)));
    expect(db.sqlite.prepare('SELECT phase FROM auth_key_migration').get()?.phase).toBe('importing');
    db.sqlite.exec(keySnapshotImportSql([a, b], 1));
    db.sqlite.exec(keySnapshotReadySql([a, { ...b, pubKeySpki: 'wrong' }], 'b'.repeat(64)));
    expect(db.sqlite.prepare('SELECT phase FROM auth_key_migration').get()?.phase).toBe('importing');
    db.sqlite.exec(keySnapshotReadySql([a, b], 'c'.repeat(64)));
    expect(db.sqlite.prepare('SELECT phase,snapshot_sha256,snapshot_count FROM auth_key_migration').get())
      .toMatchObject({ phase: 'ready', snapshot_sha256: 'c'.repeat(64), snapshot_count: 2 });
  });
});
