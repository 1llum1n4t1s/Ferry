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

const SIGNALING_TTL_MS = 60 * 60 * 1000; // 1h。Firebase の firebase-cleanup.yml と同じ stale 閾値。

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
            ? await this.writeProbe('probeOffer', segs[1] ?? '', req)
            : json(405, { error: 'METHOD_NOT_ALLOWED', action, method });
        case 'probe-offers':
          return await this.readProbeOffers();
        case 'probe-answer':
          return method === 'POST'
            ? await this.writeProbe('probeAnswer', segs[1] ?? '', req)
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
    if (!sender) return json(400, { error: 'NO_SENDER' });
    const body = (await req.json()) as { sdp?: unknown; createdAt?: unknown };
    if (typeof body.sdp !== 'string') return json(400, { error: 'BAD_BODY' });
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
    const minCreatedAt = Number(url.searchParams.get('minCreatedAt') ?? '0');
    const v = await this.state.storage.get<TimedValue>(`offer:${from}`);
    if (!v || !v.data) return json(404, { error: 'NOT_FOUND' });
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
    if (!sender) return json(400, { error: 'NO_SENDER' });
    const body = (await req.json()) as { sdp?: unknown };
    if (typeof body.sdp !== 'string') return json(400, { error: 'BAD_BODY' });
    await this.state.storage.put(`answer:${sender}`, { data: body.sdp } satisfies PlainValue);
    return json(200, { ok: true });
  }

  private async readAnswer(url: URL): Promise<Response> {
    const from = url.searchParams.get('from') ?? '';
    const v = await this.state.storage.get<PlainValue>(`answer:${from}`);
    if (!v || !v.data) return json(404, { error: 'NOT_FOUND' });
    return json(200, { data: v.data });
  }

  // ---- endpoint (per-sender, From 二重防護) ----

  private async writeEndpoint(sender: string, req: Request): Promise<Response> {
    if (!sender) return json(400, { error: 'NO_SENDER' });
    const body = (await req.json()) as { endpoint?: unknown };
    if (typeof body.endpoint !== 'string') return json(400, { error: 'BAD_BODY' });
    await this.state.storage.put(
      `endpoint:${sender}`,
      { data: body.endpoint, from: sender } satisfies EndpointValue,
    );
    return json(200, { ok: true });
  }

  private async readEndpoint(url: URL): Promise<Response> {
    const from = url.searchParams.get('from') ?? '';
    const v = await this.state.storage.get<EndpointValue>(`endpoint:${from}`);
    if (!v || !v.data) return json(404, { error: 'NOT_FOUND' });
    // storage キーが sender なので from は構造的に保証されるが、payload の from も返して
    // クライアント側の From==peer 検証 (FirebaseSignaling と同じ二重防護) を維持できるようにする。
    return json(200, { endpoint: v.data, from: v.from });
  }

  // ---- probe (per-nonce) ----

  private async writeProbe(prefix: 'probeOffer' | 'probeAnswer', nonce: string, req: Request): Promise<Response> {
    if (!nonce) return json(400, { error: 'NO_NONCE' });
    const body = (await req.json()) as { sdp?: unknown };
    if (typeof body.sdp !== 'string') return json(400, { error: 'BAD_BODY' });
    await this.state.storage.put(`${prefix}:${nonce}`, { data: body.sdp, createdAt: Date.now() } satisfies TimedValue);
    await this.bumpCreatedAt(Date.now());
    return json(200, { ok: true });
  }

  private async readProbeOffers(): Promise<Response> {
    const map = await this.state.storage.list<TimedValue>({ prefix: 'probeOffer:' });
    const offers: { nonce: string; sdp: string }[] = [];
    for (const [key, v] of map) {
      if (v && v.data) offers.push({ nonce: key.slice('probeOffer:'.length), sdp: v.data });
    }
    return json(200, { offers });
  }

  private async readProbeAnswer(nonce: string): Promise<Response> {
    if (!nonce) return json(400, { error: 'NO_NONCE' });
    const v = await this.state.storage.get<TimedValue>(`probeAnswer:${nonce}`);
    if (!v || !v.data) return json(404, { error: 'NOT_FOUND' });
    return json(200, { sdp: v.data });
  }

  private async deleteProbe(nonce: string): Promise<Response> {
    if (!nonce) return json(400, { error: 'NO_NONCE' });
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
