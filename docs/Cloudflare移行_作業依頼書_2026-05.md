# Ferry Cloudflare 移行 作業依頼書

最終更新: 2026-05-26 / 起票元: RealTimeTranslator 月額サブスク化検討セッションで派生 / 担当: ゆろさん (本人) + Claude Code

---

## TL;DR

VPS (`1llum1n4t1.net`) 上で動いている **Ferry.Relay (Node.js WebSocket リレー)** と **coturn (STUN サーバー)** を **Cloudflare** に移行する。

- 🏆 **Ferry.Relay → Cloudflare Workers + Durable Objects** (約 50 行で書ける、 Hibernation + Egress 永久無料で月額ほぼ $0)
- 🟢 **coturn → `stun.cloudflare.com:3478`** (自前 coturn 撤廃、 Docker 1 サービス削減)
- 🛡️ **Firebase は据え置き** (移行工数 vs メリット見合わず)

別プロジェクト RealTimeTranslator も同時期に Cloudflare Workers Paid プラン契約予定で、 **DO 学習成果を共有できる**。 Ferry.Relay の DO 移植は RealTimeTranslator の中継サーバー実装と本質的に同じ構造のためノウハウ流用が効く。

---

## 背景・動機

### 現状の VPS 依存

Ferry は 3 階層フォールバック設計:

1. **TCP 直接接続** (LAN)
2. **UDP ホールパンチ** (NAT 越え P2P) — STUN サーバー必要 → 現状 `1llum1n4t1.net:3478` の自前 coturn
3. **WebSocket リレー** (最終手段) — `wss://1llum1n4t1.net/ferry-relay` の自前 Node.js サーバー

この 2 つが VPS で動いており、 月額 ¥1,000-3,000 の VPS コストの一部を占有している。 別途 Velopack 配信は既に Cloudflare R2 (`ferry.nephilim.jp`) に移行済み。

### 移行する理由

| メリット | 影響 |
|---|---|
| **Egress 永久無料** | 大ファイル転送がリレー経由になっても帯域課金ゼロ |
| **Hibernation** | アイドル中の DO は完全 0 円課金、 リレー接続が無い時間帯のコストが消える |
| **270+ PoP の Edge** | リレー経由時のレイテンシが peer 近接ロケーションに分散 |
| **デプロイ運用ゼロ** | Docker compose / systemd / OS パッチ管理が消える |
| **RealTimeTranslator と DO 学習・運用を共有** | Workers Paid の $5/月固定費を 2 プロジェクトで按分 |

### 移行しない判断 (Firebase Realtime DB)

Firebase Realtime DB (シグナリング・プレゼンス) と Firebase Hosting (Bridge ページ) は **据え置く**。 理由:

- 現状 Firebase Spark 無料枠 (1GB ストレージ + 10GB/月 download) で十分動いており **月額 $0**
- 移行工数が大 (`FirebaseSignaling.cs` / `bridge.js` / `firebase-cleanup.yml` / Native AOT 用 `*JsonContext` Source Generator 全部書き直し)
- Realtime DB のリアルタイム pub/sub 性能をシグナリング用途で代替する場合 DO + WebSocket 構成が必要 = 工数の山
- メリットは「ベンダー一本化」だけで実利益が薄い

---

## スコープ

| # | タスク | 規模 | 優先度 |
|---|---|---|---|
| 1 | Ferry.Relay → Cloudflare Workers + DO へ移行 | 中 (1-2 日) | 🔴 高 |
| 2 | STUN を `stun.cloudflare.com:3478` へ切替 | 小 (30 分) | 🟡 中 |
| 3 | アプリ側の URL 切替 + リリース | 小 (1 時間 + CI) | 🔴 高 |
| - | Firebase 据え置き | — | — (やらない) |

---

## 前提条件 (作業開始前にチェック)

- [ ] Cloudflare Workers Paid プラン契約済み ($5/月、 RealTimeTranslator 用に契約予定なので共用)
- [ ] `wrangler` CLI ローカル install 済み (`npm install -g wrangler` + `wrangler login`)
- [ ] 既存 `ferry.nephilim.jp` Zone が Cloudflare 配下 (Velopack 配信で確認済)
- [ ] DO の Hello World を 1 度動かして hibernation 挙動を理解済 (RealTimeTranslator 計画 Phase 0 で実施)

---

## タスク 1: Ferry.Relay を Cloudflare Workers + DO へ移行 🏆

### 1.1 現状の Ferry.Relay 仕様確認 (移行前に必須)

`src/Ferry.Relay/` ディレクトリで以下を確認:

- WebSocket エンドポイントのパス (`/ferry-relay`)
- pairId / sessionId などの URL クエリパラメータ
- 認証方式 (Bearer Token / クエリ署名 / 認証なし のどれか)
- メッセージ形式 (バイナリパススルー、 サイズ上限)
- close handling (片方が切れたら相手も close するか等)
- 同時接続数の上限管理

⚠️ **既存挙動を完全に把握してから書き換える**。 アプリ側の `WebSocketRelayTransport.cs` と整合させること。

### 1.2 新 Cloudflare Worker プロジェクト作成

```powershell
# 既存 Ferry リポジトリ内に分離して作る
mkdir infra/cloudflare/relay
cd infra/cloudflare/relay
npm create cloudflare@latest -- --type=durable-objects
# プロンプト: TypeScript, Yes (deploy), No (git init は既存に乗る)
```

### 1.3 `wrangler.toml` 設定

```toml
name = "ferry-relay"
main = "src/index.ts"
compatibility_date = "2026-05-26"

# Workers Paid プラン必須 (DO 使用のため)

[[durable_objects.bindings]]
name = "RELAY"
class_name = "RelayDO"

[[migrations]]
tag = "v1"
new_sqlite_classes = ["RelayDO"]  # v2 以降は new_classes でない方を使う

# カスタムドメイン (Cloudflare Dashboard で手動設定するか route で)
[[routes]]
pattern = "relay.ferry.nephilim.jp/*"
custom_domain = true
```

### 1.4 Worker エントリポイント

`src/index.ts`:

```typescript
export interface Env {
  RELAY: DurableObjectNamespace;
}

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);

    // ヘルスチェック
    if (url.pathname === "/health") {
      return new Response("OK", { status: 200 });
    }

    // WebSocket 以外は拒否
    if (req.headers.get("Upgrade") !== "websocket") {
      return new Response("Expected websocket", { status: 426 });
    }

    // pairId 抽出 (例: /ferry-relay?pairId=abc123)
    const pairId = url.searchParams.get("pairId");
    if (!pairId) {
      return new Response("Missing pairId", { status: 400 });
    }

    // pairId をハッシュ化して DO ID を生成 (生 pairId 直入れは NG: ログ漏洩時に他ペアに到達される)
    const idStr = await hashPairId(pairId, env);
    const doId = env.RELAY.idFromName(idStr);
    const stub = env.RELAY.get(doId);

    return stub.fetch(req);
  },
};

async function hashPairId(pairId: string, env: Env): Promise<string> {
  // 簡易実装: SHA-256(pairId + secret salt) の hex
  // 本番では env.SALT を Workers Secrets に登録して使う
  const data = new TextEncoder().encode(pairId + "ferry-relay-salt-2026");
  const buf = await crypto.subtle.digest("SHA-256", data);
  return Array.from(new Uint8Array(buf))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}
```

### 1.5 Durable Object 実装 (リレー本体)

`src/relay-do.ts`:

```typescript
export class RelayDO {
  state: DurableObjectState;
  env: Env;

  constructor(state: DurableObjectState, env: Env) {
    this.state = state;
    this.env = env;
  }

  async fetch(req: Request): Promise<Response> {
    // 既存 peer 数チェック (Ferry は 1 ペアにつき 2 peer まで)
    const existing = this.state.getWebSockets();
    if (existing.length >= 2) {
      return new Response("Pair already full", { status: 409 });
    }

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);

    // Hibernation を効かせるため acceptWebSocket を使う (addEventListener はダメ)
    this.state.acceptWebSocket(server);

    return new Response(null, {
      status: 101,
      webSocket: client,
    });
  }

  // ピアからメッセージが来たら相手側に転送
  async webSocketMessage(ws: WebSocket, msg: ArrayBuffer | string) {
    const peers = this.state.getWebSockets();
    for (const peer of peers) {
      if (peer !== ws && peer.readyState === WebSocket.OPEN) {
        peer.send(msg);
      }
    }
  }

  // 片方が切れたら相手も close
  async webSocketClose(ws: WebSocket, code: number, reason: string) {
    const peers = this.state.getWebSockets();
    for (const peer of peers) {
      if (peer !== ws) {
        try {
          peer.close(1001, "Peer disconnected");
        } catch {}
      }
    }
  }

  async webSocketError(ws: WebSocket, error: unknown) {
    const peers = this.state.getWebSockets();
    for (const peer of peers) {
      if (peer !== ws) {
        try {
          peer.close(1011, "Peer errored");
        } catch {}
      }
    }
  }
}
```

⚠️ **重要ポイント**:
- `this.state.acceptWebSocket(ws)` を使うことで Hibernation が効く (idle 時メモリ 0、 課金 0)
- `webSocketMessage` / `webSocketClose` / `webSocketError` メソッドが hibernation 復帰時のエントリ
- `addEventListener("message", ...)` は **使わない** (hibernation が無効化される、 コスト爆発)

### 1.6 カスタムドメイン設定

Cloudflare Dashboard で:
1. Workers & Pages → ferry-relay → Settings → Triggers
2. **Custom Domains** → Add Custom Domain → `relay.ferry.nephilim.jp`
3. DNS は自動的に CNAME 設定される

### 1.7 デプロイ

```powershell
cd infra/cloudflare/relay
wrangler deploy
# → relay.ferry.nephilim.jp で公開される
```

### 1.8 検証

```powershell
# ヘルスチェック
curl https://relay.ferry.nephilim.jp/health
# → OK が返れば疎通 OK

# WebSocket 接続テスト (websocat 必要)
# 2 つのターミナルで:
websocat "wss://relay.ferry.nephilim.jp/ferry-relay?pairId=test123"
# 片方で送信したメッセージがもう片方に届けば成功
```

ローカル Ferry アプリで実機テスト (URL を新エンドポイントに一時切替):
- 2 台の PC で TCP 失敗 → UDP 失敗 → リレーフォールバックまで誘導
- 数 GB のファイル転送が完走するか
- 切断・再接続でレジュームが動くか

---

## タスク 2: STUN を Cloudflare 公開 STUN へ統一 🟢

### 2.1 変更箇所

[`src/Ferry/Infrastructure/UdpHolePunchTransport.cs`](src/Ferry/Infrastructure/UdpHolePunchTransport.cs) の STUN サーバーリストを変更。

現状 (推定):
```csharp
private static readonly (string Host, int Port)[] StunServers = new[]
{
    ("1llum1n4t1.net", 3478),          // 自前 coturn (主)
    ("stun.l.google.com", 19302),      // Google (従)
    ("stun.cloudflare.com", 3478),     // Cloudflare (従)
};
```

変更後:
```csharp
private static readonly (string Host, int Port)[] StunServers = new[]
{
    ("stun.cloudflare.com", 3478),     // Cloudflare (主、 日本 Tokyo PoP)
    ("stun.l.google.com", 19302),      // Google (従)
};
```

### 2.2 関連設定の見直し

`F-6 (設計提案 design-proposals.md)` で「STUN を `AppSettings` に外出し」が議題化されている。 今回のタイミングで一緒にやってもいい (低リスク改善)。

ただし今回スコープ外でもよい (機能としては配列定数のままで動く)。

### 2.3 検証

- 対称 NAT 環境で STUN 経由 UDP ホールパンチが成功すること
- `Util.Logger` で STUN 応答ログを確認 (どのサーバーが応答したか)

---

## タスク 3: アプリ側 URL 切替 + リリース

### 3.1 変更箇所

[`src/Ferry/Services/`](src/Ferry/Services/) または [`src/Ferry/Infrastructure/WebSocketRelayTransport.cs`](src/Ferry/Infrastructure/WebSocketRelayTransport.cs) で、 リレー URL を保持している箇所を変更。

⚠️ **`UpdateBaseUrl` のような `[JsonIgnore]` ハードコード方式を採用すること**。 settings.json から書き換え不可にして攻撃面を消す (RealTimeTranslator の `AppSettings.cs:46-48` と同パターン)。

変更例:
```csharp
// 旧
private const string RelayUrl = "wss://1llum1n4t1.net/ferry-relay";

// 新
private const string RelayUrl = "wss://relay.ferry.nephilim.jp/ferry-relay";
```

### 3.2 旧 VPS リレーの停止方針 (即停止 / 2026-05 確定)

**即停止方針**を採用する。Velopack 配信完了直後に VPS 側の `ferry-relay` + `coturn` コンテナを停止する。並行運用しない。

理由:
- Ferry の自動更新 (起動時 + Velopack) で旧クライアントは数時間〜数日で新版に揃う
- 並行運用は VPS コスト + 監視・パッチ運用が継続するため、即停止の方が運用シンプル
- ロールバックは「旧 URL を git revert + hotfix リリース」で対応 (VPS コンテナ復活も可能だが、その場合は 1llum1n4t1.net 側で再 `docker compose up`)

### 3.2.1 1llum1n4t1.net 側の停止手順

別リポジトリ `C:\Users\IMT\dev\1llum1n4t1.net` の `docker-compose.yml` から `ferry-relay` + `coturn` サービスブロックを削除し、VPS で `docker compose down ferry-relay coturn` → `docker compose up -d` (残サービス維持) を実行する。詳細手順は 1llum1n4t1.net リポジトリ側の `docs/server.md` を更新して残すこと。

### 3.3 テスト追加

- 新 URL での WebSocket 接続テスト (NSubstitute モック)
- `WebSocketRelayTransport` 単体テストを `Ferry.Tests` に追加

### 3.4 リリース手順

```powershell
# 既存の /vava スキル使用 (バージョン bump + release/x.y.z ブランチ作成)
# CI が release/x.y.z push で発火し Velopack 配信
```

`/vava` は Ferry プロジェクトでも動く想定 (CLAUDE.md でバージョン管理ルール記載済)。

### 3.5 移行後の確認

- Velopack 配信が成功し、トレイメニュー「アップデートを確認」で新版取得が成立すること
- 新リレー (`relay.ferry.nephilim.jp`) で WebSocket 接続 + 大ファイル送受信ができること
- 上記 OK を確認次第、1llum1n4t1.net 側で `ferry-relay` + `coturn` コンテナを撤去 (即停止方針)

---

## ロールバック手順 🆘

万一新リレーで問題発生時 (**即停止方針なので 1llum1n4t1.net 側を一度撤去すると即時復旧不可**、復活には docker compose up が必要):

1. **Workers 側緊急停止**: Cloudflare Dashboard → Workers → ferry-relay → Disable (新リレーへの接続を即遮断)
2. **VPS 側リレー復活**: 1llum1n4t1.net リポジトリの `docker-compose.yml` から削除した `ferry-relay` + `coturn` サービスブロックを `git revert` で復元 → VPS で `docker compose up -d`
3. **アプリ側 hotfix**: `App.axaml.cs` の `const string RelayUrl` を `wss://1llum1n4t1.net/ferry-relay` に戻して `/vava` で hotfix リリース
4. **DNS 切替案**: `relay.ferry.nephilim.jp` の CNAME を一時的に VPS に向ける (ただし TLS 証明書設定要、 推奨しない)

ロールバック判断基準:
- 接続成功率が 95% を切る (1 時間継続)
- DO duration コストが異常に膨らんでいる (Workers Paid 無料枠の 200% 超え 等)
- 大ファイル転送が完走しない事例が 3 件以上

---

## 完了条件 (Definition of Done)

- [ ] `relay.ferry.nephilim.jp` で WebSocket リレーが動作している
- [ ] アプリ側の `RelayUrl` ハードコードが新 URL に切替済み (`[JsonIgnore]` パターンで)
- [ ] STUN サーバーリストが Cloudflare/Google の 2 段になっている
- [ ] `Ferry.Tests` に `WebSocketRelayTransport` の単体テストが追加されている
- [ ] 数 GB のファイル転送が新リレー経由で完走する実機テスト合格
- [ ] Velopack 経由で新バージョン (v1.0.33 以降) が配信されている
- [ ] 1 ヶ月後に VPS の `ferry-relay` コンテナ停止 + `coturn` コンテナ停止が完了
- [ ] `1llum1n4t1.net` リポジトリの docker-compose.yml から `ferry-relay` と `coturn` サービス削除
- [ ] `CLAUDE.md` の「サーバー接続情報」セクション更新 (`relay.ferry.nephilim.jp` を記載)
- [ ] `memory-bank/Ferry/activeContext.md` を更新

---

## コスト試算

### 移行前 (現状)

```
VPS (1llum1n4t1.net) 月額: 推定 ¥1,000-3,000 (Docker compose 5 サービス全体)
  内訳のうち Ferry 関連 (Ferry.Relay + coturn): ¥500-1,500/月程度
```

### 移行後

```
Cloudflare Workers Paid: $5/月 (RealTimeTranslator と共有、 ferry-relay 単独計上では $0)
Ferry.Relay DO 課金 (100 ペア × 月平均 30 分接続想定): $0 (無料枠内)
Egress: $0 (永久無料、 大ファイル転送し放題)

→ 実質追加: $0/月
```

VPS は他のサービスでまだ使い続ける場合は VPS 自体は残るが、 Ferry.Relay + coturn 分の貢献は消える。 Ferry 関連で VPS を完全撤廃するなら **RealTimeTranslator と合わせて Cloudflare 集約のメリット最大化**。

---

## RealTimeTranslator サブスク計画との関係

別プロジェクト `RealTimeTranslator` で同時期に Cloudflare Workers + DO 中継サーバーを構築予定 (詳細: `C:\Users\IMT\.claude\plans\api-api-99-stripe-link-cozy-comet.md`)。

| 観点 | Ferry.Relay 移行 | RealTimeTranslator 中継 |
|---|---|---|
| DO の役割 | 2 peer 間の WebSocket リレー | 1 user 1 instance で OpenAI へプロキシ |
| Hibernation | ピアアイドル時に効く | VAD 無音区間で効く |
| メッセージ形式 | バイナリパススルー | session.input_audio_buffer.append JSON |
| 認証 | pairId クエリ (Firebase シグナリングで保護) | JWT (短期) + opaque refresh token (長期) |
| 期待 DO duration | 短い (ファイル転送のみ、 平均 30 分以下) | 長め (字幕翻訳セッション、 1-3 時間) |

**学習成果の共有**:
- Hibernation の挙動 (`acceptWebSocket` vs `addEventListener`)
- `wrangler.toml` の DO migration 記法
- DO ID のハッシュ化必須性 (生 ID 直入れ NG)
- Cloudflare Dashboard でのカスタムドメイン設定

→ Ferry.Relay は **より単純** な構造なので、 **Ferry.Relay を先に移行して DO 運用経験を積む** のが良い順序。 RealTimeTranslator の中継は VAD・認証・課金集計まで絡むので難易度高。

### 推奨順序

1. **Phase 0**: Workers Paid 契約 + DO Hello World で hibernation 動作確認 (両プロジェクト共通)
2. **Phase 1**: **Ferry.Relay を移行** (シンプル、 1-2 日) ← 本作業依頼書のスコープ
3. **Phase 2**: 経験を活かして RealTimeTranslator 中継サーバー実装

---

## 参考: 既存 Ferry プロジェクト情報

### 関連ファイル (現状ソース)

- `src/Ferry.Relay/` — Node.js リレーサーバー本体 (移行対象)
- `src/Ferry/Infrastructure/WebSocketRelayTransport.cs` — アプリ側 WebSocket リレークライアント
- `src/Ferry/Infrastructure/UdpHolePunchTransport.cs` — STUN サーバーリスト
- `src/Ferry/Infrastructure/FirebaseSignaling.cs` — Firebase シグナリング (移行対象外、 据え置き)
- `web/wrangler.toml` — 既存 Velopack 配信用 Worker (触らない)

### 既存 Cloudflare リソース

- Zone: `ferry.nephilim.jp` (Cloudflare 配下)
- R2 bucket: `ferry-updates` (Velopack 配信、 触らない)
- Worker: 既存 landing page (`web/wrangler.toml`、 触らない)

### 既存 GitHub Secrets (CI 用)

- `CLOUDFLARE_API_TOKEN` (R2 アップロード用、 流用可)
- `CLOUDFLARE_ACCOUNT_ID` (同上、 流用可)

→ Workers デプロイ用に追加 Secrets 不要。 既存トークンに Workers 編集権限が含まれているか要確認 (足りなければ追加スコープ発行)。

### Ferry プロジェクト固有の注意点

- **Native AOT**: アプリ側変更ではリフレクション使わない・JsonSerializerOptions の動的指定を避ける (`*JsonContext` Source Generator 経由)
- **テスト**: xUnit v3 + NSubstitute、 非同期メソッドに `TestContext.Current.CancellationToken` を渡す
- **言語**: コメント・コミットメッセージ・UI 文言すべて日本語
- **バージョン管理**: `/vava` スキル経由でバージョン bump (`Directory.Build.props` + `src/Ferry/AppVersion.cs` 同期)

---

## 補足: トラブルシュート (RealTimeTranslator 計画から流用)

| 地雷 | 対策 |
|---|---|
| `wrangler dev` のローカル DO は memory mode で本物と微妙に違う | デプロイして本番確認も併用 |
| DO migrations を `wrangler.toml` に書かないとデプロイ失敗 | 初回は `new_sqlite_classes` 必須 |
| `acceptWebSocket` を使わず `addEventListener('message')` のみだと Hibernation 効かない | コスト爆発の原因、 上記コード雛形通り `webSocketMessage` メソッドを実装 |
| Workers のメッセージサイズ上限 1 MB | Ferry チャンク 64KB なので問題なし |
| DO migration 時の全接続切断 | デプロイ時間を空いてる時間帯 (4:00 AM 等) に |
| 生 pairId を `idFromName` 直入れ | ハッシュ化必須 (上記 `hashPairId` 参照) |
| Workers の WebSocket binary フレーム判定 | `webSocketMessage(ws, msg)` で `msg instanceof ArrayBuffer` で分岐 |

---

## 次の一歩

1. **このドキュメントを読んだ Claude Code (Ferry 側)**:
   - `src/Ferry.Relay/` の現状ソースを確認して挙動を把握
   - 不明点があれば作業前にゆろさんに質問
   - タスク 1 から順に進める
2. **ゆろさん**:
   - Workers Paid 契約 (まだなら)
   - DO Hello World を 30 分触っておく (本作業前に hibernation 挙動を理解しておくとデバッグが速い)
   - 作業着手時にこのドキュメントの「完了条件」チェックリストを `memory-bank` の `activeContext.md` にコピーして進捗管理
