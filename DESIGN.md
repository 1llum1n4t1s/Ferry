# Ferry 設計

この文書は、現在の実装におけるシステム全体の責務、境界、データフロー、不変条件、設計判断をまとめる。接続タイムアウトやプロトコルのフィールド等の実装詳細は [`references/architecture.md`](references/architecture.md)、開発・検証・リリース手順は [`AGENTS.md`](AGENTS.md) と [`CONTRIBUTING.md`](CONTRIBUTING.md) を正本とする。

## 目的と範囲

Ferry は、2 台の PC を QR コードでペアリングし、ファイルまたはフォルダを直接転送するデスクトップアプリである。チャット、サーバー上のファイル保管、クラウド同期は対象に含めない。

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
| D1 `ferry_ledger` | `sessions`、`pairing_nonces`、`pairs` | `pairs` はリモートペア台帳の SSoT。他 2 テーブルは 1 時間の一時データ |
| R2 `ferry-updates` | 署名済み配布物と Velopack manifest の公開 | アプリ更新用。relay ペイロード保管には使わない |

UI は AXAML + MVVM で構成し、`App.axaml.cs` がサービスを手動で組み立てる。DI コンテナは使用しない。Windows x64 / ARM64、macOS Apple Silicon、Linux x64 / ARM64 を Native AOT で配布する。

## 主要データフロー

### ペアリング

1. 各 PC が長期 ECDH 公開鍵を含む一時セッションを D1 へ登録し、QR コードを表示する。
2. スマートフォンの Bridge ページが 2 台分の nonce を Worker へ送る。
3. Worker が nonce を不可分に claim し、D1 `pairs` を作成して両 device の inbox へ結果を push する。
4. 各 PC は公開鍵から PairSecret を導出し、ペア情報を `peers.json` へアトミックに保存する。

### 着信検知と接続確立

1. 各アプリは共有の inbox WebSocket を 1 本保持し、offer 書き込み時の接続ノックで該当 listener を起こす。低頻度 HTTP poll は WebSocket 障害時の安全網である。
2. offer 側は IPv6 dual-stack TCP listener を先に開き、STUN 情報のない offer-v1 を送る。answer 側は広告された IPv4 / IPv6 を順番に試す。
3. TCP 失敗後だけ両側が STUN を実行し、offer-v2 と answer 側 endpoint を交換して UDP ホールパンチを行う。
4. UDP も失敗した場合だけ、両 peer が同じ RelayDO room へ入室する。現行クライアントは fresh な cfToken が無ければ relay に接続しない。

### ファイル転送

1. 送信側が `FileMeta` を送り、受信側の `FileApprove` を待つ。未承認の状態では保存先のフォルダもファイルも作らない。
2. 承認後に保存先を安全に確定し、`CreateNew` で既存ファイルの上書きを防ぐ。承認待ちは peer ごと 32 件、全体 128 件に制限する。
3. 送信側が TransferId 付き 64 KiB チャンクを送り、受信側は chunk index のオフセットへ書く。relay 経路では累積 FlowAck で送信先行量を制限する。
4. 受信完了時に SHA-256 を照合し、不一致、拒否、キャンセルは `FileReject` で相手側へも伝える。接続断後のレジュームは先頭から再送する。

### プレゼンスとペア同期

クライアントは自分の presence を定期更新し、前面表示中だけ peer の `lastSeen` を ETag 付きで取得する。peer presence の参照は D1 の正式ペアに限定する。`PairSyncService` は D1 `pairs` とローカル台帳を定期照合し、remote unpair を反映する。

## データの所有と寿命

| データ | 正本 | 寿命と削除 |
| --- | --- | --- |
| deviceId、設定、ペア鍵 | 各 PC の `settings.json` / `peers.json` / `identity.key` | 一時ファイルからリネームして保存。読み込み不能な JSON は `.corrupt-*` へ分離 |
| ペア関係 | D1 `pairs`、各 PC にローカル副本 | 明示 unpair まで永続。D1 の 404 で remote unpair を検出 |
| ペアリング session / nonce | D1 | 1 時間で失効。読み時検証と日次 scheduled cleanup で除去 |
| signaling / probe | PairDO | 成功時の即時削除または 1 時間 alarm |
| presence / inbox | DeviceDO | presence は時刻の老化で offline 判定。未読キューは上限と TTL を持つ |
| relay quota | RelayQuotaDO | 入室前予約を settle または期限切れで確定。異常終了は予約全量を使用済みにする |
| 転送ファイル | 受信 PC | Cloudflare に保管しない。失敗した部分ファイルはクライアントが削除 |
| 配布物 | R2 `ferry-updates` | manifest 参照中と直近 2 version を保持 |

## 重要な不変条件

- 接続順序は TCP → UDP → relay とし、LAN 直結では STUN も relay も使わない。
- TCP / UDP の接続先は `PeerEndpointPolicy` を通し、loopback、multicast、既知の metadata endpoint へ接続しない。
- 同じ peer の接続確立中に送信操作が来ても、既存接続を cancel せず相乗りする。
- PairDO の offer / answer / endpoint は sender ごとの key に書き、同時 offer の相互上書きを起こさない。
- 現行クライアントは fresh な cfToken の取得に失敗したら relay に入らない。Worker の現行値は `RELAY_AUTH_MODE=optional` / `PAIR_LEDGER_MODE=transition` で、旧版は小さい legacy quota のみ使える。invalid bearer は legacy へ降格しない。
- RelayDO は RelayQuotaDO の lease を入室前に得る。breaker、quota、binding の設定不備で fail closed する。
- PairSecret を持つペアは HMAC 相互認証 + AES-GCM 封筒を常時使う。公開鍵交換前の旧ペアだけが平文互換経路に残る。
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
| Windows 署名をローカル、macOS / Linux 配布を CI で行う | SimplySign の対話署名と Apple 公証・クロスプラットフォーム build の条件を分離する | 1 version の完了にローカル release と `release/**` CI の両方が必要 |

## 配布と運用の境界

- `infra/cloudflare/relay/**` の `main` push は `deploy-relay.yml` が型チェックと vitest 後に Worker を配信する。D1 `schema.sql` の変更は Worker deploy と別に適用する。
- `release/**` push は macOS / Linux を build・署名・公証し、R2 へ配信する。Windows x64 / ARM64 は `scripts/release-local.ps1` が SimplySign で署名して配信する。
- R2 の固定 URL は更新時だけ exact URL purge の対象にし、version 付き package は purge しない。
- Bridge ページは relay Worker の Static Assets であり、ダウンロードランディングページ `web/` とは別系統である。
