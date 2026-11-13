# Ferry 設計

この文書は、現在の実装におけるシステム全体の責務、境界、データフロー、不変条件、設計判断をまとめる。接続タイムアウトやプロトコルのフィールド等の実装詳細は [`references/architecture.md`](references/architecture.md)、開発・検証・リリース手順は [`AGENTS.md`](AGENTS.md) と [`CONTRIBUTING.md`](CONTRIBUTING.md) を正本とする。

## 目的と範囲

Ferry は、2 台の PC を QR コードまたはワンタイムコードでペアリングし、ファイルまたはフォルダを直接転送するデスクトップアプリである。チャット、サーバー上のファイル保管、クラウド同期は対象に含めない。

転送経路は TCP 直結、STUN を使った UDP ホールパンチ、Cloudflare WebSocket リレーの順に試行する。Cloudflare はペアリング、シグナリング、プレゼンス、着信通知と最終手段の中継を担うが、通常のファイル本体は PC 間で直接流す。

## 主要コンポーネント

| コンポーネント | 責務 | 境界 |
| --- | --- | --- |
| `src/Ferry/` | .NET 10 / Avalonia の UI、ペア管理、接続確立、暗号ハンドシェイク、転送、自動更新 | 利用者のローカルデータと OS 資源を所有し、Cloudflare とは認証済み API / WebSocket で接続する |
| `ConnectionService` | TCP 待受、STUN / UDP、relay への 3 段フォールバックと peer 単位の接続状態 | トランスポートの差を `ITransport` のストリームに畳み込む |
| `TransferService` | 承認、64 KiB チャンク、フロー制御、SHA-256 整合性検証、キャンセル | 保存先の確定とファイル作成は受信承認後だけに行う |
| `CloudflareSignaling` / `CfTokenProvider` | Worker API、inbox WebSocket、cfToken の取得・更新 | HTTP 詳細とデバイス認証をサービス層から分離する |
| relay Worker | 認証、ペアリング、シグナリング、プレゼンス、inbox、relay、Bridge 静的ページ | `watashiba.kagayoi.com` の公開境界。ファイル本体を永続保管しない |
| `PairDO` | pairId ごとの offer / answer / endpoint / probe 一時状態 | sender ごとの key と alarm による失効を強制する |
| `DeviceDO` | presence、inbox WebSocket、未読通知キュー | deviceId ごとの状態。接続ノックは永続化しない |
| `RelayDO` | 2 peer 間の binary WebSocket フレーム中継 | Hibernation を使い、平文テキストフレームと上限超過を拒否する |
| `RelayQuotaDO` | relay の月次・セッション quota、同時 room、idle、breaker の強整合予約 | `QUOTA` binding の global coordinator。入室前に lease を確定する |
| D1 `ferry_ledger` | 認証鍵、ペア台帳、通知 outbox、session / nonce | 認証鍵は不可変の正本。ペア通知は成立と同じトランザクションに保存し、1 時間の配送期限を持つ |
| R2 `ferry-updates` | 署名済み配布物と Velopack manifest の公開 | アプリ更新用。relay ペイロード保管には使わない |

UI は AXAML + MVVM で構成し、`App.axaml.cs` がサービスを手動で組み立てる。DI コンテナは使用しない。Windows x64 / ARM64、macOS Apple Silicon、Linux x64 / ARM64 を Native AOT で配布する。

保存された言語は初回ウィンドウ生成前に適用し、書字方向は共通ウィンドウスタイルから後発の画面にも反映する。保存サイズ・位置・最大化状態は初回表示前に復元する一方、起動時最小化はネイティブ表示処理との競合を避けるため初回 `OnOpened` 後の idle ディスパッチで一度だけ適用する。トレイや Dock からの復帰では起動時状態を再適用しない。OS ごとの表示差は [`references/architecture.md`](references/architecture.md) の「プラットフォーム差の吸収」を参照する。

## 主要データフロー

### ペアリング

1. 各 PC が長期 ECDH 公開鍵を含む一時セッションを D1 へ登録し、QR コードを表示する。
2. スマートフォンの Bridge ページが 2 台分の nonce を Worker へ送る。
3. Worker が nonce を不可分に claim し、D1 `pairs` と両 device 宛ての通知 outbox を同じトランザクションで作る。DeviceDO が通知を永続受理した宛先だけ配送済みにする。
4. 各 PC は公開鍵から PairSecret を導出し、ペア情報を `peers.json` へアトミックに保存する。

PC 間のコード貼付経路は、永続 deviceId と短命 nonce を組み合わせたワンタイムコードを使う。呼び出し元の cfToken と相手 nonce の所有を検証し、成立時に nonce を単回消費する。

通知の一時障害は毎分の有界な outbox 再送と、本人の inbox 接続時の再送で復旧する。再送で作成時刻や 1 時間の期限を延長しない。解除・再ペアには新しい世代を割り当て、古い成立通知や解除通知が新しい状態を上書きしないよう DeviceDO の保存・再生でも照合する。重複配送を許す再送方式とし、無期限配送やクライアント再起動後の古い通知受理は保証しない。

### 着信検知と接続確立

1. 各アプリは共有の inbox WebSocket を 1 本保持し、offer 書き込み時の接続ノックで該当 listener を起こす。低頻度 HTTP poll は WebSocket 障害時の安全網である。
2. offer 側は IPv6 dual-stack TCP listener を先に開き、STUN 情報のない offer-v1 を送る。answer 側は広告された IPv4 / IPv6 を順番に試す。
3. TCP 失敗後だけ両側が STUN を実行し、offer-v2 と answer 側 endpoint を交換して UDP ホールパンチを行う。
4. UDP も失敗した場合だけ、両 peer が同じ RelayDO room へ入室する。現行クライアントは fresh な cfToken が無ければ relay に接続しない。

STUN は Cloudflare (`stun.cloudflare.com:3478`) を主、Google (`stun.l.google.com:19302`) を従とし、自前の STUN / TURN は運用しない。

同時接続では deviceId の辞書順で大きい側が相手の新しい offer を確認できた場合、answerer の listener に委譲する。委譲先の接続完了を待ち、未成立なら listener の停止完了と最終状態を確認してから offerer として再試行する。sender ごとの key は offer の相互上書きを防ぐが、確認時点で相手の offer がまだ無い同時ウィンドウまでは解消しない。

### ファイル転送

1. 送信側が `FileMeta` を送り、受信側の `FileApprove` を待つ。未承認の状態では保存先のフォルダもファイルも作らない。
2. 承認後に保存先を安全に確定し、`CreateNew` で既存ファイルの上書きを防ぐ。承認待ちは peer ごと 32 件、全体 128 件に制限する。
3. 送信側が TransferId 付き 64 KiB チャンクを送り、受信側は chunk index のオフセットへ書く。relay 経路では累積 FlowAck で送信先行量を制限する。
4. 受信完了時に SHA-256 を照合する。対応相手との送信は peer・TransferId・試行 token が一致する検証結果 ACK を待ち、成功時だけ完了にする。結果不明のタイムアウトは失敗表示にし、自動再送で保存済みファイルを重複作成しない。不一致、拒否、キャンセルは相手側へも伝える。接続断後のレジュームは先頭から再送する。

受信承認は承認待ちの `ReceiveState` を維持したまま進め、旧形式チャンクのバッファも引き継ぐ。ファイル作成と中断処理の競合では状態の所有権を確認し、中断済みの転送を再登録せず、作成済みの部分ファイルを回収する。Reject と遅延 FlowAck の返送先は受信状態の peer を使い、状態索引の削除後も別の接続へ流さない。

フォルダ保存先は peer・送信操作の BatchId・ルートフォルダで分離する。同じバッチの後続メタデータが未着でも終了通知まで予約し、終了通知後も承認待ち・受信中があれば最後の終端まで保持する。終了済み識別子や活動のない孤立バッチは 30 分で回収し、peer ごと 128、全体 512 の上限では既存転送を追い出さず新規バッチを拒否する。旧版の BatchId がない転送は従来の peer・ルート単位の寿命を使う。結果 ACK も承認応答で試行 token を echo した相手とだけ交渉し、旧版のワイヤ形式を維持する。

### プレゼンスとペア同期

クライアントは自分の presence を定期更新し、前面表示中だけ peer の `lastSeen` を ETag 付きで取得する。シグナリングと peer presence の参照は `PAIR_LEDGER_MODE` に従う。`required` は D1 の正式ペアだけを許可し、台帳未登録の要求を DO 起動前に拒否する。現行の `transition` は旧ペアとの互換性のため台帳未登録と D1 binding 未設定を許可するが、D1 の照会エラーや不正なモード値は両モードとも拒否する。remote unpair は inbox push を受けて D1 の不在を再確認後に即時反映し、`PairSyncService` の定期照合と前面復帰時の即時照合を安全網にする。

## データの所有と寿命

| データ | 正本 | 寿命と削除 |
| --- | --- | --- |
| deviceId、設定、ペア鍵 | 各 PC の `settings.json` / `peers.json` / `identity.key` | 一時ファイルからリネームして保存。読み込み不能な JSON は `.corrupt-*` へ分離 |
| deviceId と認証公開鍵の結合 | D1 `device_key_bindings` | 不可変。既存 KV は検証済み snapshot から移行し、移行中の未登録 ID は 503 で保留する |
| ペア関係 | D1 `pairs`、各 PC にローカル副本 | 明示 unpair まで永続。D1 の 404 で remote unpair を検出 |
| ペア通知 | D1 `pairing_outbox` / `pairing_generations`、DeviceDO | 元の作成時刻から 1 時間。宛先別に再送し、解除・再ペアの世代を保存・再生時にも照合する |
| ペアリング session / nonce | D1 | 1 時間で失効。読み時検証と日次 scheduled cleanup で除去 |
| signaling / probe | PairDO | 成功時の即時削除または 1 時間 alarm |
| presence / inbox | DeviceDO | presence は時刻の老化で offline 判定。未読キューは上限と TTL を持つ |
| relay quota | RelayQuotaDO | 入室前予約を settle または期限切れで確定。異常終了は予約全量を使用済みにする |
| 転送ファイル | 受信 PC | Cloudflare に保管しない。失敗した部分ファイルはクライアントが削除 |
| 配布物 | R2 `ferry-updates` | manifest 参照中と直近 2 version を保持 |

設定の空・途中切断・`null` JSON は破損として扱う。DeviceId は破損ファイルからの回収を先に試し、回収できない場合や退避に失敗した場合も設定と同じディレクトリの `device-id` の正常な副本から復元を試み、既存のペア関係を保つ。

認証鍵の移行は、物理停止・drain を証明した移行と、承認済み snapshot をカットオフ正本とする論理フェンス移行を区別する。
後者は KV writer のない D1 認証コードと新 `SESSION_HMAC_SECRET` を同じ Worker version に upload し、
その version の配信 100% と旧 HMAC 拒否・新 HMAC 受理を確認した後、snapshot の全件比較と明示 ready を行う。
旧 isolate の物理 drain や KV 収束、既存 WebSocket・認可済み in-flight・発行済み nonce・optional relay の失効は主張しない。遅延した旧 writer の KV 更新は新 D1 に取り込まず、
遅延発行された旧 HMAC token も新 version で拒否する。旧 KV は保持し、旧 writer / 旧 HMAC へ rollback しない。
operator の取得元・承認・実 version / deployment・probe 証跡を保存する。実行手順は [relay README](infra/cloudflare/relay/README.md#論理フェンスでの切替) を参照する。

## 重要な不変条件

- 接続順序は TCP → UDP → relay とし、LAN 直結では STUN も relay も使わない。
- TCP / UDP の接続先は `PeerEndpointPolicy` を通し、loopback、multicast、既知の metadata endpoint へ接続しない。
- 同じ peer の接続確立中に送信操作が来ても、既存接続を cancel せず相乗りする。
- PairDO の offer / answer / endpoint は sender ごとの key に書き、同時 offer の相互上書きを起こさない。
- 現行クライアントは fresh な cfToken の取得に失敗したら relay に入らない。Worker の現行値は `RELAY_AUTH_MODE=optional` / `PAIR_LEDGER_MODE=transition` で、旧版は小さい legacy quota のみ使える。invalid bearer は legacy へ降格しない。
- RelayDO は RelayQuotaDO の lease を入室前に得る。breaker、quota、binding の設定不備で fail closed する。
- PairSecret を持つペアは HMAC 相互認証 + AES-GCM 封筒を常時使う。公開鍵交換前の旧ペアだけが平文互換経路に残る。
- 認証鍵は D1 で最初に確定した値から変更しない。KV の結果を自動で正本へ取り込まず、移行の不確実さを鍵不一致の 401 に変換しない。切替条件と旧 writer への rollback 禁止は運用手順に従う。
- `FileMeta` の受信だけでディスクを変更せず、承認後も保存先外へのパスと既存ファイルの上書きを拒否する。
- Native AOT でリフレクション依存の JSON シリアライズを行わず、モデル追加時は対応する `JsonSerializerContext` を更新する。
- R2 配信では package を先に upload し、それを参照する `releases.*.json` を最後に upload する。
- `nephilim.jp` の旧更新・relay ホストは 2027-05-31 まで維持し、更新ファイルを apex redirect の対象にしない。

## 採用済み設計判断

| 判断 | 理由 | トレードオフ |
| --- | --- | --- |
| STUN を TCP 失敗後まで遅延する | LAN / IPv6 TCP 直結の速度とサーバー非依存性を優先する | TCP 失敗後に STUN 分の待ち時間が追加される |
| IP 候補を順次試行する | loser 側の接続を offer 側が accept する不整合を避ける | Happy Eyeballs 型の最短到達時間より遅い場合がある |
| Worker + DO + D1 にバックエンドを集約する | 自前 VPS / coturn / Firebase の運用境界と二重管理を撤去する | Cloudflare 障害時は新規ペアリング、シグナリング、relay が使えない |
| RelayQuotaDO を global coordinator にする | 入室ごとの判定を強整合に予約し、同時超過と運営費の青天井を防ぐ | 予約経路が 1 つの協調点に集約するため、障害時は relay を fail closed する |
| relay 認証を optional / transition で運用する | Bearer を送らない出荷済み旧版のリレーを即時切断しない | legacy 経路を残すかわりに、専用の小さい quota と IP rate limit を必要とする |
| TURN、R2 ペイロード保管、BYO relay を持たない | 現行の P2P-first + 有界 relay でコンポーネント数と運用負荷を抑える | symmetric NAT 等では Cloudflare relay が唯一の最終経路になる |
| 転送ペイロードをサーバーへ永続化しない | プライバシー、保管コスト、データライフサイクルを PC 間に限定する | 中断転送はサーバーから部分再開できず、先頭から再送する |
| Native AOT を全配布対象で使う | 起動、単体配布、ランタイム依存の一貫性を得る | 反射依存 API と JSON 型情報に制約がある |
| Windows 署名を CI の別ジョブ（`release-local.ps1` + Sigilune）、macOS / Linux 配布を CI の既存ジョブで行う | Certum クラウド署名（TOTP の 30 秒窓）を 1 ジョブに閉じ、Apple 公証・クロスプラットフォーム build の条件と分離する | Windows ジョブは macOS / Linux の R2 配信後に承認待ちなしで続けて走り、失敗時の再送はローカルの `-UploadOnly` か再実行で行う |

## 配布と運用の境界

- `infra/cloudflare/relay/**` の `main` push は `deploy-relay.yml` が型チェックと vitest 後に Worker を配信する。D1 `schema.sql` の変更は Worker deploy と別に適用する。
- `release/**` push は macOS / Linux を build・署名・公証し、R2 へ配信する。Windows x64 / ARM64 は同じ run の `windows-release` ジョブが `scripts/release-local.ps1` を実行し、Sigilune（Certum SimplySign のクラウド鍵）で署名して配信する。
- CI は `build/scripts/verify-release.py` で最終 package と Velopack feed を検証する。版・RID・ネイティブ形式・publish 出力との対応に加え、macOS の署名・公証と manifest のハッシュ・サイズを照合し、成果物の SHA-256 と source / run / version・依存解決結果を結び付けた証拠を生成する。R2 への最初の書き込み前に証拠の集合と配布実物を再照合し、別 run や未検証の成果物の混入を拒否する。
- Windows は通常公開と `-UploadOnly` の両方で、版・PE の RID・署名・manifest・publish 出力と package / Portable の内容を検証する。`-UploadOnly` でも同版の publish 出力を照合に使い、検証後の SHA-256 を各 upload 直前に再確認する。ビルド/署名と公開を分離して、検証した成果物をそのまま配信できる。
- CI と Windows の公開処理は、R2 の一覧と既存オブジェクトの実物を使い、同じ version の配布物の異内容上書きを拒否する。CI は version 付き package 全形式、Windows は nupkg を対象とする。CDN のキャッシュ済み 404 を未公開の根拠にしない。
- R2 の固定 URL は更新時だけ exact URL purge の対象にし、version 付き package は purge しない。
- Bridge ページは relay Worker の Static Assets（`infra/cloudflare/relay/public/`）であり、製品ページとは別系統である。
- 製品ページの配信 HTML は `../vps-web/lp/ferry/`（編集元は `../vps-web/tools/lp/templates/`）、公開実体は VPS の `/srv/www/lp/ferry/`。Cloudflare の中継設定は `../vps-web/deploy/lp-gateways/ferry/` に置き、公開 URL と既存の R2・ライセンス通信を維持する。配信経路は `../vps-web/deploy/deploy-lp.ps1` に統一する。
