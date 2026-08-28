/**
 * Ferry signaling Durable Object (PairDO) — CF 単独完結移行 Step 1。
 *
 * 1 ペア (= 同じハッシュ化 pairId) ごとに 1 インスタンス。Firebase RTDB の
 * `signaling/{pairId}/...` サブツリーを DO storage で強整合・即時に再現する。
 * KV は eventual 最大 60s 伝播で WaitForOfferExternalIp(8s)/WaitForEndpoint(10s) を構造的に
 * 超過するため不可。DO は single-thread + storage で per-sender 分離・鮮度判定を正確に持てる。
 *
 * 認可は Worker 側で完結する (verifySessionToken + pairId 当事者 + sender キー強制)。
 * 本 DO は Worker から `X-Ferry-Device: <deviceId>` を信頼し、storage 操作のみを担う。
 *
 * storage キー:
 *   offer:{sender}        = { data: sdp, createdAt }
 *   answer:{sender}       = { data: sdp }
 *   endpoint:{sender}     = { data: endpoint, from: sender }   # From 二重防護のため from も保持
 *   probeOffer:{nonce}    = { data: sdp, createdAt }
 *   probeAnswer:{nonce}   = { data: sdp, createdAt }
 *   createdAt             = number   # signaling サブツリー鮮度 (cleanup 用)
 *
 * cleanup: createdAt 書込時に alarm を (createdAt + TTL) に設定。alarm 発火時に stale なら deleteAll
 * して休眠、活動が進んでいれば再設定。短間隔で全 DO を起こさない (CF 公式警告) よう lazy + 1h TTL。
 */

import { readJsonObject } from './http';

const SIGNALING_TTL_MS = 60 * 60 * 1000; // 1h。Firebase の firebase-cleanup.yml と同じ stale 閾値。

/** PairDO へ届く識別子はクライアント入力なので、storage キーへ使う前に形式を固定する。 */
const HEX32_RE = /^[a-f0-9]{32}$/;

/** SDP / endpoint は値単位でも上限を設け、JSON 本文上限の内側で扱えるようにする。 */
export const MAX_SDP_BYTES = 16 * 1024;
export const MAX_ENDPOINT_BYTES = 2 * 1024;

/** 同じ PairDO に残せる probe offer の最大数（同時 probe の想定数 + 余裕）。 */
export const MAX_PROBE_OFFERS = 16;

/** rere レビュー #C-06: offer 鮮度判定で許容するクライアント↔サーバの時計ズレ。
 *  /auth/token の CLOCK_SKEW ガード (auth.ts) と同じ 60s に揃える。 */
const STALE_TOLERANCE_MS = 60 * 1000;

interface TimedValue {
  data: string;
  createdAt: number;
}
interface PlainValue {
  data: string;
}
interface EndpointValue {
  data: string;
  from: string;
}

export class PairDO {
  private state: DurableObjectState;

  constructor(state: DurableObjectState, _env: unknown) {
    this.state = state;
  }

  async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);
    // Worker は `https://do/<rest>` 形式に rewrite して forward する (rest = offer / answer / endpoint /
    // probe-offer/{nonce} / probe-offers / probe-answer/{nonce} / probe/{nonce} / 空=cleanup)。
    const rest = url.pathname.replace(/^\/+/, '');
    const segs = rest.length > 0 ? rest.split('/') : [];
    const action = segs[0] ?? '';
    const device = req.headers.get('X-Ferry-Device') ?? '';
    const method = req.method;

    try {
      switch (action) {
        case 'offer':
          return method === 'POST'
            ? await this.writeOffer(device, req)
            : await this.readOffer(url);
        case 'answer':
          return method === 'POST'
            ? await this.writeAnswer(device, req)
            : await this.readAnswer(url);
        case 'endpoint':
          return method === 'POST'
            ? await this.writeEndpoint(device, req)
            : await this.readEndpoint(url);
        // rere レビュー #A1-08: probe-offer / probe は method を見ずに書込・削除していたため、
        // 読み取りのつもりの GET が状態を破壊した (offer/answer/endpoint は POST/GET を
        // 分岐しているのに非対称)。他アクションと同じくメソッドを強制する。
        case 'probe-offer':
          return method === 'POST'
            ? await this.writeProbe('probeOffer', segs[1] ?? '', device, req)
            : json(405, { error: 'METHOD_NOT_ALLOWED', action, method });
        case 'probe-offers':
          return await this.readProbeOffers();
        case 'probe-answer':
          return method === 'POST'
            ? await this.writeProbe('probeAnswer', segs[1] ?? '', device, req)
            : await this.readProbeAnswer(segs[1] ?? '');
        case 'probe':
          return method === 'DELETE'
            ? await this.deleteProbe(segs[1] ?? '')
            : json(405, { error: 'METHOD_NOT_ALLOWED', action, method });
        case '':
          return method === 'DELETE' ? await this.cleanupLeaves() : json(400, { error: 'BAD_ACTION' });
        default:
          return json(400, { error: 'BAD_ACTION', action });
      }
    } catch (e) {
      // rere レビュー #C-13: 例外の実体をレスポンスボディにしか載せていなかったため、
      // サーバ側 (wrangler tail / Workers Logs) には何も残らず、クライアント側も
      // 5xx のボディを読まずに捨てるので両端で消えていた。DO 名は伏せ、アクションと
      // 例外内容だけを構造化して残す (deviceId は Worker 側で検証済みなのでここでは出さない)。
      console.error('PairDO error', JSON.stringify({ action, method, error: String(e) }));
      return json(500, { error: 'DO_ERROR', message: String(e) });
    }
  }

  // ---- offer (per-sender, 鮮度あり) ----

  private async writeOffer(sender: string, req: Request): Promise<Response> {
    const senderError = validateSender(sender);
    if (senderError) return senderError;
    const parsed = await readJsonObject(req);
    if ('error' in parsed) return parsed.error;
    const body = parsed.value;
    if (typeof body.sdp !== 'string') return json(400, { error: 'BAD_BODY' });
    if (!isWithinUtf8Limit(body.sdp, MAX_SDP_BYTES)) return bodyTooLarge('sdp');
    // rere レビュー #C-06: createdAt は必ずサーバ時刻で刻む。
    // 旧実装は offerer がボディで申告した createdAt をそのまま保存し、listener 側が渡す
    // minCreatedAt (listener のローカル時計) と直接比較していた = cross-device の時計比較。
    // 同じ罠は probe 経路で「時計差で fresh offer を捨てる回帰を招いた」として既に撤去済み
    // (ConnectionService.cs の minProbeCreatedAt 撤去コメント参照) なのに offer 経路に残っていた。
    // 片側をサーバ時刻に寄せることで、比較のズレは「listener のローカル時計 vs サーバ時刻」の
    // 1 段だけになり、/auth/token の CLOCK_SKEW ガード (±60s) で有界になる。
    // クライアントは引き続き createdAt を送ってよい (無視するだけ) なので後方互換。
    const createdAt = Date.now();
    await this.state.storage.put(`offer:${sender}`, { data: body.sdp, createdAt } satisfies TimedValue);
    await this.bumpCreatedAt(createdAt);
    return json(200, { ok: true });
  }

  private async readOffer(url: URL): Promise<Response> {
    const from = url.searchParams.get('from') ?? '';
    if (!HEX32_RE.test(from)) return json(400, { error: 'BAD_DEVICE' });
    const minCreatedAt = Number(url.searchParams.get('minCreatedAt') ?? '0');
    const v = await this.state.storage.get<TimedValue>(`offer:${from}`);
    if (!v || !isWithinUtf8Limit(v.data, MAX_SDP_BYTES) || !v.data) return json(404, { error: 'NOT_FOUND' });
    // #C-06: minCreatedAt は listener のローカル時計なので、サーバ時刻との差を許容する。
    // 許容幅は /auth/token の CLOCK_SKEW と同じ 60s。offer の TTL は 1h あるので、
    // 60s ぶん緩めても「本当に古い offer」を拾う実害はない（むしろ取りこぼしの方が痛い）。
    if (minCreatedAt > 0 && v.createdAt < minCreatedAt - STALE_TOLERANCE_MS) {
      return json(404, { error: 'STALE' });
    }
    return json(200, { data: v.data, createdAt: v.createdAt });
  }

  // ---- answer (per-sender, 鮮度なし) ----

  private async writeAnswer(sender: string, req: Request): Promise<Response> {
    const senderError = validateSender(sender);
    if (senderError) return senderError;
    const parsed = await readJsonObject(req);
    if ('error' in parsed) return parsed.error;
    const body = parsed.value;
    if (typeof body.sdp !== 'string') return json(400, { error: 'BAD_BODY' });
    if (!isWithinUtf8Limit(body.sdp, MAX_SDP_BYTES)) return bodyTooLarge('sdp');
    await this.state.storage.put(`answer:${sender}`, { data: body.sdp } satisfies PlainValue);
    await this.bumpCreatedAt(Date.now());
    return json(200, { ok: true });
  }

  private async readAnswer(url: URL): Promise<Response> {
    const from = url.searchParams.get('from') ?? '';
    if (!HEX32_RE.test(from)) return json(400, { error: 'BAD_DEVICE' });
    const v = await this.state.storage.get<PlainValue>(`answer:${from}`);
    if (!v || !isWithinUtf8Limit(v.data, MAX_SDP_BYTES) || !v.data) return json(404, { error: 'NOT_FOUND' });
    return json(200, { data: v.data });
  }

  // ---- endpoint (per-sender, From 二重防護) ----

  private async writeEndpoint(sender: string, req: Request): Promise<Response> {
    const senderError = validateSender(sender);
    if (senderError) return senderError;
    const parsed = await readJsonObject(req);
    if ('error' in parsed) return parsed.error;
    const body = parsed.value;
    if (typeof body.endpoint !== 'string') return json(400, { error: 'BAD_BODY' });
    if (!isWithinUtf8Limit(body.endpoint, MAX_ENDPOINT_BYTES)) return bodyTooLarge('endpoint');
    await this.state.storage.put(
      `endpoint:${sender}`,
      { data: body.endpoint, from: sender } satisfies EndpointValue,
    );
    await this.bumpCreatedAt(Date.now());
    return json(200, { ok: true });
  }

  private async readEndpoint(url: URL): Promise<Response> {
    const from = url.searchParams.get('from') ?? '';
    if (!HEX32_RE.test(from)) return json(400, { error: 'BAD_DEVICE' });
    const v = await this.state.storage.get<EndpointValue>(`endpoint:${from}`);
    if (!v || !isWithinUtf8Limit(v.data, MAX_ENDPOINT_BYTES) || !v.data || !HEX32_RE.test(v.from)) {
      return json(404, { error: 'NOT_FOUND' });
    }
    // storage キーが sender なので from は構造的に保証されるが、payload の from も返して
    // クライアント側の From==peer 検証 (FirebaseSignaling と同じ二重防護) を維持できるようにする。
    return json(200, { endpoint: v.data, from: v.from });
  }

  // ---- probe (per-nonce) ----

  private async writeProbe(
    prefix: 'probeOffer' | 'probeAnswer',
    nonce: string,
    sender: string,
    req: Request,
  ): Promise<Response> {
    const senderError = validateSender(sender);
    if (senderError) return senderError;
    if (!nonce) return json(400, { error: 'NO_NONCE' });
    if (!HEX32_RE.test(nonce)) return json(400, { error: 'BAD_NONCE' });
    const parsed = await readJsonObject(req);
    if ('error' in parsed) return parsed.error;
    const body = parsed.value;
    if (typeof body.sdp !== 'string') return json(400, { error: 'BAD_BODY' });
    if (!isWithinUtf8Limit(body.sdp, MAX_SDP_BYTES)) return bodyTooLarge('sdp');

    const key = `${prefix}:${nonce}`;
    // offer だけを制限すると probeAnswer:{nonce} を無制限に作れるため、両 prefix を
    // 同じ件数上限で束縛する。同じ nonce の再送は上限到達後も上書き可能。
    const existing = await this.state.storage.get<TimedValue>(key);
    if (existing === undefined) {
      const probes = await this.state.storage.list<TimedValue>({
        prefix: `${prefix}:`,
        limit: MAX_PROBE_OFFERS,
      });
      if (probes.size >= MAX_PROBE_OFFERS) {
        return json(429, { error: 'PROBE_LIMIT', message: 'probe entry limit exceeded' });
      }
    }

    const createdAt = Date.now();
    await this.state.storage.put(key, { data: body.sdp, createdAt } satisfies TimedValue);
    await this.bumpCreatedAt(createdAt);
    return json(200, { ok: true });
  }

  private async readProbeOffers(): Promise<Response> {
    const map = await this.state.storage.list<TimedValue>({
      prefix: 'probeOffer:',
      limit: MAX_PROBE_OFFERS,
    });
    const offers: { nonce: string; sdp: string }[] = [];
    for (const [key, v] of map) {
      if (offers.length >= MAX_PROBE_OFFERS) break;
      const nonce = key.slice('probeOffer:'.length);
      // 旧データや直接 storage を汚された場合も、未検証値をレスポンスへ流出させない。
      if (HEX32_RE.test(nonce) && v && v.data && isWithinUtf8Limit(v.data, MAX_SDP_BYTES)) {
        offers.push({ nonce, sdp: v.data });
      }
    }
    return json(200, { offers });
  }

  private async readProbeAnswer(nonce: string): Promise<Response> {
    if (!nonce) return json(400, { error: 'NO_NONCE' });
    if (!HEX32_RE.test(nonce)) return json(400, { error: 'BAD_NONCE' });
    const v = await this.state.storage.get<TimedValue>(`probeAnswer:${nonce}`);
    if (!v || !v.data || !isWithinUtf8Limit(v.data, MAX_SDP_BYTES)) return json(404, { error: 'NOT_FOUND' });
    return json(200, { sdp: v.data });
  }

  private async deleteProbe(nonce: string): Promise<Response> {
    if (!nonce) return json(400, { error: 'NO_NONCE' });
    if (!HEX32_RE.test(nonce)) return json(400, { error: 'BAD_NONCE' });
    await this.state.storage.delete(`probeOffer:${nonce}`);
    await this.state.storage.delete(`probeAnswer:${nonce}`);
    return json(200, { ok: true });
  }

  // ---- cleanup ----

  /** offers/answers/endpoints/createdAt の leaf を一括削除 (probe は per-nonce で即時 cleanup 済)。 */
  private async cleanupLeaves(): Promise<Response> {
    const keys: string[] = ['createdAt'];
    for (const prefix of ['offer:', 'answer:', 'endpoint:']) {
      const map = await this.state.storage.list({ prefix });
      for (const k of map.keys()) keys.push(k);
    }
    if (keys.length > 0) await this.state.storage.delete(keys);
    return json(200, { ok: true, deleted: keys.length });
  }

  /** signaling サブツリー鮮度を更新し、stale 掃除 alarm を (createdAt + TTL) に設定する。 */
  private async bumpCreatedAt(createdAt: number): Promise<void> {
    await this.state.storage.put('createdAt', createdAt);
    const existing = await this.state.storage.getAlarm();
    const target = createdAt + SIGNALING_TTL_MS;
    // alarm が無い、または現状より後ろにずらす必要があるときだけ設定 (頻繁な再設定を避ける)。
    if (existing === null || existing < target) await this.state.storage.setAlarm(target);
  }

  /** TTL 経過で signaling データを一掃して休眠する。活動が継続していれば再設定して延命。 */
  async alarm(): Promise<void> {
    const createdAt = (await this.state.storage.get<number>('createdAt')) ?? 0;
    const age = Date.now() - createdAt;
    if (age >= SIGNALING_TTL_MS) {
      await this.state.storage.deleteAll();
      // 再設定しない → 次の書込まで DO は休眠 (短間隔で全 DO を起こさない・CF 公式警告)。
    } else {
      await this.state.storage.setAlarm(createdAt + SIGNALING_TTL_MS);
    }
  }
}

function json(status: number, body: object): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

/** sender は storage キーの一部なので、未指定と形式不正を区別して拒否する。 */
function validateSender(sender: string): Response | null {
  if (!sender) return json(400, { error: 'NO_SENDER' });
  if (!HEX32_RE.test(sender)) return json(400, { error: 'BAD_DEVICE' });
  return null;
}

/** UTF-8 の実バイト数で値フィールドを制限する（JavaScript の UTF-16 文字数ではない）。 */
function isWithinUtf8Limit(value: string, maxBytes: number): boolean {
  return new TextEncoder().encode(value).byteLength <= maxBytes;
}

/** フィールド上限超過も入力本文の過大入力として同じエラー契約で返す。 */
function bodyTooLarge(field: string): Response {
  return json(413, { error: 'BODY_TOO_LARGE', message: `${field} exceeds the size limit` });
}
