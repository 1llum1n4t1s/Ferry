/**
 * Ferry WebSocket リレー (Cloudflare Workers + Durable Objects)
 *
 * 旧 VPS Node.js リレー (wss://1llum1n4t1.net/ferry-relay) の置き換え。
 * Durable Objects Hibernation API を使うことで、ペアアイドル中はメモリ 0 / 課金 0 で待機する。
 *
 * クライアント側プロトコル (src/Ferry/Infrastructure/WebSocketRelayTransport.cs と一致させること):
 *   1. wss://relay.ferry.nephilim.jp/ferry-relay?pairId=<id>&role=<offer|answer> へ接続
 *   2. リレーは 2 peer 揃ったら両方に "ready" テキストフレームを送る
 *   3. それ以降のバイナリフレームは無条件で相手側へパススルー (最大 1 MB / Workers 仕様)
 *   4. 片側が close したら相手側も 1001 で close
 *
 * pairId は Workers Secret `SALT` と連結して SHA-256 ハッシュ化してから DO ID に使う。
 * 生 pairId を `idFromName` に直接渡すと、Firebase ログ等で pairId が漏れた際に
 * 任意の第三者が同じルームに到達できてしまうため。
 */

export interface Env {
  RELAY: DurableObjectNamespace;
  /** pairId ハッシュ化用ソルト。`wrangler secret put SALT` で登録する。 */
  SALT: string;
}

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);

    // ヘルスチェック (curl https://relay.ferry.nephilim.jp/health で疎通確認)
    if (url.pathname === '/health') {
      return new Response('OK', { status: 200 });
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

async function hashPairId(pairId: string, salt: string): Promise<string> {
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
    // 既存 peer 数チェック (Ferry は 1 ペアにつき 2 peer まで)
    const existing = this.state.getWebSockets();
    if (existing.length >= 2) {
      return new Response('Pair already full', { status: 409 });
    }

    const url = new URL(req.url);
    const role = url.searchParams.get('__role') ?? 'unknown';

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];

    // hibernation を効かせるため acceptWebSocket を使う (addEventListener は使わない)。
    // 第二引数のタグは getWebSockets() で再取得できる。role をタグとして持たせて
    // 将来的なメッセージフィルタやデバッグログで活用できるようにしておく。
    this.state.acceptWebSocket(server, [role]);

    // 2 peer 揃った瞬間に両方へ "ready" を送り、クライアントの WaitForReadyAsync を通過させる。
    const sockets = this.state.getWebSockets();
    if (sockets.length === 2) {
      for (const ws of sockets) {
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
      return;
    }
    for (const peer of this.state.getWebSockets()) {
      if (peer !== ws && peer.readyState === WebSocket.OPEN) {
        try {
          peer.send(msg);
        } catch {
          /* 受信側がフレーム処理中に詰まった場合は破棄。クライアント側の信頼性レイヤーで補償される */
        }
      }
    }
  }

  /** 片側が切れたら相手側も同コードで close する (転送中の片側切断検知)。 */
  async webSocketClose(ws: WebSocket, _code: number, _reason: string, _wasClean: boolean): Promise<void> {
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
