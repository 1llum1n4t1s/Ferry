# Ferry WebSocket リレー (Cloudflare Workers + Durable Objects)

旧 VPS Node.js リレー (`wss://1llum1n4t1.net/ferry-relay`) の置き換え。

- **エンドポイント**: `wss://watashiba.kagayoi.com/ferry-relay?pairId=<id>&role=<offer|answer>`
- **ランタイム**: Cloudflare Workers + Durable Objects (Hibernation API)
- **料金・課金**: 契約プラン・実使用・含有枠に依存するため、固定額や無料枠への適合はここで断言しない。
  Cloudflare 公式の [WebSocket Hibernation](https://developers.cloudflare.com/durable-objects/best-practices/websockets/)
  では、Hibernation 可能なアイドル中 DO は duration 課金が発生しないと説明されている。
  また [Durable Objects の料金仕様](https://developers.cloudflare.com/durable-objects/platform/pricing/) では、
  compute request billing に限り incoming WebSocket message に 20:1 の換算が適用される（メトリクスの実数は変わらない）。

クライアント側は [`src/Ferry/Infrastructure/WebSocketRelayTransport.cs`](../../../src/Ferry/Infrastructure/WebSocketRelayTransport.cs) を参照。プロトコル変更時はクライアントと両方更新すること。

## 初回セットアップ (ゆろさん側)

1. **Workers Paid プラン契約** (RealTimeTranslator 用契約と共用)

2. **wrangler CLI を入れる**

   ```powershell
   pnpm add -g wrangler
   wrangler login
   # ブラウザで Cloudflare アカウントを選択して認可
   ```

3. **依存をインストール** (初回のみ)

   ```powershell
   cd infra/cloudflare/relay
   pnpm install
   ```

4. **pairId ハッシュ用 SALT を登録**

   ```powershell
   wrangler secret put SALT
   # 入力プロンプトに長めの任意文字列 (32文字以上推奨) を貼り付ける。
   # 一度設定すれば以降のデプロイでも保持される。
   ```

5. **デプロイ**

   ```powershell
   wrangler deploy
   ```

   初回は `watashiba.kagayoi.com` の Custom Domain 紐付けが Cloudflare Dashboard 側でも必要な場合がある。
   - Dashboard → Workers & Pages → ferry-relay → Settings → Triggers → Custom Domains → `watashiba.kagayoi.com` を Add
   - DNS は Cloudflare が自動で `kagayoi.com` Zone に AAAA / CNAME を貼る

6. **疎通確認**

   ```powershell
   curl https://watashiba.kagayoi.com/health
   # → OK が返れば疎通完了
   ```

### 緊急停止（global breaker）

新規のリレー処理を緊急停止する場合は、`wrangler.toml` の `[vars]` にある
`RELAY_CIRCUIT_OPEN="0"` を `RELAY_CIRCUIT_OPEN="1"` に変更してから Worker をデプロイする。
この設定はデプロイ後に有効になる。`wrangler deploy` は外部サービスを書き換える操作なので、
この runbook から自動実行せず、ユーザーの明示依頼がある場合だけ実行すること。復旧時は値を
`"0"` に戻し、同じく明示依頼を受けてデプロイする。

## 開発

### 認証鍵の移行

`device_key_bindings` が deviceId と認証公開鍵の強整合な正本となる。KV の既存値は保持するが、
positive / negative のどちらも初回登録・自動移行の根拠にはしない。`auth_key_migration` の初期状態は
`importing` で、D1 へ import 済みの鍵だけを認証する。未移行 ID・D1 障害・状態欠損は 503
`AUTH_BINDING_UNAVAILABLE`、確定 D1 鍵との差分だけが従来の 401 `DEVICE_PUBKEY_MISMATCH` になる。
schema の再適用で `ready` や既存鍵を戻さない。

切替は保守時間中に operator が取得元・承認者・全件性・カットオフを確認した immutable snapshot を用いる。
物理停止・drain を実際に確認した `--writers-frozen` と、下記の論理フェンスを確認した
`--legacy-writers-fenced` は排他的である。KV scan の一致、固定待機時間、再読込だけで完了と判断しない。
writer freeze / fence と snapshot の取得・承認はこの CLI が代行しない。新正本の稼働後に旧 KV writer 版へ
rollback してはならない。remote schema / import / deploy は別途明示した運用操作として行う。

入力は UTF-8 JSON の `{"bindings":[{"deviceId":"32桁の小文字hex","pubKeySpki":"既存の公開鍵文字列"}]}`。
秘密鍵は含めない。承認済みファイルの raw SHA-256 と件数を別入力し、全件 D1 比較・競合0を確認する。
既存鍵と違う import は全 INSERT を失敗させ、上書きしない。

```powershell
# Node 26。既定は --local。作業先は絶対パスで指定する。
node scripts/import-key-snapshot.mjs --input <snapshot.json> --sha256 <承認済SHA256> --count <件数> --persist-to <作業領域の絶対パス> --writers-frozen --snapshot-approved --snapshot-provenance <取得元・承認者・カットオフ説明>
# 同じ固定入力を検証し、最後に ready を明示する場合だけ --activate を付ける。
# 本番運用では --persist-to の代わりに --remote --work-dir <SQL作業領域の絶対パス> を明示する。
```

`--activate` がない限り新規 ID は解禁しない。ready 後は一度確定した鍵を変更せず、新規同時登録の
敗者へ cfToken を発行しない。SQL 作業ファイルは公開鍵を含み、指定した作業領域へ保持する。

#### 論理フェンスでの切替

既存 writer の物理 drain が証明できない場合に、物理停止済みとは宣言しない。operator が承認した
カットオフ snapshot を正本に選び、KV writer を含まない D1 認証コードと新しい `SESSION_HMAC_SECRET`
を **同じ Worker version** に含める。Cloudflare 公式の [コードと secrets の同時 upload](https://developers.cloudflare.com/workers/configuration/secrets/)
に従い `pnpm exec wrangler deploy --secrets-file <非公開JSON>`、又は
`pnpm exec wrangler versions upload --secrets-file <非公開JSON>` の後に
`pnpm exec wrangler versions deploy <version-id>@100%` を行う。secret を単独で先行変更しない。
`versions upload` だけでは配信は切り替わらない。JSON は secret 値を含むため証跡へ転載しない。

実 deployment / version ID とその version の traffic 100% を確認し、同じ認可経路
`GET /pairs/{canonicalPairId}` を同一当事者 ID の有効期限内診断 token で検査する。
旧 HMAC token は 401 `BAD_TOKEN`、新 HMAC token は既存 pair の 200、又は未存在 pair の
404 `NOT_FOUND` を要求する。import 前の `/auth/token` は 503 になり得るため受理確認の代用にしない。
診断 token は operator がそれぞれの HMAC で作成し、token 自体を記録しない。新 HMAC の診断 token の
受理は D1 鍵 import 済みを意味しない。100% と probe 確認の後、全件 import・比較・明示 activate を行う。

次の非秘密 JSON を `--fence-evidence <絶対パス>` で渡し、`--fence-version <実version UUID>` を明示する。
これは operator の観測証跡の検査であり、CLI が Cloudflare を問い合わせて証明するものではない。

```json
{
  "schemaVersion": 1,
  "worker": "ferry-relay",
  "versionId": "実Worker version UUID",
  "deploymentId": "実deployment UUID",
  "trafficPercent": 100,
  "snapshotSha256": "承認済raw SHA256",
  "noKvWriter": true,
  "d1KeyAuthority": true,
  "hmacRotated": true,
  "observedAt": "観測UTC ISO日時",
  "probe": {
    "host": "watashiba.kagayoi.com",
    "method": "GET",
    "path": "/pairs/小さい32hex_大きい32hex",
    "oldHmacStatus": 401,
    "oldHmacCode": "BAD_TOKEN",
    "newHmacStatus": 404,
    "newHmacCode": "NOT_FOUND"
  }
}
```

```powershell
node scripts/import-key-snapshot.mjs --input <snapshot.json> --sha256 <SHA256> --count <件数> --remote --work-dir <絶対パス> --legacy-writers-fenced --fence-version <実version UUID> --fence-evidence <非秘密JSON> --snapshot-approved --snapshot-provenance <取得元・承認者・カットオフ説明> --activate
```

CLI は snapshot SHA / 件数 / 承認・provenance・フェンス証跡を作業領域の receipt JSON に保存する。
2026-11-01 の承認済み候補は 10 鍵、raw SHA256
`aeadbfff25404b3a847a14a7a7757ee39aa7d45f2caa71a0d905502494e895aa` をカットオフ正本とする。
これは KV の収束や全旧 isolate の物理 drain の証明ではない。旧実行が遅れて KV に書いても
新 D1 正本には反映せず、遅れて発行した旧 HMAC token も新 version では拒否される。旧 KV は保持し、
旧 HMAC や旧 KV writer への rollback はしない。このフェンスの対象は新 D1 鍵正本と新 version の
HMAC 認可であり、既存 WebSocket、認可済みの in-flight 処理、発行済み pairing nonce、token を
要求しない optional relay 経路の取り消しを保証しない。snapshot 以降の KV 値を自動 import しない。

### ペア成立通知の再送

create / link は nonce claim、正式 pairs、配送世代、固定 payload の両宛先 outbox を同じ D1 transaction
へ保存する。DeviceDO の durable inbox 受理後だけその event / 宛先行を削除し、一時障害は確定済み
ペアを取り消さない。再送で `createdAt` / 1時間 TTL を延長せず、at-least-once（重複し得る）で配る。

既存の日次一時データ清掃を保ち、毎分 cron では due index から最大50宛先だけを処理する。
失敗は30秒から最大5分まで backoff し、本人の inbox 接続前にもその宛先を drain する。
空でも毎日1,440回の indexed D1 read が増える。全 DeviceDO の列挙・起動は行わない。

unpair は両宛先の outbox を同じ transaction で取消し、永続 revision / tombstone を進める。
DeviceDO は受理・保存・再接続 flush 時に正本の最新世代を確認し、受理済み新世代から旧世代へ戻らない。
遅延した旧 pair / unpair は新しい成立を上書きしない。分散操作の瞬時の全体線形化や、旧クライアントの
watch 開始前の古いイベントまで無期限に復元する保証は追加しない。

実 workerd / D1 / WebSocket のローカル回帰は、既存 wrangler の Miniflare / esbuild を使う。

```powershell
node tests/workerd-regressions.mjs --work-dir <新規の検証作業領域の絶対パス>
```

```powershell
# ローカル実行 (DO は memory mode、本番と微妙に挙動が違う点に注意)
wrangler dev

# ログをリアルタイム監視 (デプロイ後)
wrangler tail
```

`wrangler dev` の DO はメモリモードで本物の Durable Object とは挙動が違うため、Hibernation 周りの確認は必ず一度デプロイして確認すること。

## ロールバック

- **Workers 緊急停止**: Dashboard → Workers & Pages → ferry-relay → ⋯ → Disable
- **コードロールバック**: `wrangler rollback` (直前のデプロイに戻す) または `git revert` してから `wrangler deploy`

## プロトコル仕様

リレーは pairId ごとに 1 つの Durable Object インスタンスを割り当て、その内部で 2 つの WebSocket peer を相互に中継する。

```
Client A ──┐
           ├── RelayDO (pairId hash) ── 相互パススルー
Client B ──┘
```

| 項目 | 値 |
|---|---|
| 接続パス | `/ferry-relay` (`/health` はヘルスチェック) |
| 必須クエリ | `pairId=<id>` `role=<offer\|answer>` |
| pairId 形式 | `{32hex}_{32hex}`（C# `Util.PairId.Generate` と一致）。外れると 400 |
| 入室認可 | `RELAY_AUTH_MODE=optional` の段階移行（有効 Bearer + D1 participant / legacy 小枠。後述） |
| quota | `RelayQuotaDO` が強整合に予約し、global breaker・同時 room 数・月次/セッション quota を制御 |
| 入室レート制限 | `RATELIMIT_RELAY` を CF-Connecting-IP で消費（60/60s）。quota の補助で、超過は 429 |
| DO ID 計算 | `SHA-256(pairId + "\|" + SALT)` の hex |
| 接続可能 peer 数 | 1 ペアあたり 2 peer (3 人目は 409 Conflict) |
| 接続成立通知 | DO が両 peer に `"ready"` テキストフレームを送る |
| メッセージリレー | バイナリフレームのみ。テキスト受信は protocol error (1003) で room を閉じる |
| 最大フレームサイズ | アプリケーション上限 1 MiB (`RELAY_MAX_FRAME_BYTES`)。Cloudflare の WebSocket 上限 32 MiB とは別 |
| 切断時 | 片側が切れたら相手側を `1001 Peer disconnected` で close |

### 入室認可と quota の段階移行

現行設定は `RELAY_AUTH_MODE = "optional"` / `PAIR_LEDGER_MODE = "transition"` である。
入室時の判定は次のとおりで、**invalid bearer は legacy に降格せず拒否**する。

| 条件 | 適用する枠 |
|---|---|
| 検証済み Bearer かつ D1 `pairs` の participant | auth 枠 |
| Bearer なし、または台帳移行前で participant を確認できない | legacy の小枠 |
| Bearer が存在するが署名・期限・participant 検証に失敗 | 拒否 |

現行クライアントはリレー用 Bearer を fail-closed で取得・送出し、取得失敗・空値・期限切れで接続しない。
Bearer をまだ送らない出荷済み旧版だけが legacy 枠を使う。普及後は
`RELAY_AUTH_MODE=required` / `PAIR_LEDGER_MODE=required` へ反転し、legacy 経路を廃止する。

`RelayQuotaDO` は SQLite-backed Durable Object として、次の予約を強整合に直列化する。
予約は入室前に行い、セッション中のフレーム処理は lease の bytes/messages/duration/frame 上限で制御する。
クラッシュ・強制終了・異常切断では reservation を返却せず、予約分を全消費扱いにする（安全側に倒して同時超過を防ぐ）。
Rate Limit は DO を起こす乱打を抑える補助であり、quota の正本ではない。

| 対象 | bytes | messages | duration | idle / concurrency |
|---|---:|---:|---:|---:|
| global | — | — | — | `RELAY_MAX_CONCURRENT_ROOMS=16`、`RELAY_CIRCUIT_OPEN` |
| global 月次 (`RELAY_MONTHLY_*`, auth + legacy 合算) | 500 GiB | 10,000,000 | 500 h | — |
| auth セッション (`RELAY_AUTH_SESSION_*`, `RELAY_AUTH_IDLE_SECONDS`) | 10 GiB | 200,000 | 3 h | 5 min |
| legacy 月次 (`RELAY_LEGACY_MONTHLY_*`) | 10 GiB | 200,000 | 10 h | — |
| legacy セッション (`RELAY_LEGACY_SESSION_*`, `RELAY_LEGACY_IDLE_SECONDS`) | 256 MiB | 8,192 | 15 min | 2 min |

設定値の正本は `wrangler.toml` の `[vars]` であり、Worker には文字列として渡す。
legacy は global 月次枠に加えて legacy 月次小枠も同時に消費する。
auth/legacy の新旧クライアントが同じ room に混在した場合は、共有 lease を legacy の小さい
セッション枠へ原子的に降格し、legacy 月次小枠にも予約する（auth 枠への相乗りは許可しない）。
同じ lease では `offer` / `answer` を各1回だけ入室させる。settle や応答の失敗後に同じ role が
再入室してセッション枠を反復利用することはできず、未確定予約は期限切れ時に全量消費として確定する。
quota 状態と次回 alarm は同じ Durable Object storage transaction で更新する。
RelayDO は reserve 待機中に room の close/settle 世代が変わった lease も accept せず、
実測 settle の完了後に未使用 reservation を処理する。
room 合算 counter は両 WebSocket attachment に複製し、片方が切断後の一覧から先に消えても
残存 attachment だけで全量を settle する。breaker と quota 設定不備は認証・D1 より前に遮断する。

`/inbox` は本人 Bearer に加えて device 別の接続レート制限と DeviceDO あたり最大 4 WebSocket を適用する。
`sessions` / `pairing_nonces` は 1 時間で失効し、日次 scheduled handler が期限切れ行だけを D1 から一括削除する。
公開 `/health` は設定と binding の readiness だけを確認し、呼び出しごとの D1/KV subrequest は発行しない。

リレーのデータ経路としては、今回 R2 ペイロード保管・TURN・BYO relay は採用しない。
必要性と実測を確認した後段の候補であり、アプリ更新配信に既存利用している R2 とは別の話である。

## なぜハッシュ化が必要か

`idFromName(pairId)` に生 pairId を渡すと、Firebase ログや CDP 等で pairId が漏れた瞬間に任意の第三者が同じ DO インスタンス (= 同じルーム) に到達できる。SALT 付き SHA-256 でハッシュ化することで、Worker 側コードを読めない攻撃者は同じ DO ID を計算できない。SALT は Workers Secrets で管理し、ソースコードには出さない。
