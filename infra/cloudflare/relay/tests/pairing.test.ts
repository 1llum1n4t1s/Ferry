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
  private nonces = new Map<string, number>(); // sid -> created_at

  setSessionActive(sid: string, createdAt: number) {
    this.nonces.set(sid, createdAt);
  }

  prepare(_sql: string) {
    const nonces = this.nonces;
    return {
      bind: (...args: unknown[]) => ({
        first: async <T>(): Promise<T | null> => {
          const sid = args[0] as string;
          if (!nonces.has(sid)) return null;
          return { created_at: nonces.get(sid)! } as unknown as T;
        },
      }),
    };
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
