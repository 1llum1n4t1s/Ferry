# Ferry アーキテクチャ詳細

このファイルは `CLAUDE.md` から退避した詳細。毎ターンは要らないが、該当領域を触る前には必ず該当節を読む。

---

## アーキテクチャ

### 全体構造

Ferry は QR コードでペアリングし、TCP 直接接続（LAN）/ UDP ホールパンチ（NAT 越え P2P）/ WebSocket リレー（最終手段）で PC 間ファイルを P2P 転送するデスクトップアプリ。ファイル転送に特化しており、チャット機能は含まない。

- **`src/Ferry/`** — .NET 10 Avalonia UI デスクトップアプリ（Native AOT、クロスプラットフォーム: win-x64 / win-arm64 / osx-arm64 / linux-x64 / linux-arm64）
- **`infra/cloudflare/relay/`** — バックエンド一式 (TypeScript)。Cloudflare Workers + Durable Objects + D1 で **シグナリング（PairDO）/ プレゼンス + inbox（DeviceDO）/ ペア台帳（D1）/ WebSocket リレー（RelayDO、`wss://watashiba.kagayoi.com/ferry-relay`）/ QR ペアリング用 Bridge ページ（`public/` を Static Assets 配信）** を 1 Worker に集約。テストは vitest（`pnpm test`）。**`infra/cloudflare/relay/**` を main に push すると `deploy-relay.yml` が `wrangler deploy` で自動配信**（手動 `wrangler deploy` も可）。死活監視は `relay-healthcheck.yml`（15 分ごとの cron）。⚠️ 旧来は手動デプロイのみでリリースに紐づかず、本番が古いまま残り `/auth/token` が 426 を返す事故（2026-06-23）が起きたため CI 化した
- **`web/`** — ダウンロード用ランディングページ（`index.html` + Cloudflare Worker `worker.js` + `wrangler.toml`）。QR ペアリングの Bridge ページとは別物。`web/` 配下を main に push すると `deploy-landing.yml` が Cloudflare に配信
- **`tests/Ferry.Tests/`** — xUnit v3 + NSubstitute によるユニットテスト

> **Firebase は完全撤去済み（2026-07、v1.0.66 の Step 6/7）**。シグナリング・プレゼンス・ペアリング・Bridge 配信はすべて上記 relay Worker に一本化。旧 `src/Ferry.Bridge/`（Firebase Hosting 版 Bridge）・`FirebaseSignaling.cs`・`firebase-cleanup.yml` は削除済み。Firebase プロジェクト `ferry-edf09` 自体もシャットダウン済み（2026-08-01 完全削除予定）で、**v1.0.64 以前の旧バイナリはシグナリング不能**（v1.0.65+ の自動更新で CF 経路へ移行する）。

### Avalonia UI ネイティブ + MVVM サービス層

UI は Avalonia UI ネイティブ（AXAML）。手動 DI（`App.axaml.cs` で組み立て）。DI コンテナは未使用。

```
Views/                  → Avalonia AXAML + コードビハインド（MainWindow, TransferView, SettingsView）
ViewModels/             → CommunityToolkit.Mvvm の ObservableObject / ObservableProperty
Services/               → インターフェース (I*Service) + 実装 + Stub（テスト・開発用）
Infrastructure/         → CloudflareSignaling（+ CfTokenProvider 認証）, TcpDirectTransport, UdpHolePunchTransport, WebSocketRelayTransport, StunClient, FileChunker
```

主要サービスインターフェース:
- `IConnectionService` — ペアリング（QR）とオンデマンド接続（TCP / UDP / リレー）を管理
- `ITransferService` — ファイルチャンク転送（SHA-256 検証・レジューム対応）
- `IPeerRegistryService` — ペア情報の永続化（`%APPDATA%\Ferry\peers.json`）
- `ISettingsService` — アプリ設定（`%APPDATA%\Ferry\settings.json`）

### 接続フロー（3 階層フォールバック）

イベント駆動で固定タイムアウトに依存しない設計。**STUN は遅延実行**で、LAN で TCP 直結できるケースに STUN レイテンシを乗せない（offer-v1 は STUN 情報なしで即送る）:

1. **Offer 側**: TCP リスナー起動（**IPv6 デュアルスタック**、`IPv6Any + DualMode` の 1 ポートで v4/v6 両受け。v6 スタック無しは v4 にフォールバック）→ **offer-v1 送信（STUN 情報なし）** → TCP accept と Answer ポーリングを `WhenAny` で同時待機
2. **Answer 側**: offer 受信 → TCP 接続試行 → 結果を answer に `route` フィールドで通知
   - offer の `Ips` は **IPv4 + IPv6（GUA/ULA、リンクローカル等は除外・最大 3 個）を「v4[0], v6[0], v4[1], …」インターリーブ**で並び、Answer は各 IP 3s / 全体 5s 予算で**順次**試行（`TcpDirectTransport.GetLocalIpAddresses` / `IsAdvertisableIpv6`）。LAN の v4 即成功は従来最速のまま、**IPv4 が CGNAT でも end-to-end IPv6 なら UDP/リレーへ行かず TCP 直結**できる（相手側ルータの IPv6 SPI ファイアウォールが inbound を落とす環境では不成立→従来どおり UDP へ）。並列レースにしないのは「Answer が捨てた側の接続を Offer が accept する」不整合を避けるため。旧バージョン Answer は v4 ソケット固定で v6 エントリに即例外→次の IP へ進むだけで無害（プロトコル互換、`ConnectionInfo` 変更なし）
   - TCP 成功 → `route = "direct"` → 両側 TCP で接続完了
   - TCP 失敗 → `route = "needRelay"` → 双方が次ステップ（UDP）へ
3. **TCP 失敗時（UDP ホールパンチ）**: ここが非対称で**順序が肝**。外部エンドポイントの交換は更に非対称で、**Offer 側の endpoint は offer-v2 ペイロード（`ConnectionInfo.ExternalIp/ExternalPort`）で運ばれ、Answer 側の endpoint だけが `/sig/{pairId}/endpoint`（PairDO キー `endpoint:{answererDeviceId}`）経由**で渡る（rere #D-003 で per-sender 化。書き手は自分の deviceId キー、読み手はペア相手の deviceId キー）。
   - **Offer 側**: answer(needRelay) 受信 → STUN クエリ → **ExternalIp を載せた offer-v2 を自分の offer キー（PairDO `offer:{_deviceId}`）に上書き再送**（`SendSdpOfferAsync(pairId, _deviceId, …)`）→ Answer の外部エンドポイント（`endpoint:{peerId}`）を最大 10 秒待つ（`WaitForEndpointAsync(pairId, peerId)`）→ 取得後ホールパンチ
   - **Answer 側**: 最初に読んだ offer-v1 には ExternalIp が無い。**`WaitForOfferExternalIpAsync` で offer-v2（ExternalIp 付き）を最大 8 秒ポーリングして読み直してから**（`TryReadOfferOnceAsync(pairId, peerId)`）STUN → 自分の外部エンドポイントを publish（`SendEndpointAsync(pairId, _deviceId, …)`）→ ホールパンチ。MITM 防御（`offer.From == ペア相手` ＋ per-sender キー一致）は再読み分にも適用
   - ⚠️ **Answer 側は「最初に読んだ offer の ExternalIp 有無」でゲートせず、offer-v2 を待って読み直してからホールパンチする**。offer-v1 は常に ExternalIp が空なので、ゲートすると UDP を一切起動せず自分の endpoint も publish しない → Offer 側が endpoint 待ちでタイムアウト → **cross-NAT（別回線・別 NAT）で必ずリレーへ落ちる構造バグ**になる（実際に過去発生・修正済み）
4. **UDP 失敗時**: WebSocket リレーにフォールバック（`wss://watashiba.kagayoi.com/ferry-relay`、Cloudflare Workers + Durable Objects、Hibernation 対応）

> UDP ホールパンチの成功は **NAT タイプ依存**。修正後も両側が CGNAT / symmetric NAT（日本の IPoE 等で多い）だとホールパンチが抜けずリレーに落ちる。「UDP 修正＝必ず P2P」ではない点に注意。IPoE 環境の救済としては上記 **IPv6 TCP 直結**が先に効く（NAT 越え不要、相手側 FW の inbound 許可のみが条件）。

STUN は **Cloudflare 公開 STUN (`stun.cloudflare.com:3478`) を主、Google STUN (`stun.l.google.com:19302`) を従** の 2 サーバーフォールバック。IPv4 明示指定（`AddressFamily.InterNetwork`）。旧 VPS 自前 coturn (`1llum1n4t1.net:3478`) は Cloudflare 移行に伴い 2026-05 に撤去済み。

### 着信検知（接続ノック）と CF 使用量

着信 listener（`ListenForIncomingConnectionAsync`）は旧実装で `/sig/{pairId}/offer` を **400ms 間隔で常時ポーリング**しており、アイドルでも 1 ペアあたり ~20 万 req/日を relay Worker に流していた（2026-07 実測: 4 ペアで ~50 万 req/日）。v1.0.67 で「**接続ノック**」方式に移行:

- **relay Worker** (`signaling-routes.ts`): offer / probe-offer の POST 成功時、ペア相手の DeviceDO inbox WS へ `{type:"knock", pairId, from}` を push する。knock は **transient**（`devicedo.ts` の notify が storage に積まず接続中 WS にだけ送る。積むと INBOX_MAX=50 を溢れさせてペア成立イベントを押し出す + 次回接続時に stale replay される）
- **クライアント** (`ConnectionService`): inbox WS を 1 本（`_knockWatcher`、初回 `StartListeningForConnection` で遅延生成・全 listener 共有）張り、ノック受信で該当 Session の listener を `SignalKnock()` で即時に起こす。listener 自身は **単発読み（`TryReadOfferOnceAsync`）+ 安全網待機**（WS 接続中 `IdleListenPollMs`=**120s** / 切断直後 `IdleListenPollNoInboxMs`=3s / **切断が `NoInboxFastWindowMs`=120s を超えたら `IdleListenPollNoInboxSustainedMs`=10s**）に低頻度化。間隔の決定は `ResolveIdleListenPollMs`（切断開始時刻 `_inboxDownSinceMs` を全 listener が CAS で共有）。検知レイテンシは WS push で ms オーダー＝旧 400ms ポーリングより速い
  - ⚠️ **切断中の 3s を持続させないこと**。1 ペアあたり **28,800 req/日** を PairDO に流す。2026-07-31〜08-02 の実測で、inbox が張れないまま 3s ポーリングが続き PairDO が 1 日 **46,595 / 58,393 / 34,687 req**（正常値 ~2,600 の 18〜22 倍。2 ペア × 28,800 = 57,600 と一致）に跳ね、30 日ローリングで含有枠 100 万の **90%** を占めた。上限は送信側の `OfferAnswerWaitSeconds`(20s) が縛る（listener は offer 読み → TCP 試行 5s → answer 書込を 20s 以内に終える必要があるため 10s が上限。15s 以上は着信を落とす）。回帰は `ConnectionServiceKnockTests` の間隔 3 本
  - inbox WS の再接続は**正常クローズも例外と同じバックオフ経路**に通す（`CloudflareSignaling.InboxLoopAsync`）。旧実装はサーバ正常クローズ時に `ReceiveAsync` が例外を投げず内側ループを抜けるだけで、待機ゼロの即再接続になっていた。加えて段数リセットは接続成立時ではなく **`InboxStableSessionMs`=60s 以上継続したとき**に限る（「繋がる→即切れる」の間は常に最短間隔が選ばれバックオフが効かないため）。DeviceDO は CF 都合で落ちる（`Durable Object reset because its code was updated.` を実測）
- **probe offer の読み取り（`ReadProbeOffersAsync`）はノック駆動**: relay は offer / probe-offer の**どちらの POST でも**ノックを push するので、アイドル中に probe を毎周読む必要はない。`WaitForKnockAsync` の戻り値（true=ノック / false=安全網タイムアウト）で分岐し、**ノックで起きた周だけ probe も読む**。ノック配送自体が失敗した probe を取りこぼさないよう、安全網タイムアウトが `ProbeSafetyNetEveryNRounds`=5 回連続したときだけ読み直す（WS 接続中なら最長 10 分に 1 回）
- **answer / endpoint / probe-answer の待機は従来どおり能動ポーリング**（送信側が数秒〜20s で有界に待つ経路。knock 不要）
- 旧 `WaitForOfferAsync`（400ms 常時ポーラ）は撤去済み。回帰テストは `ConnectionServiceKnockTests`（ノックで即再読み / ノック無しで安全網間隔まで沈黙 / **アイドル中は probe を読み直さない** / **ノックで probe も読み直す**）と relay 側 `tests/knock.test.ts`

> **⚠️ この安全網ポーリング自体が次のボトルネックだった（2026-07 の Durable Objects 使用量調査）**。ノック化で 400ms ポーリングは消えたが、残った安全網が **1 周あたり `probe-offers` + `offer` の 2 リクエスト**を PairDO へ投げており、15s 間隔 × 2 req × ペア数で **PairDO が DO リクエストの 89%（87.8 万 req/月）** を占め、Workers Paid の含有枠 100 万を 138% まで超過させていた（`ferry-relay_PairDO` 87.8 万 / `ferry-relay_DeviceDO` 11.2 万 / `RelayDO` 0）。対策は上記の 2 点＝**安全網 15s→120s**（1/8）と **probe のノック駆動化**（1/2）で、合わせて **PairDO を約 5.5 万 req/月（-94%）** に落とす。どちらもノックが主経路である前提に乗っているだけなので、通常の着信検知レイテンシは変わらない。使用量は Cloudflare のカスタムダッシュボード「Kagayoi 有料枠の余裕（Workers Paid）」（30 日表示）で監視する。

### ペアリングフロー

1. PC-A がセッション登録（`POST /pair/session` → D1 の `sessions` + `pairing_nonces`）→ QR コード表示（Bridge ページ URL + sid + nonce + 長期 ECDH 公開鍵）
2. スマホで QR スキャン → Bridge ページ（`https://watashiba.kagayoi.com`、relay Worker の Static Assets・API と同一オリジンなので CORS 不要）が開く
3. Bridge ページ内カメラで PC-B の QR をスキャン
4. Bridge が `POST /pair/create`（**両 sid の nonce 値所有を D1 で server 検証** = ghost peer 注入防止・bearer 不要・IP rate limit）→ 両 PC の DeviceDO inbox（WebSocket）へペア成立を即 push
5. ペア情報 + PairSecret（交換した公開鍵から ECDH 導出）を `peers.json` にローカル保存

**PC コード貼付ペアリング（スマホ無しの直接ペア）**: 相手の 32hex コードを貼ると `SubmitPairingAsync` → `POST /pair/link`。認可は「自分の bearer（sidA は cfToken の claims 固定＝詐称不能）+ 相手セッションがアクティブ（相手の nonce **値**の所有は不要）」で、QR 経路（`/pair/create`）とは別の認可モデルとして明確に分離（v1.0.67 で CF 対応）。device rate limit 付き。

### Cloudflare バックエンド構造（relay Worker）

認可は `POST /auth/token` が発行する **cfToken（自前 HMAC bearer、1h）** を全リクエストで検証する。`CfTokenProvider` が ECDSA P-256 署名チャレンジで取得・約 50 分ごとに refresh。deviceId↔公開鍵は KV `DEVICE_KEY_BINDING` に **first-write-wins** で束縛され、鍵不一致は 401 `DEVICE_PUBKEY_MISMATCH` → `IdentityLost` イベント（clean slate UI）。外部 ID プロバイダ非依存で SSE 再購読も不要。

| ルート | 実体 | 内容 |
|---|---|---|
| `/sig/{pairId}/offer・answer・endpoint`（per-sender）、`probe-offer/{nonce}`・`probe-offers`・`probe-answer/{nonce}`（per-nonce） | **PairDO**（pairId ごと 1 DO） | storage キー `offer:{sender}` / `answer:{sender}` / `endpoint:{sender}` / `probeOffer:{nonce}` / `probeAnswer:{nonce}`。当事者検証 + sender キー強制（`X-Ferry-Device`）は Worker 側（`signaling-routes.ts`）で完結し、旧 Firebase rules の per-sender なりすまし防止（#D-003）をコードで担保 |
| `/presence/{deviceId}`（POST/DELETE=本人のみ、GET・`/last-seen`=認証済みなら可） | **DeviceDO**（deviceId ごと 1 DO） | presence（`lastSeen` は server now）。`/last-seen` は ETag/304 対応（帯域節約） |
| `/inbox`（WebSocket） | DeviceDO | ペア成立通知の真 push + **接続ノック**（§着信検知）。未読はキュー（TTL 1h・最大 50 件）に積んで接続時 flush。knock は transient で積まない |
| `/pair/session`・`/pair/create`・`/pair/link` | **D1** `ferry_ledger`（`sessions` / `pairing_nonces` / `pairs`） | セッション登録・QR ペア成立・コード貼付ペア成立（認可モデルは上記） |
| `/pairs/{pairId}`（PUT/GET/DELETE、bearer 当事者のみ） | D1 `pairs` | ペア台帳 SSoT。GET 404 で remote-unpair 検出（`PairSyncService`）。DELETE は相手 inbox へ unpair push |
| `/ferry-relay`（WebSocket） | **RelayDO** | 転送リレー本体（Hibernation 対応）。pairId は `SALT` 付き SHA-256 で DO 名化（生 pairId 漏洩による横入り防止）。⚠️ **入室認可は未実装**（下記） |

**⚠️ `/ferry-relay` の入室認可は未完（移行途中）**: `index.ts` は `Upgrade: websocket` + query の `pairId`/`role` だけで RelayDO へ入れており、cfToken も当事者検証も無い。pairId は deviceId の Ordinal 連結で**決定的に導出できる**ため、相手の deviceId を知る第三者は同じルームに入って 2 スロットを埋め、正当な合流を 409 で遮断できる（PairSecret を持たない旧ペアでは中継データの盗聴・改竄も成立する。`SALT` ハッシュは `idFromName` の推測防止であって入室防御ではない）。恒久対策は `/sig/*` と同じ **Bearer 必須 + pairId 当事者検証**だが、必須化した瞬間に **Bearer を送らない出荷済みクライアントのリレー転送が全滅する**。そのため段階移行にしてあり、**クライアント側の Bearer 送出だけ先行実装済み**（`WebSocketRelayTransport` の `bearerTokenAsync`、`App.axaml.cs` が `CfTokenProvider.GetCfTokenAsync` を注入）。**サーバ側の必須化は Bearer 付きクライアントが行き渡ってから**行う（それまでこの表の「入室認可」は無いものとして扱う）。

**Rate limit の枠分け**（`wrangler.toml` の `unsafe.bindings`）: `RATELIMIT_IP`(60/60s、bearer 無しの `/auth/token`・`/pair/create`) / `RATELIMIT_DEVICE`(30/60s、**低頻度**な `/auth/token`・`/pair/link`) / `RATELIMIT_SIG`(600/60s、`/sig/*` 専用) / `RATELIMIT_SESSION`(5/60s)。⚠️ **`/sig/*` に `RATELIMIT_DEVICE` を流用しないこと**。シグナリングは接続 1 回で **offer POST + answer GET 400ms ポーリング ≒ 52 req**、経路 Probe 1 回 ≒ 17 req を消費するため 30/60s では枠を焼き切り、**送信側が「相手が返した answer を読む GET」を自分で 429 させて必ず `PeerUnreachableException`（相手から応答がありません）で失敗する**（v1.0.70 で実際に発生。429 も枠を消費するのでポーリング継続中は回復せず自己閉塞する）。実測ピーク ≒ 90 req/分。回帰は `tests/signaling-ratelimit.test.ts`。

**Cleanup ポリシー**（旧 firebase-cleanup.yml の置換）:

- **PairDO（signaling）**: 書込時に 1h alarm を仕掛け、TTL 経過で `deleteAll` して休眠（lazy・全 DO を短間隔で起こさない）
- **probe（per-nonce）**: probe sender の finally で `CleanupProbeAsync(nonce)` により**即時削除**
- **D1 `sessions` / `pairing_nonces`**: 読み時に `created_at` が 1h 超なら失効扱い（`EXPIRED_SESSION`）
- **presence**: 削除でなく `LastSeen` の老化（`OfflineThresholdMs`=60s）で UI 側が offline 判定

`ConnectionInfo` の `Probe / From / Nonce` フィールド (v12-v14 追加):
- `Probe: bool` — true なら listening 側は経路 Probe 用と判定して通常 transport 確立をスキップ
- `From: string?` — 送信元 deviceId。自己 probe offer の listening 側スキップ用識別子
- `Nonce: string?` — bidirectional 同時 probe race 対策の per-probe 識別子 (v12 追加、v14 で key path として正規化)

### ローカライズ（18 言語）

UI 文言は `src/Ferry/Resources/Locales/<locale>.axaml`（`ResourceDictionary`）に置き、`App.axaml` が `x:Key` 付きの `ResourceInclude` として全ロケールを宣言する。**全 18 ファイルが同一のキー集合を持つ**のが契約（現在 175 キー）。

- **参照方法**: AXAML からは `{DynamicResource Text.Xxx}`（辞書差し替えで自動追従）、C# からは `App.Text("Xxx")`（`Text.` プレフィックスは付けない）。
- **フォールバック**: `App.SetLocale` は `en_US` を `MergedDictionaries[0]` に常駐させ、選択ロケールを後ろに Add する（後勝ち）。さらに各ロケールファイル自身も冒頭で `en_US` を merge する。**二重のフォールバックで欠損キーは英語表示**になる。
- **未解決時**: `App.Text` はキー未登録／`Application.Current == null`（ユニットテスト）のとき `$"Text.{key}"` をそのまま返す。テストがこの値を期待していると「辞書にキーが実在するか」は検証されない点に注意。
- **⚠️ C# 側で `App.Text` から組み立てた文言はロケール切替で自動更新されない**。計算プロパティは変更通知が出ず、`ConnectionStatusText` のような格納プロパティは古い言語のまま残る。保持側の ViewModel が `App.LocaleChanged`（static イベント）を購読し、`RaiseLocalizedTextChanged()` で再通知する。購読者は `Dispose` で必ず解除する（static イベントなのでリークする）。現在の購読者は `ConnectionViewModel`（PairedPeer 全件 + `RebuildVisiblePeers`）と `TransferViewModel`（TransferItem 全件）。
- **キー追加時**: `en_US.axaml` と `ja_JP.axaml` は必須。残り 16 言語も揃えるのが原則（揃えないと英語にフォールバックする）。追加後は「使用キーが en_US に全て存在するか」「18 ファイルのキー集合が一致するか」を機械的に確認する。

### Native AOT 制約

- JSON シリアライズは Source Generator 必須（`FileMetaJsonContext`, `PeerRegistryJsonContext`, `ConnectionInfoJsonContext`, `AppSettingsJsonContext`, `CfJsonContext`〔CloudflareSignaling の API DTO〕, `CfAuthJsonContext`〔/auth/token DTO〕）。リフレクションベースのシリアライズは使わず、上記 source-gen コンテキストを使う
- `ConnectionInfo` にプロパティを追加する場合は `ConnectionInfoJsonContext` を更新する

### プラットフォーム差の吸収（Win / mac / Linux）

OS 依存処理は実行時分岐（`OperatingSystem.IsWindows()/IsMacOS()/IsLinux()`、AOT でトリミング安全）で 1 箇所に閉じ込める。新たに OS 依存コードを足すときは下記の既存吸収点に倣う。

- **ログイン時自動起動** (`Util.AutoStartManager.Apply`): `SettingsService.SetAutoStart` が委譲。**Win=レジストリ Run キー** / **mac=`~/Library/LaunchAgents/com.1llum1n4t1s.ferry.plist`（`RunAtLoad`、`.app` なら `open` で起動）** / **Linux=`$XDG_CONFIG_HOME/autostart/ferry.desktop`（AppImage 時は `$APPIMAGE` を Exec）** を生成/削除する。設定 UI（`AutoStartWithWindows` トグル）は全 OS で機能し、ラベルは OS 中立文言（「ログイン時に起動」/「Start at login」）。JSON プロパティ名 `AutoStartWithWindows` は既存 `settings.json` 互換のため**そのまま維持する（改名しない）**。`App.axaml.cs` 起動時に有効なら `SetAutoStart(true)` を冪等再適用し、更新で実行パスが変わっても追従する（self-heal）
- **多重起動の前面化** (`SingleInstanceGuard`): 上記のとおり Mutex + Named Pipe で全 OS 対称
- **× ボタン / 最小化トレイ格納** (`MainWindow.OnClosing` / `WindowStateProperty` observable): **macOS は × で終了せず `Hide()`**（赤信号ボタン慣習。終了はメニューバー「終了」/Cmd+Q。これがないと転送中 transport が切れる）。最小化トレイ格納（`ShowInTaskbar=false`+`Hide`）は **Win/Linux 限定**（mac は最小化=Dock 慣習なのでスキップ）
- **ファイラ起動** (`Util.ShellHelper.OpenFolder`): Win=`explorer.exe` / mac=`open` / Linux=`xdg-open`。非 Windows は `ArgumentList` でパスを渡す
- **通知音** (`Util.NotificationSound.Play`): 受信完了時に `TransferService.CompleteReceive`(検証成功・AutoAccept 経路含む)から呼ぶ。Win=`MessageBeep`(user32 P/Invoke。`System.Media.SystemSounds` は Windows 専用アセンブリ依存で cross-plat net10.0 から参照不可) / mac=`afplay Glass.aiff` / Linux=`canberra-gtk-play`→`paplay`。設定 `EnableNotificationSound` が ON かつ送信元ピアが `AppSettings.MutedPeerIds` に無いときのみ鳴らす(best-effort、失敗は無視)。`MutedPeerIds` はこのゲートが唯一の consumer（populate する per-peer ミュート UI は未実装の足場）
- **macOS Local Network 許可**: LAN 直結（TCP/UDP）は macOS のローカルネットワークプライバシ対象。`build/resources/app/App.plist` に `NSLocalNetworkUsageDescription` を持たせ、初回プロンプトに自前文言を出す（拒否されると直結不可→リレー転落）。mDNS/Bonjour 不使用のため `NSBonjourServices` は不要
- **データ配置パス** (`Util.AppPaths`): ログ出力先を OS 別に解決（Win=`%LOCALAPPDATA%\Ferry\logs` / mac=`~/Library/Logs/Ferry` / Linux=`~/.local/share/Ferry/logs`）。`LocalApplicationData` の mac 非慣習・空文字化リスクを明示パスで回避。settings/peers は移行リスクで `ApplicationData` 据置（詳細は §ログとデバッグ）
- **ファイアウォール** (`FirewallHelper`): Windows のみ netsh で受信許可。mac は署名済みアプリの初回 listen 時に OS が許可ダイアログ、Linux は ufw/firewalld 手動許可（いずれも未許可なら直結失敗→リレー）

### 自動更新と配信（CI/CD）

Velopack による自動更新の配信元は **Cloudflare R2**（カスタムドメイン `https://ferry.kagayoi.com`、bucket `ferry-updates`）。クライアントは `App.axaml.cs` の `UpdateBaseUrl` 定数 + `Velopack.Sources.SimpleWebSource` で更新を取得する（旧 `GithubSource` から移行済み）。`Check4Update` は起動時 + 24時間ごとに実行。

**Windows リリース (ローカル実行)**: `pwsh scripts/release-local.ps1` — Lhamiel で確立したローカル署名付きリリースフローの横展開。コード署名 (Authenticode、Certum **Open Source Code Signing in the cloud**、CN=`Open Source Developer Yuichiro Shinozaki`) は SimplySign Desktop のトークンログイン中セッション + スマホ OTP が必要で GitHub Actions からは署名できないため、win-x64 / win-arm64 の 2 チャンネルはローカルスクリプトでリリースする。スクリプトは publish (Native AOT) → `vpk pack` + **Authenticode 署名** (`--signParams`、タイムスタンプ `http://time.certum.pl`) → 署名検証 → `wrangler` (pnpm dlx) で R2 バケット `ferry-updates` にアップロード (manifest は最後) → 配信確認 (`releases.{channel}.json` HTTP 200) → **manifest 外の旧配布物を Cloudflare API V4 で自動削除** (Aggressive 保持戦略。⚠️ 対象は `*.nupkg` だけでなく**バージョン文字列 `x.y.z` を含む全オブジェクト**〔zip / deb / rpm / AppImage 含む〕で、manifest 参照分と**直近 2 世代**〔`KEEP_VERSIONS` / `$KeepVersionCount`〕だけを保持する。今回ビルドしないチャンネルの manifest は R2 から取得して keep set に加えるため、macOS / Linux の配布物は誤削除しない。ランディングページの DL リンクはバージョン文字列を含まない固定名なので対象外) まで一括実行。Cloudflare トークンは `C:\Users\IMT\dev\Secret\secrets.json` の `cloudflare.api_token` を実行時に読む。動作確認は `-SkipUpload` (ビルド + 署名のみ)、RID 絞り込みは `-Runtimes win-x64`。**実行前提: SimplySign Desktop がトークンログイン済み** (証明書が CurrentUser\My に見えること。スクリプトがプリフライトで検査して落とす)。**`/vava` は `vava.config.json` の `localRelease` キーを読んでこのスクリプトを自動実行する**。

**macOS / Linux (CI)**: `release/**` ブランチへの push で `.github/workflows/release.yml` が発火し、以下を順に呼ぶ（GitHub Releases は使わず R2 単独配信）:

- `build.yml` — 5 ランタイムを Native AOT 発行（win-* は `package.yml` の portable zip 用に残置）
- `package.yml` — ユーザー向け配布物（zip / deb / rpm / AppImage）
- `velopack.yml` — Velopack 自動更新パッケージ（`vpk pack --channel <runtime>` → `releases.<channel>.json` + nupkg）。**win-x64 / win-arm64 は matrix から除外済み** — 未署名 win フィードがローカル署名リリースの成果物を R2 上で上書きしないため。**osx-arm64 は Developer ID 署名 + notarytool 公証**（一時キーチェーンに証明書 .p12×2 をインポート → `notarytool store-credentials`（**app-specific password 方式**）→ `vpk pack` に `--signAppIdentity` / `--signInstallIdentity` / `--notaryProfile` を渡して .app codesign → .pkg productsign → 公証 → stapler を自動実行。要 Apple Secrets 8 個、手順は [`docs/operations/macos-signing.md`](docs/operations/macos-signing.md)。⚠️ 公証は **app-specific password 方式必須** — App Store Connect API キー方式は Team Key + Developer 権限でないと `invalidAsn1` で失敗する。`matrix: fail-fast: false` で osx 失敗時に linux を巻き込まない）。linux は署名不要
- `r2-upload` job — フィードとインストーラを `wrangler` で R2 にアップロード（要 Secrets: `CLOUDFLARE_API_TOKEN` / `CLOUDFLARE_ACCOUNT_ID`）。**cleanup は R2 上の `releases.win-*.json` を keep set に取り込む**（CI 成果物に win manifest が無いため、取り込まないと署名済み win nupkg を「manifest 外」と誤判定して削除する。取得失敗時は安全側で cleanup を中止）

> **relay Worker の自動デプロイ（release/** とは独立・main の path 変更でトリガー）**: `deploy-relay.yml` が `infra/cloudflare/relay/**` 変更時に `wrangler deploy` で自動配信する（手動デプロイ忘れによる「コードと本番の乖離」事故＝2026-06-23 の `/auth/token` 426 の再発防止）。Bridge ページ（`public/`）もこの Worker の Static Assets なので同時に配信される。

> ℹ️ `package.yml` の win portable zip (`ferry_*.zip`) は引き続き CI で生成される未署名バイナリ（ランディングページ未参照のため影響は限定的）。署名対象に含めたい場合はローカルスクリプトへの移植が必要。

バージョンは `Directory.Build.props` の `<Version>` 単一管理（CI では `version` job が抽出、ローカルスクリプトも同ファイルを読む）。GitHub Actions はコミット SHA で固定。

> アプリ内で使う `Ferry.AppVersion.Value`（About 表示 / presence のバージョン報告 / `IgnoreUpdateTag` の陳腐化判定）は、`Ferry.csproj` の `GenerateAppVersion` ターゲットが `$(Version)` から `obj/**/AppVersion.g.cs` を毎ビルド生成する。**手書きの `AppVersion.cs` は置かない**（Native AOT でリフレクションに頼れないため compile-time 定数である点は維持しつつ、正本を 1 つにしてドリフトを構造的に防ぐ。旧・手書き定数は実際に 3 リリース分ズレて `IgnoreUpdateTag` の自動クリアが効かなくなっていた）。

### 転送プロトコル

TCP / WebSocket 上の長さプレフィクス付きバイナリプロトコル（`TransferProtocol.cs` + `FileChunker.cs` + `LengthPrefixedStream.cs`）。チャンクサイズ **64KB** (P-15 で旧 16KB から 4 倍化、`TransferProtocol.ChunkSize` 定数参照)。

メッセージ種別一覧 (`TransferProtocol.cs` の `const byte` で定義):

| 種別 | 値 | 用途 |
|------|------|------|
| FileMeta | 0x01 | ファイル名 / サイズ / チャンク数 / TransferId / 相対パス |
| FileChunk | 0x02 | `[0x02][TransferId 16byte][chunkIndex 4byte][data]` (ヘッダ長 ChunkHeaderSize=21) |
| FileAck | 0x03 | 受信側が SHA-256 検証結果を送信側に通知 |
| FileReject | 0x04 | 拒否通知 (`[0x04][TransferId 16byte][reason UTF-8]`) — v12 で TransferId プレフィクス追加 |
| **FileHash** | **0x05** | SHA-256 ハッシュ後送り (送信側が全 chunk 送信後に送付、P-3 で導入) |
| **FileApprove** | **0x06** | 受信承認通知 (受信側が承認時に送信、送信側はこれを待ってチャンク送信開始、v1 で導入) |
| **FileFlowAck** | **0x07** | フロー制御 ACK (`[0x07][TransferId 16byte][receivedChunkCount 4byte]`)。受信側が `FlowAckIntervalChunks`(64=4MB) ごと + 完了時に書き込み済みチャンク数を返す。送信側は `FlowControlWindowChunks`(512=32MB) を超えて先行しないよう待機 (v1.0.46 で導入、後述「リレー経路のフロー制御」) |
| Ping / Pong | 0x10 / 0x11 | キープアライブ |
| ResumeRequest / ResumeResponse | 0x20 / 0x21 | レジューム関連 (現状応答は false 固定) |

受信側（`TransferService.HandleFileChunk`）は **TransferId で受信状態を引き、`chunkIndex × ChunkSize` のオフセットへ `Seek` して書き込む**ため、UDP の順不同到着でも正しく再構成できる。受信完了は全 chunkIndex 受信（ビットマップ `ReceivedChunkSet`）で判定し、最後に SHA-256 でファイル整合性を検証する。受信ファイル名・相対パスはパストラバーサル防止のため保存先ディレクトリ配下に収まることを検証する。検証ロジックは純関数 `Util.SafePath`（`NormalizeSeparators` / `HasParentTraversal` / `HasUnsafeRoot` / `SafeFileName` / `IsWithinDirectory`）に集約。**送信元 OS のパス区切りに依存しない**よう受信した `FileName`/`RelativePath` を `\`→`/` 正規化してから basename 抽出・`..` パス要素判定し（Windows 送信 → mac/Linux 受信の混在を吸収。単独ファイル経路も正規化して非対称を解消）、最終防御は `StartsWith` ではなく `Path.GetRelativePath` ベースで saveDir 配下を強制する（区切り・大小・正規化のクロス OS 差を OS 既定の比較規則に委ねる）。加えて **NUL 等の制御文字を含む `FileName`/`RelativePath` は `HandleFileMeta` 冒頭で早期 `FileReject`**（`SafePath.ContainsControlChar`）し、`SafePath.IsWithinDirectory` も例外安全化（throw せず false に倒す）する。これが無いと細工 `FileMeta` の NUL で `Path.*` が `ArgumentException`→受信ループ→`ChannelClosed` で進行中転送を切断できる**リモート DoS**（ペア済み peer から 1 通で発火、early-return しないので `FileReject` も飛ばない）になる。シンボリックリンク追跡は文字列防御の対象外（攻撃には saveDir への事前書込権限が必要で、信頼モデル§の設計途上事項）。回帰は `SafePathTests`。

UDP ホールパンチ経由の場合は `UdpHolePunchTransport` が信頼性レイヤー（選択的 ACK・フラグメンテーション 1187 bytes・スライディングウィンドウ 128）を提供する。順序保証はトランスポート層ではなく上記の chunkIndex ベース書き込みで担保している。

**リレー経路のフロー制御 (v1.0.46 追加)**: WebSocket リレー (`ClientWebSocket.SendAsync`) はローカル送信バッファ受理で即返るため、TCP/UDP のような end-to-end バックプレッシャーが効かない。これが無いと送信側が受信側のドレイン速度（多くは受信側のダウンロード帯域）を超えてチャンクを流し込み、Cloudflare 中継バッファが膨張して **転送開始 ~55秒で接続が close handshake 無しに切断**される（大容量ファイルのみ再現。小さいファイルは溢れる前に完了）。対策として `FileFlowAck (0x07)` によるアプリ層スライディングウィンドウを導入: 受信側が `HandleFileChunk` で `FlowAckIntervalChunks`(64) ごと + 完了時に書き込み済みチャンク数を返し、送信側 `SendChunksAsync` は `index - FlowAckedChunks >= FlowControlWindowChunks`(512=32MB) の間 `Task.Delay(10)` で待機（`FlowAckStallTimeoutMs`=60s で打ち切り）。これで中継バッファを ~32MB に抑え、転送は受信側帯域で律速されつつ完走する。TCP/UDP 経路では各 transport の自然なバックプレッシャーが先に効くため待機はほぼ発生しない。FlowAck は累積カウントなので 1 個欠落しても次の ACK で回復する。

> **なぜ ~55秒切断が起きたか / なぜ 32MB が安全か**: Cloudflare Durable Object は 1 インスタンス 128MB メモリ割当で、その送信 WebSocket は backpressure 未実装 (workerd#988)。FlowAck が無いと受信側ドレイン未了分が DO メモリに線形に積まれ、128MB 超過で isolate がメモリ超過リセット → close handshake 無しに切断する (これが「~55秒」の正体)。FlowAck 導入後はリレー DO に積まれる未ドレイン分 ≒ 送信先行 32MB + wire/受信 OS バッファ数MB ≈ 最悪 ~40MB で、128MB に対し約 3〜4 倍マージン。よって窓 512 (=32MB) は縮める必要がない (縮めると高 RTT でスループット低下)。Cloudflare の WebSocket メッセージ上限は 2025-10-31 に 1 MiB → 32 MiB へ引上げ済みで 64KB チャンクには無関係。
>
> **フロー制御が実際に発火したかの確認 (v1.0.47 で診断ログ追加)**: `SendChunksAsync` の窓待機が初めて発火した時に Info ログ「フロー制御 window 発火（受信ドレイン律速に移行）」を 1 度だけ出す (`%LOCALAPPDATA%\Ferry\logs`)。**このログが出ていれば送信が受信ドレインに律速された＝中継バッファは 32MB で頭打ち**。出ないまま完走した場合は (a) ファイルが窓 (32MB) 未満で構造的に未行使 (b) 受信が十分速い、のどちらか。受信側で帯域を絞っても送信が減速しないように見える時は、まず ① 経路が Relay か (`接続完了！ 経路:` ログ) ② ファイルが 32MB 超か ③ 絞りが受信↔Cloudflare edge のソケット読み出しに実効しているか (Relay ではローカル帯域絞りは受信↔edge 間にしか効かず、送信→DO 流入は独立) を確認する。受信側を一時停止すると送信側が 512 chunk で確実に待機に入る。`HandleFileFlowAck` も Debug ログで ack 到達と item 解決 (`found`) を出す。

> **レジュームは「先頭から再送」方式**（`ResumeTransferAsync`、`startChunk=0`）。受信側は承認時にファイルを再作成するため、部分再送ではなく全チャンクを送り直す。
>
> チャンクメッセージ形式は 2026-05 に `chunkIndex` 単独から `TransferId + chunkIndex` に変更済み。**旧形式とは非互換**（既存の配布クライアントは存在しないため移行問題なし）。形式を再度変える場合は送受信（`FileChunker.CreateChunkMessage` ↔ `HandleFileChunk`）と `FileChunkerTests` のオフセットを揃えること。

### 承認プロトコル (v1〜v8 で大改修)

ファイル送信は送信側が `FileMeta` 送信 → **受信側の `FileApprove` (0x06) を 60 秒待つ** → 承認受信後にチャンク送信開始、というフロー。AutoAcceptFileTransfer 有効時は受信側が即承認を返す。

- 受信側拒否時は `FileReject (0x04, TransferId プレフィクス付き)` を送信
- 送信側 60s タイムアウト時も `FileReject` を受信側に投げて **symmetric expiry** (v8)
- `HandleFileMeta` の early return (パストラバーサル / 保存パス異常 / dir 作成失敗) でも `FileReject` を sender に送信 (v7)
- `HandleFileReject` は 4 ケース対応: `_pendingSendApprovals` / `_activeTransfers` / `_pendingApprovals` / `_receiveStates` (race ケース) (v8)
- 拒否理由は `item.ErrorMessage` に詰めてから TCS 解決して UI に伝える (v12)
- `SendRejectFireAndForget(Guid, string)` ヘルパーで `TransferService` 内の FileReject 送信を統一 (v9)

### 転送 UI / 操作と接続短縮 (v1.0.47)

転送履歴の宛先別表示・送信操作（再送 / 一時停止 / キャンセル）・受信保存先の常時表示・多重起動防止を追加した。送信時は VM 側 item とサービス側 item が別インスタンスのため、`SendFileAsync(filePath, relativePath, transferId, ct)` に **TransferId を渡して両者を同一 ID で相関**させる（受信は VM とサービスが同一 TransferItem を共有）。

- **宛先別履歴**: `TransferViewModel` は全件を `Transfers` に保持しつつ、選択中ピアに属する項目だけを `VisibleTransfers` に投影（`TransferItem.PeerId` で判定）。`ConnectionViewModel.SelectedPeer` 変更を購読して `RebuildVisibleTransfers`。`TransferView.axaml` の `ListBox`（`Classes="transferList"`）は `VisibleTransfers` を bind。⚠️ 履歴は件数が青天井なので**仮想化必須**。外側を `ScrollViewer` で包むと `VirtualizingStackPanel` にビューポートが伝わらず仮想化が壊れる（旧 `ScrollViewer > ItemsControl` 構成が全行のビジュアルと各行の `MarqueeTextBlock` を生かし続けていた回帰）。また `RebuildVisibleTransfers` は`ReconcileVisiblePeers` と同じく **in-place 差分反映**で行う（`Clear()` 全置換は Reset 通知で全行が再生成されスクロール位置も飛ぶ）
- **対称キャンセル** (`CancelTransfer`): 送受信どちらからでも `FileReject` で相手に通知し、送信側は自分の `_sendCts`（`ConcurrentDictionary<Guid, CancellationTokenSource>`）を cancel、受信側は `_receiveStates` を破棄。`HandleFileReject` の `_activeTransfers` 分岐も `_sendCts` cancel を行う
- **一時停止 / 再開** (`PauseSendTransfer` / `ResumeSendTransfer`): 送信のみ対応。`_pausedSends`(`ConcurrentDictionary<Guid, byte>`) に TransferId を入れ、`SendChunksAsync` がチャンク送信ループ手前で `_pausedSends.ContainsKey` の間 `Task.Delay(100)` 待機。待機中は `TransferState.Paused`（色 `#FF9F0A`）を表示
- **自動リトライ**: `SendOneFileAsync` が `MaxSendAttempts=3` でリトライ。2 回目以降は `TransferItem.Note` に「リトライ中…(n/3)」を表示（`OnTransferError` は `_sendCtsByItem` 管理中の送信項目をスキップしてリトライループに委ねる）。`OperationCanceledException` はリトライせず Cancelled 扱い。**`PeerUnreachableException`（相手無応答＝オフライン/未起動/到達不可）もリトライせず即 Error 扱い**: offer に対し answer が `OfferAnswerWaitSeconds`(20s) 以内に来なかった場合 `ConnectionService.ConnectToPeerAsync` がこの専用型を投げる。相手がいないのに再接続を繰り返しても毎回 20s 待ちを空打ちするだけ（旧実装は一過性エラー扱いで 20s×3≒60s 浪費していた）なので、明確なオフラインメッセージを出してユーザーの手動「再送」に委ねる。**転送中の一過性切断（相手は生存・接続確立済み）はこの型を投げず従来どおりリトライ対象**
- **保存先アドレスバー**: 受信保存先を設定画面から `MainWindow` 上部の常時表示バーへ移動（📁 アイコン + readonly TextBox + 📂 で OS のファイラ起動 + 変更ボタン）。`SettingsView` 側の保存先ブロックは撤去
- **多重起動防止** (`SingleInstanceGuard`): 名前付き `Mutex`（`Ferry-SingleInstance-Mutex-v1`）で取得失敗時は **Named Pipe**（`Ferry-Activate-<user>-v1`）で既存インスタンスへ前面化シグナルを送って即終了。`Program.cs` の `VelopackApp...Run()` 直後に `TryAcquire`、`App.axaml.cs` で `StartActivationListener` を起動。`Mutex`・`NamedPipeServer/ClientStream` とも .NET 上で **Win/mac/Linux すべて対応**（Unix は UDS バック）なので、2 個目起動時の既存ウィンドウ前面化は全 OS で対称に動く（旧 `EventWaitHandle`〔Windows 専用〕から移行）
- **接続検出の短縮** (#5): offer/answer ポーリング間隔を 1000ms → 400ms に短縮し、相手の送信開始から受信開始までの待ちを削減（※着信 offer の**常時**ポーリングは v1.0.67 でノック方式に置換済み — §着信検知。400ms 高速ポーリングが残るのは接続確立中の有界待機〔answer / offer-v2 / endpoint〕のみ）

#### 追加修正 (v1.0.47 後半)

- **複数ファイルを即時 N 行表示**: `SendFilesAsync` は **先に全ファイル分の `TransferItem` を生成・`AddTransfer`**（State=Pending で即 `VisibleTransfers` に並ぶ）してから、`SendItemAsync(item, peer)` を 1 件ずつ直列に送る。旧実装は `foreach await SendOneFileAsync` で 1 件完了まで次行が出ない症状だった。`SendOneFileAsync` は「item 生成 + AddTransfer + SendItemAsync」の薄いラッパ（ResendAsync 用）に分割
- **相手表示名の伝播**: `PairedPeer.DisplayName` を明示バッキングフィールド + `SetProperty` に変更（plain プロパティのままなので AOT の `PeerRegistryJsonContext` シリアライズは維持）。`PresencePollLoop` の `peer.DisplayName =` 代入が変更通知を出し、**左ペイン（ピアリスト）も右ペインも更新**される。旧 plain auto-property は通知が無く左ペインが古い名前のままだった
- **経路バッジ「状態取得中」固着の解消**: `ConnectionViewModel.ProbePeerRouteAsync` は **probe が `Unknown` を返しても既に有効な Route を持つピアは据え置く**（転送中の probe 競合タイムアウトで `Unknown` 退行しない）。`RefreshPeersAsync` は接続中ピア（`_connectionService.ConnectedPeer?.SessionId == peer.PeerId`）には probe せず `_connectionService.Route` を即反映。offline 時の `Unknown` 化は `!isOnline` 分岐が担当
- **ファイルパスのマーキー**: `Controls/MarqueeTextBlock`（`Decorator` 派生・テンプレート非依存・子 TextBlock をコード保持・**クラス共通の単一 `DispatcherTimer`** で `TranslateTransform.X` を更新）。行ごとにタイマーを立てると流れている行数ぶん Dispatcher キューを埋めるため登録制にしている。`IsEffectivelyVisible` がfalse のインスタンス（設定タブ表示中・トレイ格納中）は tick 側でスキップし、全件不可視ならタイマーごと停止する。収まる時は静止、はみ出す時のみ左へ流す。`TransferView` の Row2 パス表示を差し替え
- **転送レート(bps)**: `TransferViewModel` の 1 秒 `DispatcherTimer`（`OnRateTimerTick`）が `VisibleTransfers` の InProgress 項目について **転送開始からの累積平均**（総転送バイト ÷ 経過秒）で bps を算出し `TransferItem.RateText` を更新（停止/完了/一時停止でクリア）。瞬間差分はチャンクのバースト/フロー制御待ちで乱高下するため累積平均に統一。整形は `Util.Formatting.FormatBitrate`（1000 区切り）。開始基準は素フィールド `RateStartBytes`/`RateStartTick`（一時停止/完了で `RateStartTick=0` にリセットし、停止区間を平均に含めない＝再開後は再開時点からの平均）
- **送受信日時**: `TransferItem.CreatedAt`（生成時 `DateTime.Now`）+ `CreatedAtText`（`yyyy-MM-dd HH:mm:ss`）を全履歴行（ファイル名行の右）に常時表示。重複回避のため `DisplayInfo` の完了時刻連結は撤去
- **受信フォルダを開くボタン**: 保存先バー（`MainWindow` 上部）の 📂 に一本化。OS ファイラ起動は `Util.ShellHelper.OpenFolder`、保存先は `MainWindow.axaml.cs` の `OnOpenSaveDirClick`（`_settingsService.Settings.SaveDirectory` 優先）から取得。`TransferView` ヘッダにも一時的に 📂 を置いていたが「開くボタンが 3 つある」状態を避けるため撤去し保存先バーのみに集約
- **Bridge の URL 貼り付けペアリング撤去**: `src/Ferry.Bridge/`（index.html + bridge.js）からモード B（URL ペースト）を削除。このページはカメラ付き端末（スマホ）でしか到達しないため。モード選択はカメラ 1 枚のみ（自動カメラ起動はしない方針は維持）

### 宛先リスト（v1.0.66 高機能化）

サイドバーのピア一覧は `ConnectionViewModel.VisiblePeers`（`ObservableCollection<object>`、見出し `PeerListSection` と `PairedPeer` を混在）への投影で描画する。純関数 `BuildPeerProjection(peers, search, mode, label, keep)` が検索フィルタ・ソート（`PeerSortMode`: 名前 / 最終転送 / 経路 / 転送中）・セクション分割（📌 ピン留め / オンライン / オフライン）を決定し、`ReconcileVisiblePeers` が **in-place 差分反映**する。⚠️ 一覧更新は必ず `ReconcileVisiblePeers` の in-place 差分反映で行う。`Clear()` による全置換は禁止 — `SelectedPeer` が null に振れて `TransferViewModel` が履歴をクリアし、転送ビュー消失・D&D 不能になる（実際に起きた回帰）。検索で選択中ピアが除外されても `keep` 引数で必ず一覧に残す。ピン留めは `PairedPeer.IsPinned`（peers.json 永続）、見出し行は `ContainerPrepared` で非選択・非フォーカス化。回帰は `PeerListProjectionTests`。

### プレゼンス監視（オンライン検出）

ConnectionViewModel が定期的に relay Worker（DeviceDO）へハートビート送信・ピアの lastSeen をポーリング。

```text
HeartbeatLoop (30秒):
  └ UpdatePresenceAsync(deviceId, displayName) → POST /presence/{deviceId}
    （lastSeen はサーバー時刻で記録。アプリの Version も載せる＝presence でバージョン分布が見える）

PresencePollLoop (30秒):
  └ ① 前面（表示中かつ非最小化）のときだけ実行。トレイ格納/最小化中は停止（Heartbeat は継続するので相手からは online のまま）
  └ ② 取得対象: 選択中ピアは毎サイクル / 他ピアは FullPollEveryNCycles(4=2分) に1回 / ピア未選択時は一覧鮮度のため毎サイクル全ピア
  └ ④⑤ GetPresenceLastSeenAsync(peerId): GET /presence/{peerId}/last-seen を ETag 条件付き（未変更なら 304 で本文ゼロ）
  └ now - LastSeen < 60秒 なら IsOnline = true（false→true で WentOnline → 経路 Probe 発火）
```

> ①〜⑤（可視性ゲート / 選択ピア優先 / 30s 間隔 / ETag 304 / LastSeen 単独取得）は Firebase 時代に DL 帯域節約のため導入した施策で、CF 移行後も**リクエスト数削減**としてそのまま有効。前面復帰時は `RefreshPeersAsync` で全ピア即フル取得（DisplayName 同期・経路再判定込み）。表示名同期はポーリングでは行わず、手動更新 / 前面復帰の `GetPresenceAsync`（フル取得）に委譲する。

### テスト

xUnit v3 + NSubstitute。テスト内の非同期メソッドには `TestContext.Current.CancellationToken` を渡すこと（xUnit1051 警告回避）。

### ログとデバッグ

**SuperLightLogger**（log4net 互換シム + 内蔵 File Target、Native AOT 安全）でファイル出力。出力先は OS 別に `Util.AppPaths.GetLogDirectory` が解決する: **Win=`%LOCALAPPDATA%\Ferry\logs`** / **mac=`~/Library/Logs/Ferry`**（慣習どおり Console.app から見える・常に存在し書込可。`LocalApplicationData` は mac で `~/.local/share` 隠し＝非慣習かつ空文字化のリスクがあるため明示パスに寄せている） / **Linux=`~/.local/share/Ferry/logs`**（XDG）。ファイル名は `Ferry_YYYYMMDD.log`。DEBUG は全レベル、Release は Info 以上（接続フォールバックの各段を本番でも追えるようにするため）。IP 等の PII はログ出力時に `Util.Logger.MaskIp` で末尾オクテットを伏せる。`Logger.Initialize` は失敗時に `%TEMP%`（mac/Linux は `$TMPDIR`）へフォールバックする（`Program.cs`）。なお settings.json / peers.json は DeviceId・ペア情報の移行リスクがあるため従来の `ApplicationData`（mac=`~/.config/Ferry`）配置のまま。`Util.Logger` は内部で `SuperLightLogger.ILog` を保持し、`LogManager.Configure(b => b.AddSuperLightFile(...).SetMinimumLevel("Trace"))` でローリング設定（旧 NLog から 2026-05 に移行）。

**通信デバッグのポイント:**
- SDP offer/answer の待機: `SDP 受信(CF, offer|answer)` / `SDP ポーリングエラー(CF, ...)` ログで状態を確認（404 継続 = PairDO に相手の書き込みが無い）。着信検知は `接続ノック受信`（Debug）と `CF inbox WebSocket 接続成立`（Debug）で追う
- 接続失敗時は常にログに原因が出力されるよう各所で `Util.Logger.Log(..., Util.LogLevel.Error)` を使用
- relay Worker 側は `cd infra/cloudflare/relay && pnpm dlx wrangler tail` でライブログ（DO の exception や knock 配送を確認できる）

