/**
 * Ferry device Durable Object (DeviceDO) — CF 単独完結移行 Step 2。
 *
 * deviceId-keyed。Firebase の 2 機能を 1 DO に集約する:
 *   1. presence (lastSeen/displayName/version) — オンライン検知。**poll で取得** (realtime 批判反映:
 *      presence は 60s 許容なので WS push 不要。ETag/304 で帯域も節約)。
 *   2. pairing inbox — QR ペア成立通知。**ここだけ WebSocket push** (QR スキャン直後に PC へ即反映する
 *      唯一 push の価値がある経路)。Hibernation でアイドル無課金。
 *
 * 認可は Worker (device-routes.ts) で完結。presence write/delete は本人のみ、read は任意認証済 (相手の
 * オンライン検知の正当用途)。inbox WS は token.deviceId の DO にしか繋がらない。notify は Worker 内部のみ
 * (公開ルートなし。/pair/create がペア成立時に両 sid の DeviceDO へ呼ぶ)。
 *
 * storage:
 *   presence = { lastSeen, displayName, version }
 *   inbox    = InboxEvent[]   (TTL/上限で prune。WS 未接続時の取りこぼし救済キュー)
 */

import { readJsonObject } from './http';

const INBOX_TTL_MS = 60 * 60 * 1000; // 1h
const INBOX_MAX = 50;
/** 通常 listener 1本 + pairing画面の一時 listener を許容しつつ、push fan-outを固定する。 */
export const INBOX_MAX_CONNECTIONS = 4;

/** rere レビュー #A2-09: presence に格納する自己申告文字列の上限。
 *  /auth/token の pubKeySpki (256 文字) と同じ思想で、格納側で長さを有界にする。 */
const MAX_DISPLAY_NAME_LEN = 128;
const MAX_VERSION_LEN = 32;

interface Presence {
  lastSeen: number;
  displayName: string;
  version: string;
}

/** inbox イベント。ペア成立 (FirebaseSignaling.PairingData 相当) と unpair 通知の両方を運ぶ。
 *  createdAt は TTL prune に必須。種別ごとの追加フィールド (sidA/nameA/... や type:'unpair') は任意。 */
type InboxEvent = { createdAt: number; [key: string]: unknown };

/** イベントの期限。壊れた保存値は TTL 対象外として扱い、無期限に残さない。 */
function inboxExpiresAt(event: InboxEvent): number | null {
  const createdAt = (event as { createdAt?: unknown } | null | undefined)?.createdAt;
  return typeof createdAt === 'number' && Number.isFinite(createdAt)
    ? createdAt + INBOX_TTL_MS
    : null;
}

function pruneInbox(events: InboxEvent[], now: number): InboxEvent[] {
  return events
    .filter((event) => {
      const expiresAt = inboxExpiresAt(event);
      return expiresAt !== null && expiresAt > now;
    })
    .slice(-INBOX_MAX);
}

export class DeviceDO {
  private state: DurableObjectState;

  constructor(state: DurableObjectState, _env: unknown) {
    this.state = state;
  }

  async fetch(req: Request): Promise<Response> {
    // inbox WebSocket upgrade
    if (req.headers.get('Upgrade') === 'websocket') {
      return this.openInbox();
    }

    const url = new URL(req.url);
    const segs = url.pathname.replace(/^\/+/, '').split('/').filter((s) => s.length > 0);
    const action = segs[0] ?? '';
    const method = req.method;

    try {
      switch (action) {
        case 'presence':
          if (segs[1] === 'last-seen') return await this.readLastSeen(req);
          if (method === 'POST') return await this.writePresence(req);
          if (method === 'DELETE') return await this.deletePresence();
          return await this.readPresence();
        case 'notify': // Worker 内部のみ (/pair/create から)
          return await this.notify(req);
        default:
          return json(400, { error: 'BAD_ACTION', action });
      }
    } catch (e) {
      // rere レビュー #C-13: PairDO と同じく、例外の実体をレスポンスボディにしか
      // 載せていなかったのでサーバ側に痕跡が残らなかった。構造化して残す。
      console.error('DeviceDO error', JSON.stringify({ action, method, error: String(e) }));
      return json(500, { error: 'DO_ERROR', message: String(e) });
    }
  }

  // ---- presence (poll) ----

  private async writePresence(req: Request): Promise<Response> {
    const parsed = await readJsonObject(req);
    if ('error' in parsed) return parsed.error;
    const body = parsed.value;
    // rere レビュー #A2-09: displayName / version は typeof チェックだけで長さ制限が無く、
    // 認証済みデバイスが巨大文字列を格納して DO 容量を食い、ピア側はそれを presence 経由で
    // 受け取って peers.json へ永続化・UI 描画していた。/auth/token の pubKeySpki が 256 文字
    // 上限を持つのと揃えて、格納側でも上限を掛ける（超過分は切り詰め＝既存の成功契約は保つ）。
    const clamp = (v: unknown, max: number): string =>
      typeof v === 'string' ? v.slice(0, max) : '';
    const p: Presence = {
      lastSeen: Date.now(), // server now (クライアント時計に依存しない)
      displayName: clamp(body.displayName, MAX_DISPLAY_NAME_LEN),
      version: clamp(body.version, MAX_VERSION_LEN),
    };
    await this.state.storage.put('presence', p);
    return json(200, { ok: true, lastSeen: p.lastSeen });
  }

  /** ETag/If-None-Match 対応の LastSeen 単独取得 (Firebase の帯域節約経路を踏襲)。 */
  private async readLastSeen(req: Request): Promise<Response> {
    const p = await this.state.storage.get<Presence>('presence');
    if (!p) return json(404, { error: 'NOT_FOUND' });
    const etag = `"${p.lastSeen}"`;
    if (req.headers.get('If-None-Match') === etag) {
      return new Response(null, { status: 304, headers: { etag } });
    }
    return new Response(JSON.stringify({ lastSeen: p.lastSeen }), {
      status: 200,
      headers: { 'content-type': 'application/json', etag },
    });
  }

  private async readPresence(): Promise<Response> {
    const p = await this.state.storage.get<Presence>('presence');
    if (!p) return json(404, { error: 'NOT_FOUND' });
    return json(200, p);
  }

  private async deletePresence(): Promise<Response> {
    await this.state.storage.delete('presence');
    return json(200, { ok: true });
  }

  // ---- pairing inbox (WebSocket push) ----

  private async openInbox(): Promise<Response> {
    const liveSockets = this.state.getWebSockets().filter(
      (ws) => ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING,
    );
    if (liveSockets.length >= INBOX_MAX_CONNECTIONS) {
      return json(429, { error: 'INBOX_CONNECTION_LIMIT' });
    }

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    // Hibernation 対応で accept (アイドル中は課金 0)。
    this.state.acceptWebSocket(server);

    // 接続時に未読 (TTL 内) を flush。重複は client 側 SeenPairingIds + start-time gate で dedupe する
    // (Firebase の subscribe-replay と同じ前提なので client 防御をそのまま使える)。
    const stored = await this.state.storage.get<InboxEvent[]>('inbox');
    const now = Date.now();
    const events = Array.isArray(stored) ? pruneInbox(stored, now) : [];
    // 接続時 flush は読むだけにせず、期限切れ・上限超過・壊れた保存値を実際に掃除する。
    // 全件 stale の場合は空配列を残さずキーを消す（presence は別キーなので保持される）。
    const needsPrune = !Array.isArray(stored)
      ? stored !== undefined
      : events.length !== stored.length;
    if (needsPrune) {
      if (events.length > 0) await this.state.storage.put('inbox', events);
      else await this.state.storage.delete('inbox');
    }
    // 旧デプロイ時に alarm 無しで保存された行も、次回接続時に cleanup を予約する。
    // 既存 alarm より早い期限があれば前倒しするが、通知時の「最も遅い期限」設定は崩さない。
    if (events.length > 0) await this.ensureInboxAlarm(events, 'earliest');
    for (const e of events) {
      try {
        server.send(JSON.stringify(e));
      } catch {
        /* 即切断は webSocketClose で処理 */
      }
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  /** Worker 内部呼出: ペア成立を inbox に積み、接続中の WS に即 push する。
   *  type=knock（接続ノック: offer/probe 書込の即時合図）は transient — storage に積まず
   *  接続中の WS にだけ送る。積むと高頻度ノックが INBOX_MAX(50) を溢れさせてペア成立イベントを
   *  押し出す上、次回 inbox 接続時の flush で stale ノックが replay される。 */
  private async notify(req: Request): Promise<Response> {
    const e = (await req.json()) as InboxEvent;
    const transient = e.type === 'knock';
    if (!transient) {
      const stored = await this.state.storage.get<InboxEvent[]>('inbox');
      const events = Array.isArray(stored) ? stored : [];
      const now = Date.now();
      // 同じ (pairingId, type) のイベントは積み増さず最新 1 件に畳む。
      // /pair/link は相手セッションが 1h アクティブな間なら何度でも成立でき（設計どおり: 相手の
      // nonce 値所有を要求しない認可モデル）、同一 sidA→sidB の繰り返しは常に同じ pairingId を生む。
      // 積み増すと RATELIMIT_DEVICE(30/60s) の範囲でも 2 分弱で INBOX_MAX(50) を溢れさせ、
      // **他ピアの未読ペア成立/unpair イベントを押し出せる**（WS 未接続の相手が取りこぼす）。
      // クライアントは元々 SeenPairingIds で重複を捨てるため、畳んでも観測される挙動は変わらない。
      const pairingId = typeof e.pairingId === 'string' ? e.pairingId : null;
      const kept = pairingId === null
        ? events
        : events.filter((x) => !(x.pairingId === pairingId && x.type === e.type));
      kept.push(e);
      const pruned = pruneInbox(kept, now);
      await this.state.storage.put('inbox', pruned);
      // 新しい通知で延命される範囲の最も遅い期限を、DO あたり 1 個の alarm に設定する。
      await this.ensureInboxAlarm(pruned, 'latest');
    }

    let delivered = 0;
    for (const ws of this.state.getWebSockets()) {
      if (ws.readyState === WebSocket.OPEN) {
        try {
          ws.send(JSON.stringify(e));
          delivered++;
        } catch {
          /* 片側切断は無視 */
        }
      }
    }
    return json(200, { ok: true, delivered });
  }

  /** inbox の cleanup 用 alarm を、既存 alarm を不必要に後ろ倒ししない範囲で予約する。 */
  private async ensureInboxAlarm(events: InboxEvent[], order: 'earliest' | 'latest'): Promise<void> {
    const expirations = events
      .map((event) => inboxExpiresAt(event))
      .filter((expiresAt): expiresAt is number => expiresAt !== null);
    if (expirations.length === 0) return;

    const target = order === 'earliest'
      ? Math.min(...expirations)
      : Math.max(...expirations);
    const existing = await this.state.storage.getAlarm();
    const shouldMove = existing === null
      || (order === 'earliest' ? existing > target : existing < target);
    if (shouldMove) await this.state.storage.setAlarm(target);
  }

  /** TTL を過ぎた inbox 行だけを削除し、残りがあれば最短期限へ alarm を進める。 */
  async alarm(): Promise<void> {
    const stored = await this.state.storage.get<InboxEvent[]>('inbox');
    if (stored === undefined) return;

    const events = Array.isArray(stored) ? pruneInbox(stored, Date.now()) : [];
    if (events.length === 0) {
      await this.state.storage.delete('inbox');
      // deleteAll は使わない。presence は DeviceDO 内で固定 1 件として保持する。
      return;
    }

    if (!Array.isArray(stored) || events.length !== stored.length) {
      await this.state.storage.put('inbox', events);
    }

    const expirations = events
      .map((event) => inboxExpiresAt(event))
      .filter((expiresAt): expiresAt is number => expiresAt !== null);
    if (expirations.length > 0) {
      // alarm() 内では現在の alarm は消費済みなので、残りの最短期限を必ず設定する。
      await this.state.storage.setAlarm(Math.min(...expirations));
    }
  }

  // ---- Hibernation handlers (inbox は受信専用。client からのメッセージは握りつぶす) ----

  async webSocketMessage(_ws: WebSocket, _msg: ArrayBuffer | string): Promise<void> {
    /* inbox は push 専用。keepalive 等のテキストは無視。 */
  }

  async webSocketClose(ws: WebSocket, code: number, reason: string, wasClean: boolean): Promise<void> {
    try {
      ws.close(code, reason);
    } catch {
      /* 既に閉じている */
    }
    void wasClean;
  }

  async webSocketError(_ws: WebSocket, _e: unknown): Promise<void> {
    /* getWebSockets() から自然に除外される */
  }
}

function json(status: number, body: object): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}
