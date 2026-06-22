/**
 * Ferry WebSocket リレー (Cloudflare Workers + Durable Objects)
 *
 * 旧 VPS Node.js リレー (wss://1llum1n4t1.net/ferry-relay) の置き換え。
 * Durable Objects Hibernation API を使うことで、ペアアイドル中はメモリ 0 / 課金 0 で待機する。
 *
 * クライアント側プロトコル (src/Ferry/Infrastructure/WebSocketRelayTransport.cs と一致させること):
 *   1. wss://relay.ferry.nephilim.jp/ferry-relay?pairId=<id>&role=<offer|answer> へ接続
 *   2. リレーは 2 peer 揃ったら両方に "ready" テキストフレームを送る
 *   3. それ以降のバイナリフレームは無条件で相手側へパススルー
 *      (WebSocket メッセージ上限は 32 MiB / Workers 仕様。2025-10-31 に 1 MiB から引上げ。
 *       Ferry の 64KB チャンクには十分余裕。受信側ドレイン未了分は DO メモリ 128MB 内にキューされ、
 *       backpressure は実装されない workerd#988 → 送信側のアプリ層フロー制御 FileFlowAck で頭打ちにする)
 *   4. 片側が close したら相手側も 1001 で close
 *
 * pairId は Workers Secret `SALT` と連結して SHA-256 ハッシュ化してから DO ID に使う。
 * 生 pairId を `idFromName` に直接渡すと、Firebase ログ等で pairId が漏れた際に
 * 任意の第三者が同じルームに到達できてしまうため。
 */

export interface Env {
  RELAY: DurableObjectNamespace;
  /** signaling DO (PairDO)。CF 単独完結移行 Step 1。pairId-keyed で offers/answers/endpoints/probes を保持。 */
  PAIR: DurableObjectNamespace;
  /** device DO (DeviceDO)。CF 単独完結移行 Step 2。deviceId-keyed で presence + pairing inbox(WS) を保持。 */
  DEVICE: DurableObjectNamespace;
  /** ペア台帳 D1 (sessions/pairing_nonces/pairs)。CF 単独完結移行 Step 2。 */
  DB: D1Database;
  /** pairId ハッシュ化用ソルト。`wrangler secret put SALT` で登録する。 */
  SALT: string;

  // === #D-001a Phase B: Firebase Custom Token Auth (auth.ts) ===
  /** deviceId ↔ pubKeySpki first-write-wins binding KV (wrangler kv namespace create で作成)。 */
  DEVICE_KEY_BINDING: KVNamespace;
  /** Firebase SA PKCS#8 PEM (改行込み)。`cat sa.pem | wrangler secret put FIREBASE_PRIVATE_KEY`。 */
  FIREBASE_PRIVATE_KEY: string;
  /** Firebase SA client_email。`wrangler secret put FIREBASE_CLIENT_EMAIL`。 */
  FIREBASE_CLIENT_EMAIL: string;
  /** Firebase Realtime DB URL (vars)。例: https://ferry-edf09-default-rtdb.firebaseio.com */
  FIREBASE_DATABASE_URL: string;

  // === CF 単独完結 (docs/design/cf-only-migration.md) ===
  /** cfToken (自前 HMAC bearer) 署名用シークレット。`wrangler secret put SESSION_HMAC_SECRET`。
   *  未設定なら /auth/token は cfToken を発行せず Firebase 経路のみで動作する (dual-path)。 */
  SESSION_HMAC_SECRET: string;

  /** Rate limit bindings (unsafe.bindings)。 */
  RATELIMIT_IP?: RateLimit;
  RATELIMIT_DEVICE?: RateLimit;
  RATELIMIT_SESSION?: RateLimit;
}

/** Cloudflare Rate Limit binding の最小型（@cloudflare/workers-types の正式型と互換）。 */
interface RateLimit {
  limit(opts: { key: string }): Promise<{ success: boolean }>;
}

// CF 単独完結移行: DO クラスを entry module から re-export (Workers の DO クラス公開要件)。
export { PairDO } from './pairdo';
export { DeviceDO } from './devicedo';

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);

    // ヘルスチェック (curl https://relay.ferry.nephilim.jp/health で疎通確認)
    if (url.pathname === '/health') {
      return new Response('OK', { status: 200 });
    }

    // #D-001a Phase B: Firebase Custom Token Auth エンドポイント
    //
    // Codex P1 指摘: Bridge は https://ferry-edf09.web.app から POST application/json を投げるため、
    // ブラウザは事前に CORS preflight (OPTIONS) を送る。本 Worker が OPTIONS を扱わないと
    // Access-Control-Allow-* が返らず Bridge から /pair/token が呼べない (本番 /auth/token も
    // 直接ブラウザ呼出を想定するならガード必要)。Allowed origins は Firebase Hosting 由来のみ。
    if (url.pathname === '/auth/token' || url.pathname === '/pair/token') {
      if (req.method === 'OPTIONS') {
        return corsPreflightResponse(req);
      }
      if (req.method === 'POST') {
        const { handleAuthToken, handlePairToken } = await import('./auth');
        const handler = url.pathname === '/auth/token' ? handleAuthToken : handlePairToken;
        const resp = await handler(req, env);
        return withCorsHeaders(resp, req);
      }
      return new Response('Method Not Allowed', { status: 405, headers: { 'allow': 'POST, OPTIONS' } });
    }

    // CF 単独完結移行 Step 1: signaling HTTP ルート (`/sig/{pairId}/...`)。
    // 認可 (cfToken Bearer + pairId 当事者 + sender キー強制) は handleSignaling 内で完結し PairDO へ委譲。
    // C# desktop クライアントからの呼出なので CORS は不要 (ブラウザ経由は Bridge の /pair/* のみ)。
    if (url.pathname.startsWith('/sig/')) {
      const { handleSignaling } = await import('./signaling-routes');
      return handleSignaling(req, env, url);
    }

    // CF 単独完結移行 Step 2: presence (poll) と pairing inbox (WS push)。
    // /inbox は WebSocket upgrade なので、下の relay 用 WS 処理 (pairId 必須) より前に捌く。
    if (url.pathname === '/inbox') {
      const { handleInbox } = await import('./device-routes');
      return handleInbox(req, env);
    }
    if (url.pathname.startsWith('/presence/')) {
      const { handlePresence } = await import('./device-routes');
      return handlePresence(req, env, url);
    }

    // CF 単独完結移行 Step 2: ペアリング / ペア台帳。
    // /pair/create は Bridge(browser) から呼ぶので CORS 対応 (dual-path 中は web.app オリジン)。
    if (url.pathname === '/pair/create') {
      if (req.method === 'OPTIONS') return corsPreflightResponse(req);
      if (req.method === 'POST') {
        const { handlePairCreate } = await import('./pairing-routes');
        return withCorsHeaders(await handlePairCreate(req, env), req);
      }
      return new Response('Method Not Allowed', { status: 405, headers: { allow: 'POST, OPTIONS' } });
    }
    // /pair/session と /pairs/* は PC (bearer) からのみ。CORS 不要。
    if (url.pathname.startsWith('/pair/session')) {
      const { handlePairSession } = await import('./pairing-routes');
      return handlePairSession(req, env, url);
    }
    if (url.pathname.startsWith('/pairs/')) {
      const { handlePairs } = await import('./pairing-routes');
      return handlePairs(req, env, url);
    }

    // WebSocket 以外は拒否
    if (req.headers.get('Upgrade') !== 'websocket') {
      return new Response('Expected websocket', { status: 426 });
    }

    // クライアント側は ?pairId=...&role=... を必ず送る契約。欠落は 400。
    const pairId = url.searchParams.get('pairId');
    if (!pairId) {
      return new Response('Missing pairId', { status: 400 });
    }
    const role = url.searchParams.get('role') ?? 'unknown';

    // pairId をハッシュ化して DO ID に使う (生 pairId 直入れによる横入りを防ぐ)
    const idStr = await hashPairId(pairId, env.SALT);
    const doId = env.RELAY.idFromName(idStr);
    const stub = env.RELAY.get(doId);

    // role はクエリで DO に持ち越す
    const forwarded = new Request(req.url + (url.search ? '&' : '?') + `__role=${encodeURIComponent(role)}`, req);
    return stub.fetch(forwarded);
  },
};

/**
 * CORS preflight / レスポンスへのヘッダ付与。
 *
 * Allowed origins: 開発の利便で Firebase Hosting の通常ドメイン (web.app / firebaseapp.com) を許可。
 * 不一致なら ACAO を付けない (= ブラウザがブロック)。Credentials は使わないので Allow-Credentials は不要。
 */
const ALLOWED_ORIGIN_SUFFIXES = ['.web.app', '.firebaseapp.com'];

function resolveAllowedOrigin(req: Request): string | null {
  const origin = req.headers.get('Origin');
  if (!origin) return null;
  try {
    const o = new URL(origin);
    if (o.protocol !== 'https:') return null;
    if (ALLOWED_ORIGIN_SUFFIXES.some((sfx) => o.hostname.endsWith(sfx))) return origin;
  } catch {
    // invalid URL → no CORS
  }
  return null;
}

function corsPreflightResponse(req: Request): Response {
  const allowed = resolveAllowedOrigin(req);
  const headers: Record<string, string> = {
    'access-control-allow-methods': 'POST, OPTIONS',
    'access-control-allow-headers': 'content-type',
    'access-control-max-age': '600',
    'vary': 'Origin',
  };
  if (allowed) headers['access-control-allow-origin'] = allowed;
  return new Response(null, { status: 204, headers });
}

function withCorsHeaders(resp: Response, req: Request): Response {
  const allowed = resolveAllowedOrigin(req);
  if (!allowed) return resp;
  // Response は immutable なので新規生成して header を足す
  const headers = new Headers(resp.headers);
  headers.set('access-control-allow-origin', allowed);
  headers.set('vary', 'Origin');
  return new Response(resp.body, { status: resp.status, statusText: resp.statusText, headers });
}

export async function hashPairId(pairId: string, salt: string): Promise<string> {
  const data = new TextEncoder().encode(pairId + '|' + salt);
  const buf = await crypto.subtle.digest('SHA-256', data);
  return Array.from(new Uint8Array(buf))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('');
}

/**
 * リレー本体。1 ペア (= 同じハッシュ化 pairId) ごとに 1 インスタンス。
 * `state.acceptWebSocket` で受け付けた WebSocket は Hibernation 対応となり、
 * アイドル中は Worker が完全に休眠する (課金 0)。復帰時は webSocketMessage / Close / Error が呼ばれる。
 */
export class RelayDO {
  state: DurableObjectState;

  constructor(state: DurableObjectState, _env: Env) {
    this.state = state;
  }

  async fetch(req: Request): Promise<Response> {
    // rere #F-001: runbook / relay-healthcheck.yml が `wrangler tail` を復旧手順の柱に据えているため、
    // 接続ライフサイクル (join / ready / 409 / close / error / 転送失敗) を tail で追えるようにログする。
    // 生 pairId は出さず、ハッシュ化済み DO id の前方 8 桁 + role のみに留めて PII を増やさない。
    const room = this.state.id.toString().slice(0, 8);

    // 既存 peer 数チェック (Ferry は 1 ペアにつき 2 peer まで)。
    // readyState で生存 (OPEN/CONNECTING) のみを数える。クライアントの再接続/張り直しで
    // 古いソケットが CLOSING/CLOSED のまま getWebSockets に残っていると、生きた接続が 1 本でも
    // length>=2 となり正当な再接続を 409 で誤って弾く (送信側ログの '409' 事象)。half-dead を除外する。
    const live = this.state
      .getWebSockets()
      .filter((ws) => ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING);
    if (live.length >= 2) {
      console.log(`[relay] 409 pair-full room=${room} live=${live.length}`);
      return new Response('Pair already full', { status: 409 });
    }

    const url = new URL(req.url);
    const role = url.searchParams.get('__role') ?? 'unknown';
    console.log(`[relay] join room=${room} role=${role} live=${live.length}`);

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];

    // hibernation を効かせるため acceptWebSocket を使う (addEventListener は使わない)。
    // 第二引数のタグは getWebSockets() で再取得できる。role をタグとして持たせて
    // 将来的なメッセージフィルタやデバッグログで活用できるようにしておく。
    this.state.acceptWebSocket(server, [role]);

    // 2 peer 揃った瞬間に両方へ "ready" を送り、クライアントの WaitForReadyAsync を通過させる。
    // Codex P2 fix (第10弾 #5): admission filter (OPEN/CONNECTING) と整合させる。
    // 旧実装は unfiltered な getWebSockets() を `=== 2` で評価していたため、
    // reconnect で CLOSING/CLOSED な古い socket が DO に残っていると以下の race が起きていた:
    //   admission: live=1 (closed を除外) なので 2 人目を accept
    //   ready check: sockets.length === 3 (closed 1 + live 2) なので ready 不送出
    // 結果、両クライアントの WaitForReadyAsync が timeout する。
    // ready check も live filter された sockets で評価することでこの race を解消する。
    // 新規 accept 直後は readyState が CONNECTING の可能性があるので、改めて live を取り直す。
    const liveAfterAccept = this.state
      .getWebSockets()
      .filter((ws) => ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING);
    if (liveAfterAccept.length === 2) {
      console.log(`[relay] ready room=${room} (2 peers paired)`);
      for (const ws of liveAfterAccept) {
        try {
          ws.send('ready');
        } catch {
          /* 片側が即切断したケース。webSocketClose 側で相方も閉じるので無視 */
        }
      }
    }

    return new Response(null, { status: 101, webSocket: client });
  }

  /**
   * ピアからメッセージを受信したら相手側へ転送する。
   * バイナリフレームのみリレーし、テキストフレームは握りつぶす (制御目的の "ready" のみ DO 側で送る契約)。
   */
  async webSocketMessage(ws: WebSocket, msg: ArrayBuffer | string): Promise<void> {
    if (typeof msg === 'string') {
      // クライアント側はバイナリしか送らない契約。テキストが来たら不正としてドロップ。
      // rere #F-001: 不正テキストフレームの破棄は通常起きないのでログする（バイナリの正常リレーは
      // チャンク毎になるためログしない）。
      console.log(`[relay] drop text-frame room=${this.state.id.toString().slice(0, 8)}`);
      return;
    }
    for (const peer of this.state.getWebSockets()) {
      if (peer !== ws && peer.readyState === WebSocket.OPEN) {
        try {
          peer.send(msg);
        } catch (e) {
          // 受信側がフレーム処理中に詰まった場合は破棄。クライアント側の信頼性レイヤーで補償されるが、
          // rere #F-001: 黙って捨てると切断原因の切り分けが不能になるためログする。
          console.error(`[relay] relay-send failed room=${this.state.id.toString().slice(0, 8)}: ${e}`);
        }
      }
    }
  }

  /** 片側が切れたら相手側も同コードで close する (転送中の片側切断検知)。 */
  async webSocketClose(ws: WebSocket, _code: number, _reason: string, _wasClean: boolean): Promise<void> {
    console.log(`[relay] close room=${this.state.id.toString().slice(0, 8)} code=${_code} clean=${_wasClean}`);
    for (const peer of this.state.getWebSockets()) {
      if (peer !== ws) {
        try {
          peer.close(1001, 'Peer disconnected');
        } catch {
          /* 既に close 済みのケースは無視 */
        }
      }
    }
  }

  async webSocketError(ws: WebSocket, _error: unknown): Promise<void> {
    console.error(`[relay] error room=${this.state.id.toString().slice(0, 8)}: ${_error}`);
    for (const peer of this.state.getWebSockets()) {
      if (peer !== ws) {
        try {
          peer.close(1011, 'Peer errored');
        } catch {
          /* 同上 */
        }
      }
    }
  }
}
