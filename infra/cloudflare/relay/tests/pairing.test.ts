/**
 * CF 単独完結移行 Step 2 / Step 7: pairing-routes の単体テスト。
 *
 * - derivePairId: C# ConnectionService.GeneratePairId (string.Compare Ordinal 昇順 + "_" 連結) と一致することを固定する。
 *   不一致だと A 側と B 側で別の pairId を導出し、signaling DO が別インスタンスに分裂して接続不能になる。
 * - handlePairLink: PC コード貼付ペアリング (bearer 必須 + 相手セッション存在のみ要求) の認可ロジックを固定する。
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { derivePairId, handlePairCreate, handlePairLink, handlePairSession, handlePairs } from '../src/pairing-routes';
import { mintSessionToken } from '../src/auth';
import type { Env } from '../src/index';

const notifyInboxMock = vi.fn(async () => {});
vi.mock('../src/device-routes', () => ({
  notifyInbox: (...args: unknown[]) => notifyInboxMock(...args),
}));

const A = 'a'.repeat(32);
const B = 'b'.repeat(32);
const PAIR_ID_RE = /^[a-f0-9]{32}_[a-f0-9]{32}$/;

describe('derivePairId', () => {
  it('Ordinal 昇順で連結する (引数順に依存しない)', () => {
    expect(derivePairId(A, B)).toBe(`${A}_${B}`);
    expect(derivePairId(B, A)).toBe(`${A}_${B}`); // 入替えても同じ
  });

  it('結果は pairId 正規表現にマッチする', () => {
    expect(derivePairId(A, B)).toMatch(PAIR_ID_RE);
    expect(derivePairId(B, A)).toMatch(PAIR_ID_RE);
  });

  it('hex 小文字は JS 文字列比較が .NET Ordinal と一致する (代表値)', () => {
    const x = '0'.repeat(32);
    const y = 'f'.repeat(32);
    // '0'(0x30) < 'f'(0x66) なので x が先
    expect(derivePairId(y, x)).toBe(`${x}_${y}`);
  });
});

// ------------------ handlePairLink (D1 stub + notifyInbox mock) ------------------

class FakeD1 {
  private nonces = new Map<string, { nonce: string; createdAt: number }>(); // sid -> pairing_nonces 行
  private sessions = new Map<string, { displayName: string; publicKey: string; createdAt: number }>();
  private publicKeys = new Map<string, string>(); // sid -> sessions.public_key
  private pairs = new Map<string, { nameA: string; nameB: string; createdAt: number }>();

  setSessionActive(sid: string, createdAt: number, nonce = 'n'.repeat(32)) {
    this.nonces.set(sid, { nonce, createdAt });
    this.sessions.set(sid, { displayName: '', publicKey: this.publicKeys.get(sid) ?? '', createdAt });
  }

  /** pairing_nonces 行が残っているか（/pair/create の nonce 消費を検証する）。 */
  hasNonce(sid: string): boolean {
    return this.nonces.has(sid);
  }

  /** rere #C-32 用: sessions.public_key（サーバーが持つ権威データ）を仕込む。 */
  setSessionPublicKey(sid: string, publicKey: string) {
    this.publicKeys.set(sid, publicKey);
    const session = this.sessions.get(sid);
    if (session) session.publicKey = publicKey;
  }

  hasPair(pairId: string): boolean {
    return this.pairs.has(pairId);
  }

  getPair(pairId: string): { nameA: string; nameB: string; createdAt: number } | undefined {
    return this.pairs.get(pairId);
  }

  setPair(pairId: string, nameA = '', nameB = '', createdAt = Date.now()): void {
    this.pairs.set(pairId, { nameA, nameB, createdAt });
  }

  prepare(sql: string) {
    const nonces = this.nonces;
    const sessions = this.sessions;
    const publicKeys = this.publicKeys;
    const pairs = this.pairs;
    // sessions と pairing_nonces で返す列が違うので SQL で振り分ける
    const isSessions = sql.includes('FROM sessions');
    const isSessionInsert = sql.includes('INTO sessions');
    const isSessionDelete = sql.includes('DELETE FROM sessions');
    const isNonceInsert = sql.includes('INTO pairing_nonces');
    const isDeleteNonce = sql.includes('DELETE FROM pairing_nonces');
    const isNonceClaim = sql.includes('UPDATE pairing_nonces SET nonce=?');
    const isPairSelect = sql.includes('FROM pairs');
    const isPairInsert = sql.includes('INTO pairs');
    const isPairUpdate = sql.includes('UPDATE pairs');
    const isPairDelete = sql.includes('DELETE FROM pairs');
    // 条件付き DELETE (WHERE sid=? AND nonce=?) は compare-and-swap。nonce 不一致なら消えない。
    const isConditionalDelete = isDeleteNonce && sql.includes('nonce=?');
    return {
      bind: (...args: unknown[]) => {
        const apply = (): number => {
          if (isSessionInsert) {
            const sid = args[0] as string;
            const displayName = args[1] as string;
            const publicKey = args[2] as string;
            const createdAt = args[3] as number;
            sessions.set(sid, { displayName, publicKey, createdAt });
            publicKeys.set(sid, publicKey);
            return 1;
          }
          if (isSessionDelete) {
            const existed = sessions.delete(args[0] as string);
            publicKeys.delete(args[0] as string);
            return existed ? 1 : 0;
          }
          if (isNonceInsert) {
            nonces.set(args[0] as string, { nonce: args[1] as string, createdAt: args[2] as number });
            return 1;
          }
          if (isNonceClaim) {
            const claim = args[0] as string;
            const sidA = args[3] as string;
            const nonceA = args[4] as string;
            const cutoffA = args[5] as number;
            const sidB = args[6] as string;
            const nonceB = args[7] as string;
            const cutoffB = args[8] as number;
            const rowA = nonces.get(sidA);
            const rowB = nonces.get(sidB);
            if (!rowA || !rowB || rowA.nonce !== nonceA || rowB.nonce !== nonceB) return 0;
            if (rowA.createdAt < cutoffA || rowB.createdAt < cutoffB) return 0;
            rowA.nonce = claim;
            rowB.nonce = claim;
            return 2;
          }
          if (isPairInsert) {
            const pairId = args[0] as string;
            const nameA = args[1] as string;
            const nameB = args[2] as string;
            const createdAt = args[3] as number;
            // /pair/create の INSERT ... SELECT は、両 nonce が同じ claim 値かを条件にする。
            if (sql.includes('SELECT')) {
              const rowA = nonces.get(args[4] as string);
              const rowB = nonces.get(args[5] as string);
              if (!rowA || !rowB || rowA.nonce !== args[6] || rowB.nonce !== args[6]) return 0;
            }
            pairs.set(pairId, { nameA, nameB, createdAt });
            return 1;
          }
          if (isPairUpdate) {
            const pairId = args[3] as string;
            if (!pairs.has(pairId)) return 0;
            pairs.set(pairId, { nameA: args[0] as string, nameB: args[1] as string, createdAt: args[2] as number });
            return 1;
          }
          if (isPairDelete) {
            const existed = pairs.delete(args[0] as string);
            return existed ? 1 : 0;
          }
          if (!isDeleteNonce) return 0;
          if (sql.includes('sid IN')) {
            const claim = args[2] as string;
            let changes = 0;
            for (const sid of [args[0] as string, args[1] as string]) {
              if (nonces.get(sid)?.nonce === claim) {
                nonces.delete(sid);
                changes += 1;
              }
            }
            return changes;
          }
          const sid = args[0] as string;
          const row = nonces.get(sid);
          if (!row) return 0;
          if (isConditionalDelete && row.nonce !== (args[1] as string)) return 0;
          nonces.delete(sid);
          return 1;
        };
        return {
          first: async <T>(): Promise<T | null> => {
            const sid = args[0] as string;
            if (isSessions) {
              const session = sessions.get(sid);
              if (!session && !publicKeys.has(sid)) return null;
              return {
                display_name: session?.displayName ?? '',
                public_key: session?.publicKey ?? publicKeys.get(sid) ?? '',
              } as unknown as T;
            }
            if (isPairSelect) {
              const pairId = sid;
              const pair = pairs.get(pairId);
              if (!pair) return null;
              return { pair_id: pairId, name_a: pair.nameA, name_b: pair.nameB, created_at: pair.createdAt } as unknown as T;
            }
            const row = nonces.get(sid);
            if (!row) return null;
            return { nonce: row.nonce, created_at: row.createdAt } as unknown as T;
          },
          // batch() から実行される DELETE/INSERT/UPDATE を反映する擬似 statement。
          __run: apply,
          run: async () => ({ success: true, meta: { changes: apply() } }),
        };
      },
    };
  }

  /** env.DB.batch(stmts) 相当。prepare().bind() が返す擬似 statement の __run を順に適用し、
   *  D1 と同じく meta.changes を返す。 */
  async batch(stmts: Array<{ __run?: () => number }>): Promise<unknown[]> {
    return stmts.map((s) => ({ success: true, meta: { changes: s.__run?.() ?? 0 } }));
  }
}

const SECRET = 'test-cf-hmac-secret-0123456789';
const HOUR_MS = 60 * 60 * 1000;

function makeEnv(db: FakeD1): Env {
  return { DB: db, SESSION_HMAC_SECRET: SECRET } as unknown as Env;
}

async function bearerFor(deviceId: string, env: Env): Promise<string> {
  return mintSessionToken(deviceId, 3600, env);
}

function mkRequest(body: unknown, token?: string): Request {
  const headers: Record<string, string> = { 'content-type': 'application/json' };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  return new Request('https://relay.test/pair/link', { method: 'POST', headers, body: JSON.stringify(body) });
}

describe('handlePairLink', () => {
  beforeEach(() => {
    notifyInboxMock.mockClear();
  });

  it('正常系: 相手セッションがアクティブなら 200 + pairingId + 両者へ notifyInbox', async () => {
    const db = new FakeD1();
    db.setSessionActive(B, Date.now());
    const env = makeEnv(db);
    const token = await bearerFor(A, env);

    const res = await handlePairLink(mkRequest({ sidB: B, nameA: 'PC-A', nameB: 'PC-B' }, token), env);
    expect(res.status).toBe(200);
    const j = (await res.json()) as { ok: boolean; pairingId: string };
    expect(j.ok).toBe(true);
    expect(j.pairingId).toBe(derivePairId(A, B));
    expect(db.hasPair(derivePairId(A, B))).toBe(true);
    expect(notifyInboxMock).toHaveBeenCalledTimes(2);
    const calledWith = notifyInboxMock.mock.calls.map((c) => c[1]);
    expect(calledWith).toEqual(expect.arrayContaining([A, B]));
  });

  it('bearer なしは 401 BAD_TOKEN', async () => {
    const db = new FakeD1();
    const env = makeEnv(db);
    const res = await handlePairLink(mkRequest({ sidB: B }), env);
    expect(res.status).toBe(401);
    expect(((await res.json()) as { error: string }).error).toBe('BAD_TOKEN');
  });

  it('sidB が自分自身 (sidA と同一) なら 400 SAME_SID（自分の inbox に自分をペアリングできない）', async () => {
    const db = new FakeD1();
    const env = makeEnv(db);
    const token = await bearerFor(A, env);
    const res = await handlePairLink(mkRequest({ sidB: A }, token), env);
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('SAME_SID');
  });

  it('sidB が 32hex でないなら 400 BAD_SID', async () => {
    const db = new FakeD1();
    const env = makeEnv(db);
    const token = await bearerFor(A, env);
    const res = await handlePairLink(mkRequest({ sidB: 'not-hex' }, token), env);
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('BAD_SID');
  });

  it('相手セッションが存在しないなら 404 SESSION_NOT_FOUND（attacker が架空 sid を叩いても何も起きない）', async () => {
    const db = new FakeD1(); // B のセッション未登録
    const env = makeEnv(db);
    const token = await bearerFor(A, env);
    const res = await handlePairLink(mkRequest({ sidB: B }, token), env);
    expect(res.status).toBe(404);
    expect(((await res.json()) as { error: string }).error).toBe('SESSION_NOT_FOUND');
    expect(notifyInboxMock).not.toHaveBeenCalled();
  });

  it('相手セッションが 1h 超過なら 401 EXPIRED_SESSION', async () => {
    const db = new FakeD1();
    db.setSessionActive(B, Date.now() - HOUR_MS - 1000);
    const env = makeEnv(db);
    const token = await bearerFor(A, env);
    const res = await handlePairLink(mkRequest({ sidB: B }, token), env);
    expect(res.status).toBe(401);
    expect(((await res.json()) as { error: string }).error).toBe('EXPIRED_SESSION');
  });

  it('攻撃者は自分以外の deviceId を sidA として詐称できない (cfToken の claims が sidA の唯一の源)', async () => {
    // 攻撃者 (deviceId=attacker) の正当な bearer で、被害者 victim を sidB に指定して呼んでも、
    // pairingId・event は常に sidA=attacker (claims.deviceId) で導出される。
    // 「sidA=victim, sidB=他人」という詐称ペアを victim 抜きで成立させる経路は存在しない。
    const attacker = 'c'.repeat(32);
    const victim = B;
    const db = new FakeD1();
    db.setSessionActive(victim, Date.now());
    const env = makeEnv(db);
    const token = await bearerFor(attacker, env);

    const res = await handlePairLink(mkRequest({ sidB: victim }, token), env);
    expect(res.status).toBe(200);
    const j = (await res.json()) as { pairingId: string };
    expect(j.pairingId).toBe(derivePairId(attacker, victim));
    // event の sidA は claims (= attacker) 固定。victim が「ペアリングを申し込まれた」ことが
    // PairingDetected として両者に通知される（通常のペアリング成立と同じ挙動で、ghost peer 注入ではない）。
  });

  // rere レビュー #C-32 の回帰テスト。
  // ペア成立イベントに載る公開鍵は PairSecret (ECDH ルート鍵) の導出元なので、
  // 呼び出し元が申告した pkA/pkB を採用すると鍵配送の完全性が呼び出し元依存になる。
  // 必ず D1 sessions.public_key (bearer 本人が /pair/session で登録した権威データ) を使う。
  it('#C-32: body の pkA/pkB を無視し、D1 sessions.public_key を両者へ配る', async () => {
    const db = new FakeD1();
    db.setSessionActive(B, Date.now());
    db.setSessionPublicKey(A, 'AUTHORITATIVE_PK_A');
    db.setSessionPublicKey(B, 'AUTHORITATIVE_PK_B');
    const env = makeEnv(db);
    const token = await bearerFor(A, env);

    const res = await handlePairLink(
      mkRequest({ sidB: B, pkA: 'ATTACKER_PK_A', pkB: 'ATTACKER_PK_B' }, token),
      env,
    );
    expect(res.status).toBe(200);

    expect(notifyInboxMock).toHaveBeenCalledTimes(2);
    for (const call of notifyInboxMock.mock.calls) {
      const event = call[2] as { pkA: string; pkB: string };
      expect(event.pkA).toBe('AUTHORITATIVE_PK_A');
      expect(event.pkB).toBe('AUTHORITATIVE_PK_B');
    }
  });

  it('#C-32: sessions 行が無くても申告値は使わず空の公開鍵で成立させる (可用性優先・平文フォールバック)', async () => {
    const db = new FakeD1();
    db.setSessionActive(B, Date.now()); // public_key は未登録
    const env = makeEnv(db);
    const token = await bearerFor(A, env);

    const res = await handlePairLink(mkRequest({ sidB: B, pkA: 'ATTACKER_PK_A', pkB: 'ATTACKER_PK_B' }, token), env);
    expect(res.status).toBe(200);

    const event = notifyInboxMock.mock.calls[0][2] as { pkA: string; pkB: string };
    expect(event.pkA).toBe('');
    expect(event.pkB).toBe('');
  });

  it('RATELIMIT_DEVICE が枯渇していたら 429 DEVICE_RATE_LIMIT を返し notifyInbox を呼ばない (sidB 総当たり / 通知スパム対策)', async () => {
    const db = new FakeD1();
    db.setSessionActive(B, Date.now());
    const baseEnv = makeEnv(db);
    const token = await bearerFor(A, baseEnv);
    const limitSpy = vi.fn(async () => ({ success: false }));
    const env = { ...baseEnv, RATELIMIT_DEVICE: { limit: limitSpy } } as unknown as Env;

    const res = await handlePairLink(mkRequest({ sidB: B }, token), env);
    expect(res.status).toBe(429);
    expect(((await res.json()) as { error: string }).error).toBe('DEVICE_RATE_LIMIT');
    expect(limitSpy).toHaveBeenCalledWith({ key: A });
    expect(notifyInboxMock).not.toHaveBeenCalled();
  });
});

// ------------------ pairs 台帳 mode gate / server timestamp ------------------

function pairRequest(pairId: string, body: unknown, token: string): Request {
  return new Request(`https://relay.test/pairs/${pairId}`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify(body),
  });
}

describe('handlePairs PUT の段階移行', () => {
  it('required では未登録 pair の legacy PUT を 409 PAIR_PROOF_REQUIRED で拒否する', async () => {
    const db = new FakeD1();
    const env = { ...makeEnv(db), PAIR_LEDGER_MODE: 'required' } as unknown as Env;
    const token = await bearerFor(A, env);
    const res = await handlePairs(pairRequest(derivePairId(A, B), { nameA: 'A', nameB: 'B', createdAt: 1 }, token), env, new URL(`https://relay.test/pairs/${derivePairId(A, B)}`));

    expect(res.status).toBe(409);
    expect(((await res.json()) as { error: string }).error).toBe('PAIR_PROOF_REQUIRED');
    expect(db.hasPair(derivePairId(A, B))).toBe(false);
  });

  it('transition では legacy backfill を許可するが rate limit を消費し、createdAt は server 時刻にする', async () => {
    const db = new FakeD1();
    const limit = vi.fn(async () => ({ success: true }));
    const env = { ...makeEnv(db), RATELIMIT_DEVICE: { limit } } as unknown as Env;
    const token = await bearerFor(A, env);
    const pairId = derivePairId(A, B);
    const url = new URL(`https://relay.test/pairs/${pairId}`);
    const res = await handlePairs(pairRequest(pairId, { nameA: 'A', nameB: 'B', createdAt: 1 }, token), env, url);

    expect(res.status).toBe(200);
    expect(limit).toHaveBeenCalledWith({ key: A });
    expect(db.getPair(pairId)?.createdAt).toBeGreaterThan(1);
  });

  it('required では既存行の UPDATE は許可し、申告 createdAt を保存しない', async () => {
    const db = new FakeD1();
    const pairId = derivePairId(A, B);
    db.setPair(pairId, 'old-A', 'old-B', 100);
    const env = { ...makeEnv(db), PAIR_LEDGER_MODE: 'required' } as unknown as Env;
    const token = await bearerFor(A, env);
    const url = new URL(`https://relay.test/pairs/${pairId}`);
    const res = await handlePairs(pairRequest(pairId, { nameA: 'new-A', nameB: 'new-B', createdAt: 1 }, token), env, url);

    expect(res.status).toBe(200);
    expect(db.getPair(pairId)?.nameA).toBe('new-A');
    expect(db.getPair(pairId)?.createdAt).toBeGreaterThan(1);
  });

  it('pairs PUT は非 canonical pairId を拒否する', async () => {
    const db = new FakeD1();
    const env = makeEnv(db);
    const token = await bearerFor(B, env);
    const reverse = `${B}_${A}`;
    const url = new URL(`https://relay.test/pairs/${reverse}`);
    const res = await handlePairs(pairRequest(reverse, {}, token), env, url);
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('BAD_PAIR_ID');
  });

  it('明示された不正な PAIR_LEDGER_MODE は transition に降格せず 503', async () => {
    const db = new FakeD1();
    const env = { ...makeEnv(db), PAIR_LEDGER_MODE: 'typo' } as unknown as Env;
    const token = await bearerFor(A, env);
    const pairId = derivePairId(A, B);
    const url = new URL(`https://relay.test/pairs/${pairId}`);
    const res = await handlePairs(pairRequest(pairId, {}, token), env, url);

    expect(res.status).toBe(503);
    expect(((await res.json()) as { error: string }).error).toBe('PAIR_LEDGER_MISCONFIGURED');
    expect(db.hasPair(pairId)).toBe(false);
  });
});

// ------------------ /pair/session rate limit / input bounds ------------------

describe('handlePairSession', () => {
  function sessionRequest(body: unknown, token: string): Request {
    return new Request('https://relay.test/pair/session', {
      method: 'POST',
      headers: { 'content-type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify(body),
    });
  }

  it('本人 deviceId を key に RATELIMIT_SESSION を消費して登録する', async () => {
    const db = new FakeD1();
    const limit = vi.fn(async () => ({ success: true }));
    const env = { ...makeEnv(db), RATELIMIT_SESSION: { limit } } as unknown as Env;
    const token = await bearerFor(A, env);
    const res = await handlePairSession(
      sessionRequest({ displayName: 'PC', publicKey: 'pk', pairingNonce: 'c'.repeat(32) }, token),
      env,
      new URL('https://relay.test/pair/session'),
    );

    expect(res.status).toBe(200);
    expect(limit).toHaveBeenCalledWith({ key: A });
  });

  it('displayName/publicKey の上限超過は D1 を書き込まず 400 にする', async () => {
    const db = new FakeD1();
    const env = makeEnv(db);
    const token = await bearerFor(A, env);
    const overName = await handlePairSession(
      sessionRequest({ displayName: 'x'.repeat(129), publicKey: '', pairingNonce: 'c'.repeat(32) }, token),
      env,
      new URL('https://relay.test/pair/session'),
    );
    expect(overName.status).toBe(400);
    const overKey = await handlePairSession(
      sessionRequest({ displayName: '', publicKey: 'x'.repeat(257), pairingNonce: 'd'.repeat(32) }, token),
      env,
      new URL('https://relay.test/pair/session'),
    );
    expect(overKey.status).toBe(400);
  });
});

// ------------------ handlePairCreate rate limit (bearer 不要の公開エンドポイント) ------------------

describe('handlePairCreate rate limit', () => {
  it('RATELIMIT_IP が枯渇していたら 429 IP_RATE_LIMIT を返し D1 へ問い合わせない', async () => {
    const db = new FakeD1();
    const prepareSpy = vi.spyOn(db, 'prepare');
    const limitSpy = vi.fn(async () => ({ success: false }));
    const env = { DB: db, RATELIMIT_IP: { limit: limitSpy } } as unknown as Env;
    const req = new Request('https://relay.test/pair/create', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'CF-Connecting-IP': '1.2.3.4' },
      body: JSON.stringify({ sidA: A, sidB: B, nonceA: 'x'.repeat(32), nonceB: 'y'.repeat(32) }),
    });

    const res = await handlePairCreate(req, env);
    expect(res.status).toBe(429);
    expect(((await res.json()) as { error: string }).error).toBe('IP_RATE_LIMIT');
    expect(limitSpy).toHaveBeenCalledWith({ key: '1.2.3.4' });
    expect(prepareSpy).not.toHaveBeenCalled();
  });
});

// ------------------ handlePairCreate の nonce 単回使用 ------------------

describe('handlePairCreate の nonce 消費', () => {
  const NA = 'a1'.repeat(16);
  const NB = 'b2'.repeat(16);

  function createReq(nonceA: string, nonceB: string): Request {
    return createReqFor(A, B, nonceA, nonceB);
  }

  function createReqFor(sidA: string, sidB: string, nonceA: string, nonceB: string): Request {
    return new Request('https://relay.test/pair/create', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ sidA, sidB, nonceA, nonceB, nameA: 'PC-A', nameB: 'PC-B' }),
    });
  }

  beforeEach(() => {
    notifyInboxMock.mockClear();
  });

  it('成立したら両 sid の pairing_nonces を削除する (server 権威の単回使用)', async () => {
    const db = new FakeD1();
    db.setSessionActive(A, Date.now(), NA);
    db.setSessionActive(B, Date.now(), NB);
    const env = makeEnv(db);

    const res = await handlePairCreate(createReq(NA, NB), env);
    expect(res.status).toBe(200);
    expect(((await res.json()) as { pairingId: string }).pairingId).toBe(derivePairId(A, B));
    expect(notifyInboxMock).toHaveBeenCalledTimes(2);
    expect(db.hasNonce(A)).toBe(false);
    expect(db.hasNonce(B)).toBe(false);
    const pair = db.getPair(derivePairId(A, B));
    expect(pair).toBeDefined();
    expect(pair?.createdAt).toBeGreaterThan(Date.now() - 5_000);
  });

  it('同じ nonce の再送 (撮影された QR のリプレイ) は 404 になり inbox へ再 push しない', async () => {
    const db = new FakeD1();
    db.setSessionActive(A, Date.now(), NA);
    db.setSessionActive(B, Date.now(), NB);
    const env = makeEnv(db);

    expect((await handlePairCreate(createReq(NA, NB), env)).status).toBe(200);
    notifyInboxMock.mockClear();

    const replay = await handlePairCreate(createReq(NA, NB), env);
    expect(replay.status).toBe(404);
    expect(((await replay.json()) as { error: string }).error).toBe('SESSION_NOT_FOUND');
    expect(notifyInboxMock).not.toHaveBeenCalled();
  });

  it('nonce 不一致では消費しない (失敗リクエストで正規の nonce を潰さない)', async () => {
    const db = new FakeD1();
    db.setSessionActive(A, Date.now(), NA);
    db.setSessionActive(B, Date.now(), NB);
    const env = makeEnv(db);

    const res = await handlePairCreate(createReq(NA, 'c3'.repeat(16)), env);
    expect(res.status).toBe(401);
    expect(((await res.json()) as { error: string }).error).toBe('INVALID_NONCE_MATCH');
    expect(db.hasNonce(A)).toBe(true);
    expect(db.hasNonce(B)).toBe(true);
  });

  it('並列の二重 create は片方だけが成立する (単回使用が並列でも破れない)', async () => {
    const db = new FakeD1();
    db.setSessionActive(A, Date.now(), NA);
    db.setSessionActive(B, Date.now(), NB);
    const env = makeEnv(db);

    // 検証(SELECT) と消費(DELETE) が非原子的だった旧実装では、両方が verifyNonce を通過して
    // 双方が notifyPairEstablished を実行できた（QR 撮影直後の二重送信 / 攻撃者の同時リプレイ）。
    // 条件付き DELETE の compare-and-swap により、消せた 1 本だけが成立する。
    const [r1, r2] = await Promise.all([
      handlePairCreate(createReq(NA, NB), env),
      handlePairCreate(createReq(NA, NB), env),
    ]);

    const statuses = [r1.status, r2.status].sort();
    expect(statuses[0]).toBe(200);
    expect(statuses[1]).toBeGreaterThanOrEqual(400);
    // ペア成立 push は勝った 1 本ぶん (両者へ 1 回ずつ) だけ
    expect(notifyInboxMock).toHaveBeenCalledTimes(2);
    expect(db.hasNonce(A)).toBe(false);
    expect(db.hasNonce(B)).toBe(false);
  });

  it('同じ sid を別の相手へ使う並列 create でも台帳は勝者 1 組だけ残す', async () => {
    const sidC = 'c'.repeat(32);
    const nonceC = 'c3'.repeat(16);
    const db = new FakeD1();
    db.setSessionActive(A, Date.now(), NA);
    db.setSessionActive(B, Date.now(), NB);
    db.setSessionActive(sidC, Date.now(), nonceC);
    const env = makeEnv(db);

    const [ab, ac] = await Promise.all([
      handlePairCreate(createReqFor(A, B, NA, NB), env),
      handlePairCreate(createReqFor(A, sidC, NA, nonceC), env),
    ]);

    expect([ab.status, ac.status].filter((status) => status === 200)).toHaveLength(1);
    expect([db.hasPair(derivePairId(A, B)), db.hasPair(derivePairId(A, sidC))]
      .filter(Boolean)).toHaveLength(1);
    expect(notifyInboxMock).toHaveBeenCalledTimes(2);
    // 負け側の相手 nonce は claim されず、別の正規ペアリングへ再利用できる。
    expect(db.hasNonce(B) || db.hasNonce(sidC)).toBe(true);
  });
});

// ------------------ 不正 JSON ボディ (500 ではなく 400 INVALID_JSON) ------------------

describe('壊れた JSON ボディ', () => {
  function badBodyRequest(url: string, token?: string): Request {
    const headers: Record<string, string> = { 'content-type': 'application/json' };
    if (token) headers['Authorization'] = `Bearer ${token}`;
    return new Request(url, { method: 'POST', headers, body: 'not-json' });
  }

  it('/pair/link は 400 INVALID_JSON を返す (CF 既定の 500 に落ちない)', async () => {
    const db = new FakeD1();
    const env = makeEnv(db);
    const token = await bearerFor(A, env);
    const res = await handlePairLink(badBodyRequest('https://relay.test/pair/link', token), env);
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('INVALID_JSON');
  });

  it('/pair/create は 400 INVALID_JSON を返し D1 へ問い合わせない', async () => {
    const db = new FakeD1();
    const prepareSpy = vi.spyOn(db, 'prepare');
    const env = makeEnv(db);
    const res = await handlePairCreate(badBodyRequest('https://relay.test/pair/create'), env);
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('INVALID_JSON');
    expect(prepareSpy).not.toHaveBeenCalled();
  });
});
