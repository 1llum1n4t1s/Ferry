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

interface Presence {
  lastSeen: number;
  displayName: string;
  version: string;
}

/** inbox イベント。ペア成立 (FirebaseSignaling.PairingData 相当) と unpair 通知の両方を運ぶ。
 *  createdAt は TTL prune に必須。種別ごとの追加フィールド (sidA/nameA/... や type:'unpair') は任意。 */
type InboxEvent = { createdAt: number; [key: string]: unknown };

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
      return json(500, { error: 'DO_ERROR', message: String(e) });
    }
  }

  // ---- presence (poll) ----

  private async writePresence(req: Request): Promise<Response> {
    const parsed = await readJsonObject(req);
    if ('error' in parsed) return parsed.error;
    const body = parsed.value;
    const p: Presence = {
      lastSeen: Date.now(), // server now (クライアント時計に依存しない)
      displayName: typeof body.displayName === 'string' ? body.displayName : '',
      version: typeof body.version === 'string' ? body.version : '',
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
    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    // Hibernation 対応で accept (アイドル中は課金 0)。
    this.state.acceptWebSocket(server);

    // 接続時に未読 (TTL 内) を flush。重複は client 側 SeenPairingIds + start-time gate で dedupe する
    // (Firebase の subscribe-replay と同じ前提なので client 防御をそのまま使える)。
    const events = (await this.state.storage.get<InboxEvent[]>('inbox')) ?? [];
    const now = Date.now();
    for (const e of events) {
      if (now - e.createdAt < INBOX_TTL_MS) {
        try {
          server.send(JSON.stringify(e));
        } catch {
          /* 即切断は webSocketClose で処理 */
        }
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
      const events = (await this.state.storage.get<InboxEvent[]>('inbox')) ?? [];
      const now = Date.now();
      events.push(e);
      const pruned = events.filter((x) => now - x.createdAt < INBOX_TTL_MS).slice(-INBOX_MAX);
      await this.state.storage.put('inbox', pruned);
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
