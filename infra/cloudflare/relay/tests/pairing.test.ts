/**
 * CF 単独完結移行 Step 2 / Step 7: pairing-routes の単体テスト。
 *
 * - derivePairId: C# ConnectionService.GeneratePairId (string.Compare Ordinal 昇順 + "_" 連結) と一致することを固定する。
 *   不一致だと A 側と B 側で別の pairId を導出し、signaling DO が別インスタンスに分裂して接続不能になる。
 * - handlePairLink: PC コード貼付ペアリング (bearer 必須 + 相手セッション存在のみ要求) の認可ロジックを固定する。
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { derivePairId, handlePairCreate, handlePairLink } from '../src/pairing-routes';
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
  private publicKeys = new Map<string, string>(); // sid -> sessions.public_key

  setSessionActive(sid: string, createdAt: number, nonce = 'n'.repeat(32)) {
    this.nonces.set(sid, { nonce, createdAt });
  }

  /** pairing_nonces 行が残っているか（/pair/create の nonce 消費を検証する）。 */
  hasNonce(sid: string): boolean {
    return this.nonces.has(sid);
  }

  /** rere #C-32 用: sessions.public_key（サーバーが持つ権威データ）を仕込む。 */
  setSessionPublicKey(sid: string, publicKey: string) {
    this.publicKeys.set(sid, publicKey);
  }

  prepare(sql: string) {
    const nonces = this.nonces;
    const publicKeys = this.publicKeys;
    // sessions と pairing_nonces で返す列が違うので SQL で振り分ける
    const isSessions = sql.includes('FROM sessions');
    const isDeleteNonce = sql.includes('DELETE FROM pairing_nonces');
    // 条件付き DELETE (WHERE sid=? AND nonce=?) は compare-and-swap。nonce 不一致なら消えない。
    const isConditionalDelete = isDeleteNonce && sql.includes('nonce=?');
    return {
      bind: (...args: unknown[]) => ({
        first: async <T>(): Promise<T | null> => {
          const sid = args[0] as string;
          if (isSessions) {
            if (!publicKeys.has(sid)) return null;
            return { public_key: publicKeys.get(sid)! } as unknown as T;
          }
          const row = nonces.get(sid);
          if (!row) return null;
          return { nonce: row.nonce, created_at: row.createdAt } as unknown as T;
        },
        // batch() から実行される DELETE を反映するための擬似 statement。影響行数を返す
        // （/pair/create の単回使用は「消せた側だけが成立する」ので changes が判定の実体）。
        __run: (): number => {
          if (!isDeleteNonce) return 0;
          const sid = args[0] as string;
          const row = nonces.get(sid);
          if (!row) return 0;
          if (isConditionalDelete && row.nonce !== (args[1] as string)) return 0;
          nonces.delete(sid);
          return 1;
        },
      }),
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
    return new Request('https://relay.test/pair/create', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ sidA: A, sidB: B, nonceA, nonceB, nameA: 'PC-A', nameB: 'PC-B' }),
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
