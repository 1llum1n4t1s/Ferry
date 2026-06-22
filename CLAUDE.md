# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ビルド・テストコマンド

```bash
# デバッグビルド（単体プロジェクト / ソリューション全体 Ferry.slnx）
dotnet build src/Ferry/Ferry.csproj
dotnet build Ferry.slnx          # アプリ + テストを一括ビルド

# リリース発行 (Native AOT、ランタイム指定が必須)
# CI は win-x64 / win-arm64 / osx-arm64 / linux-x64 / linux-arm64 の 5 ランタイムを発行する
dotnet publish src/Ferry/Ferry.csproj -c Release -r win-x64

# テスト全実行
dotnet test tests/Ferry.Tests/Ferry.Tests.csproj

# テスト単体実行（クラス名 or メソッド名でフィルタ）
dotnet test tests/Ferry.Tests/Ferry.Tests.csproj --filter "FullyQualifiedName~FileChunkerTests"

# Bridge ページのデプロイ (Firebase Hosting)
cd src/Ferry.Bridge && firebase deploy --only hosting
```

> Windows 向けリリースは `pwsh scripts/release-local.ps1` でローカル実行する（コード署名のため）。macOS / Linux と Bridge ページは `release/**` ブランチへの push で CI が配信する（後述「自動更新と配信」）。
>
> PR（→ main）は `.github/workflows/dotnet-build.yml`（".NET Build"）が build + test で検証する。`release/**` トリガーの配信 CI（後述）とは別ワークフローなので、コード変更の正否はこの PR CI で確認する。

## アーキテクチャ

### 全体構造

Ferry は QR コードでペアリングし、TCP 直接接続（LAN）/ UDP ホールパンチ（NAT 越え P2P）/ WebSocket リレー（最終手段）で PC 間ファイルを P2P 転送するデスクトップアプリ。ファイル転送に特化しており、チャット機能は含まない。

- **`src/Ferry/`** — .NET 10 Avalonia UI デスクトップアプリ（Native AOT、クロスプラットフォーム: win-x64 / win-arm64 / osx-arm64 / linux-x64 / linux-arm64）
- **`src/Ferry.Bridge/`** — Firebase Hosting にデプロイする Web ページ（スマホでQRスキャン→2台のPCをペアリング。`bridge.js` + `index.html`、ライブラリは CDN 直リンク）
- **`infra/cloudflare/relay/`** — Cloudflare Workers + Durable Objects の WebSocket リレー実装 (TypeScript)。`wss://relay.ferry.nephilim.jp/ferry-relay` に配信。**`infra/cloudflare/relay/**` を main に push すると `deploy-relay.yml` が `wrangler deploy` で自動配信**（手動 `wrangler deploy` も可）。死活監視は `relay-healthcheck.yml`（15 分ごとの cron）。⚠️ 旧来は手動デプロイのみでリリースに紐づかず、本番が古いまま残り `/auth/token` が 426 を返す事故（2026-06-23）が起きたため CI 化した
- **`web/`** — ダウンロード用ランディングページ（`index.html` + Cloudflare Worker `worker.js` + `wrangler.toml`）。`src/Ferry.Bridge/` の QR ペアリングページとは別物。`web/` 配下を main に push すると `deploy-landing.yml` が Cloudflare に配信
- **`tests/Ferry.Tests/`** — xUnit v3 + NSubstitute によるユニットテスト

### Avalonia UI ネイティブ + MVVM サービス層

UI は Avalonia UI ネイティブ（AXAML）。手動 DI（`App.axaml.cs` で組み立て）。DI コンテナは未使用。

```
Views/                  → Avalonia AXAML + コードビハインド（MainWindow, TransferView, SettingsView）
ViewModels/             → CommunityToolkit.Mvvm の ObservableObject / ObservableProperty
Services/               → インターフェース (I*Service) + 実装 + Stub（テスト・開発用）
Infrastructure/         → FirebaseSignaling, TcpDirectTransport, UdpHolePunchTransport, WebSocketRelayTransport, StunClient, FileChunker
```

主要サービスインターフェース:
- `IConnectionService` — ペアリング（QR）とオンデマンド接続（TCP / UDP / リレー）を管理
- `ITransferService` — ファイルチャンク転送（SHA-256 検証・レジューム対応）
- `IPeerRegistryService` — ペア情報の永続化（`%APPDATA%\Ferry\peers.json`）
- `ISettingsService` — アプリ設定（`%APPDATA%\Ferry\settings.json`）

### 接続フロー（3 階層フォールバック）

イベント駆動で固定タイムアウトに依存しない設計。**STUN は遅延実行**で、LAN で TCP 直結できるケースに STUN レイテンシを乗せない（offer-v1 は STUN 情報なしで即送る）:

1. **Offer 側**: TCP リスナー起動 → **offer-v1 送信（STUN 情報なし）** → TCP accept と Answer ポーリングを `WhenAny` で同時待機
2. **Answer 側**: offer 受信 → TCP 接続試行 → 結果を answer に `route` フィールドで通知
   - TCP 成功 → `route = "direct"` → 両側 TCP で接続完了
   - TCP 失敗 → `route = "needRelay"` → 双方が次ステップ（UDP）へ
3. **TCP 失敗時（UDP ホールパンチ）**: ここが非対称で**順序が肝**。外部エンドポイントの交換は更に非対称で、**Offer 側の endpoint は offer-v2 ペイロード（`ConnectionInfo.ExternalIp/ExternalPort`）で運ばれ、Answer 側の endpoint だけが `signaling/{pairId}/endpoints/{answererDeviceId}` 経由**で渡る（rere #D-003 で per-sender 化。書き手は自分の deviceId キー、読み手はペア相手の deviceId キー）。
   - **Offer 側**: answer(needRelay) 受信 → STUN クエリ → **ExternalIp を載せた offer-v2 を自分の offer ノード（`offers/{_deviceId}`）に上書き再送**（`SendSdpOfferAsync(pairId, _deviceId, …)`）→ Answer の外部エンドポイント（`endpoints/{peerId}`）を最大 10 秒待つ（`WaitForEndpointAsync(pairId, peerId)`）→ 取得後ホールパンチ
   - **Answer 側**: 最初に読んだ offer-v1 には ExternalIp が無い。**`WaitForOfferExternalIpAsync` で offer-v2（ExternalIp 付き）を最大 8 秒ポーリングして読み直してから**（`TryReadOfferOnceAsync(pairId, peerId)`）STUN → 自分の外部エンドポイントを publish（`SendEndpointAsync(pairId, _deviceId, …)`）→ ホールパンチ。MITM 防御（`offer.From == ペア相手` ＋ per-sender キー一致）は再読み分にも適用
   - ⚠️ **Answer 側を「最初に読んだ offer の ExternalIp 有無」でゲートしてはいけない**。offer-v1 は常に ExternalIp が空なので、ゲートすると UDP を一切起動せず自分の endpoint も publish しない → Offer 側が endpoint 待ちでタイムアウト → **cross-NAT（別回線・別 NAT）で必ずリレーへ落ちる構造バグ**になる（実際に過去発生・修正済み）
4. **UDP 失敗時**: WebSocket リレーにフォールバック（`wss://relay.ferry.nephilim.jp/ferry-relay`、Cloudflare Workers + Durable Objects、Hibernation 対応）

> UDP ホールパンチの成功は **NAT タイプ依存**。修正後も両側が CGNAT / symmetric NAT（日本の IPoE 等で多い）だとホールパンチが抜けずリレーに落ちる。「UDP 修正＝必ず P2P」ではない点に注意。

STUN は **Cloudflare 公開 STUN (`stun.cloudflare.com:3478`) を主、Google STUN (`stun.l.google.com:19302`) を従** の 2 サーバーフォールバック。IPv4 明示指定（`AddressFamily.InterNetwork`）。旧 VPS 自前 coturn (`1llum1n4t1.net:3478`) は Cloudflare 移行に伴い 2026-05 に撤去済み。

### ペアリングフロー

1. PC-A がセッション登録 → QR コード表示（Bridge ページ URL + セッションID）
2. スマホで QR スキャン → Bridge ページが開く
3. Bridge ページ内カメラで PC-B の QR をスキャン
4. Bridge が Firebase `pairings/` に両セッション書き込み → 両 PC に通知
5. ペア情報をローカル保存 → Firebase セッション削除

### Firebase 構造

```
sessions/{sessionId}                                = { DisplayName, CreatedAt }
pairings/{pairingId}                                = { SidA, SidB, NameA, NameB, CreatedAt }
presence/{deviceId}                                 = { LastSeen, DisplayName }   # オンライン検出
signaling/{pairId}/offers/{senderDeviceId}          = TimedSignalingValue { Data(ConnectionInfo JSON base64), CreatedAt }  # rere #D-003: 送信元 deviceId で per-sender 分離。Data に ips, port, externalIp, externalPort, relayUrl, route, probe, from, nonce を含む
signaling/{pairId}/answers/{answererDeviceId}       = SignalingValue { Data(ConnectionInfo JSON base64) }  # rere #D-003: answerer の deviceId で per-sender 分離（鮮度なし）
signaling/{pairId}/endpoints/{senderDeviceId}       = SignalingValue { Data("from|ip:port" base64) }  # rere #D-003: 送信元 deviceId で per-sender 分離（UDP ホールパンチ用）
signaling/{pairId}/createdAt                        = タイムスタンプ  # cleanup 用に維持（firebase-cleanup.yml が pairId サブツリーの stale 掃除に使う）。offer 鮮度判定自体は offers/{sender}.CreatedAt へ移行
signaling/{pairId}/probeOffers/{nonce}              = TimedSignalingValue { Data, CreatedAt }  # 経路 Probe v14: per-nonce key
signaling/{pairId}/probeAnswers/{nonce}             = TimedSignalingValue { Data, CreatedAt }  # 同上
```

**Cleanup ポリシーごとのノード分類**:

- **`sessions/{sessionId}` / `pairings/{pairingId}` / `signaling/{pairId}/...`**: 各エントリに `CreatedAt` フィールドを含み、GitHub Actions (`firebase-cleanup.yml`、6 時間おき) で 1 時間超の古いデータを自動削除
- **`presence/{deviceId}`**: `CreatedAt` ではなく `LastSeen` (heartbeat 30 秒で更新) を使う。`FirebaseSignaling.UpdatePresenceAsync` が書き込み、`OfflineThresholdMs=60s` 経過で UI 側で offline 判定。cleanup 対象外 (オンライン検出専用なので時間経過 = 削除でなく、`LastSeen` 老化 = offline 表示)
- **`signaling/<pairId>/probeOffers/<nonce>` / `probeAnswers/<nonce>`**: probe sender の finally で `CleanupProbeAsync(nonce)` により **即時削除**。タイムアウト経過待ちなし

`ConnectionInfo` の `Probe / From / Nonce` フィールド (v12-v14 追加):
- `Probe: bool` — true なら listening 側は経路 Probe 用と判定して通常 transport 確立をスキップ
- `From: string?` — 送信元 deviceId。自己 probe offer の listening 側スキップ用識別子
- `Nonce: string?` — bidirectional 同時 probe race 対策の per-probe 識別子 (v12 追加、v14 で key path として正規化)

### Native AOT 制約

- JSON シリアライズは Source Generator 必須（`FileMetaJsonContext`, `PeerRegistryJsonContext`, `ConnectionInfoJsonContext`, `AppSettingsJsonContext`）
- リフレクションベースのシリアライズは使用不可
- `ConnectionInfo` にプロパティを追加する場合は `ConnectionInfoJsonContext` の更新が必要

### プラットフォーム差の吸収（Win / mac / Linux）

OS 依存処理は実行時分岐（`OperatingSystem.IsWindows()/IsMacOS()/IsLinux()`、AOT でトリミング安全）で 1 箇所に閉じ込める。新たに OS 依存コードを足すときは下記の既存吸収点に倣う。

- **ログイン時自動起動** (`Util.AutoStartManager.Apply`): `SettingsService.SetAutoStart` が委譲。**Win=レジストリ Run キー** / **mac=`~/Library/LaunchAgents/com.1llum1n4t1s.ferry.plist`（`RunAtLoad`、`.app` なら `open` で起動）** / **Linux=`$XDG_CONFIG_HOME/autostart/ferry.desktop`（AppImage 時は `$APPIMAGE` を Exec）** を生成/削除する。設定 UI（`AutoStartWithWindows` トグル）は全 OS で機能し、ラベルは OS 中立文言（「ログイン時に起動」/「Start at login」）。JSON プロパティ名 `AutoStartWithWindows` は既存 `settings.json` 互換のため**改名しない**。`App.axaml.cs` 起動時に有効なら `SetAutoStart(true)` を冪等再適用し、更新で実行パスが変わっても追従する（self-heal）
- **多重起動の前面化** (`SingleInstanceGuard`): 上記のとおり Mutex + Named Pipe で全 OS 対称
- **× ボタン / 最小化トレイ格納** (`MainWindow.OnClosing` / `WindowStateProperty` observable): **macOS は × で終了せず `Hide()`**（赤信号ボタン慣習。終了はメニューバー「終了」/Cmd+Q。これがないと転送中 transport が切れる）。最小化トレイ格納（`ShowInTaskbar=false`+`Hide`）は **Win/Linux 限定**（mac は最小化=Dock 慣習なのでスキップ）
- **ファイラ起動** (`Util.ShellHelper.OpenFolder`): Win=`explorer.exe` / mac=`open` / Linux=`xdg-open`。非 Windows は `ArgumentList` でパスを渡す
- **通知音** (`Util.NotificationSound.Play`): 受信完了時に `TransferService.CompleteReceive`(検証成功・AutoAccept 経路含む)から呼ぶ。Win=`MessageBeep`(user32 P/Invoke。`System.Media.SystemSounds` は Windows 専用アセンブリ依存で cross-plat net10.0 から参照不可) / mac=`afplay Glass.aiff` / Linux=`canberra-gtk-play`→`paplay`。設定 `EnableNotificationSound` が ON かつ送信元ピアが `AppSettings.MutedPeerIds` に無いときのみ鳴らす(best-effort、失敗は無視)。`MutedPeerIds` はこのゲートが唯一の consumer（populate する per-peer ミュート UI は未実装の足場）
- **macOS Local Network 許可**: LAN 直結（TCP/UDP）は macOS のローカルネットワークプライバシ対象。`build/resources/app/App.plist` に `NSLocalNetworkUsageDescription` を持たせ、初回プロンプトに自前文言を出す（拒否されると直結不可→リレー転落）。mDNS/Bonjour 不使用のため `NSBonjourServices` は不要
- **データ配置パス** (`Util.AppPaths`): ログ出力先を OS 別に解決（Win=`%LOCALAPPDATA%\Ferry\logs` / mac=`~/Library/Logs/Ferry` / Linux=`~/.local/share/Ferry/logs`）。`LocalApplicationData` の mac 非慣習・空文字化リスクを明示パスで回避。settings/peers は移行リスクで `ApplicationData` 据置（詳細は §ログとデバッグ）
- **ファイアウォール** (`FirewallHelper`): Windows のみ netsh で受信許可。mac は署名済みアプリの初回 listen 時に OS が許可ダイアログ、Linux は ufw/firewalld 手動許可（いずれも未許可なら直結失敗→リレー）

### 自動更新と配信（CI/CD）

Velopack による自動更新の配信元は **Cloudflare R2**（カスタムドメイン `https://ferry.nephilim.jp`、bucket `ferry-updates`）。クライアントは `App.axaml.cs` の `UpdateBaseUrl` 定数 + `Velopack.Sources.SimpleWebSource` で更新を取得する（旧 `GithubSource` から移行済み）。`Check4Update` は起動時 + 24時間ごとに実行。

**Windows リリース (ローカル実行)**: `pwsh scripts/release-local.ps1` — Lhamiel で確立したローカル署名付きリリースフローの横展開。コード署名 (Authenticode、Certum **Open Source Code Signing in the cloud**、CN=`Open Source Developer Yuichiro Shinozaki`) は SimplySign Desktop のトークンログイン中セッション + スマホ OTP が必要で GitHub Actions からは署名できないため、win-x64 / win-arm64 の 2 チャンネルはローカルスクリプトでリリースする。スクリプトは publish (Native AOT) → `vpk pack` + **Authenticode 署名** (`--signParams`、タイムスタンプ `http://time.certum.pl`) → 署名検証 → `wrangler` (pnpm dlx) で R2 バケット `ferry-updates` にアップロード (manifest は最後) → 配信確認 (`releases.{channel}.json` HTTP 200) → **manifest 外の旧 `*.nupkg` を Cloudflare API V4 で自動削除** (Aggressive 保持戦略。今回ビルドしないチャンネルの manifest は R2 から取得して keep set に加えるため、macOS / Linux の nupkg は誤削除しない) まで一括実行。Cloudflare トークンは `C:\Users\IMT\dev\Secret\secrets.json` の `cloudflare.api_token` を実行時に読む。動作確認は `-SkipUpload` (ビルド + 署名のみ)、RID 絞り込みは `-Runtimes win-x64`。**実行前提: SimplySign Desktop がトークンログイン済み** (証明書が CurrentUser\My に見えること。スクリプトがプリフライトで検査して落とす)。**`/vava` は `vava.config.json` の `localRelease` キーを読んでこのスクリプトを自動実行する**。

**macOS / Linux + Bridge (CI)**: `release/**` ブランチへの push で `.github/workflows/release.yml` が発火し、以下を順に呼ぶ（GitHub Releases は使わず R2 単独配信）:

- `build.yml` — 5 ランタイムを Native AOT 発行（win-* は `package.yml` の portable zip 用に残置）
- `package.yml` — ユーザー向け配布物（zip / deb / rpm / AppImage）
- `velopack.yml` — Velopack 自動更新パッケージ（`vpk pack --channel <runtime>` → `releases.<channel>.json` + nupkg）。**win-x64 / win-arm64 は matrix から除外済み** — 未署名 win フィードがローカル署名リリースの成果物を R2 上で上書きしないため。**osx-arm64 は Developer ID 署名 + notarytool 公証**（一時キーチェーンに証明書 .p12×2 をインポート → `notarytool store-credentials`（**app-specific password 方式**）→ `vpk pack` に `--signAppIdentity` / `--signInstallIdentity` / `--notaryProfile` を渡して .app codesign → .pkg productsign → 公証 → stapler を自動実行。要 Apple Secrets 8 個、手順は [`docs/operations/macos-signing.md`](docs/operations/macos-signing.md)。⚠️ 公証は **app-specific password 方式必須** — App Store Connect API キー方式は Team Key + Developer 権限でないと `invalidAsn1` で失敗する。`matrix: fail-fast: false` で osx 失敗時に linux を巻き込まない）。linux は署名不要
- `r2-upload` job — フィードとインストーラを `wrangler` で R2 にアップロード（要 Secrets: `CLOUDFLARE_API_TOKEN` / `CLOUDFLARE_ACCOUNT_ID`）。**cleanup は R2 上の `releases.win-*.json` を keep set に取り込む**（CI 成果物に win manifest が無いため、取り込まないと署名済み win nupkg を「manifest 外」と誤判定して削除する。取得失敗時は安全側で cleanup を中止）
- `firebase-deploy` job — Bridge ページ (`src/Ferry.Bridge`) を Firebase Hosting に deploy（`--only hosting`。**RTDB ルールはこの job では触らない**）

> **relay Worker の自動デプロイ（release/** とは独立・main の path 変更でトリガー）**: `deploy-relay.yml` が `infra/cloudflare/relay/**` 変更時に `wrangler deploy` で自動配信する（手動デプロイ忘れによる「コードと本番の乖離」事故＝2026-06-23 の `/auth/token` 426 の再発防止）。**Firebase RTDB ルールは CF 単独完結移行で Firebase ごと撤去予定のため CI 化せず手動運用**（`cd src/Ferry.Bridge && firebase deploy --only database`。厳格ルールは 2026-06-23 にデプロイ済み。ルールファイルは Firebase が受理する `//` コメントを含む＝文字列値コメントキーは構文エラーで deploy 不能なので残す）。

> ℹ️ `package.yml` の win portable zip (`ferry_*.zip`) は引き続き CI で生成される未署名バイナリ（ランディングページ未参照のため影響は限定的）。署名対象に含めたい場合はローカルスクリプトへの移植が必要。

バージョンは `Directory.Build.props` の `<Version>` 単一管理（CI では `version` job が抽出、ローカルスクリプトも同ファイルを読む）。GitHub Actions はコミット SHA で固定。

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

- **宛先別履歴**: `TransferViewModel` は全件を `Transfers` に保持しつつ、選択中ピアに属する項目だけを `VisibleTransfers` に投影（`TransferItem.PeerId` で判定）。`ConnectionViewModel.SelectedPeer` 変更を購読して `RebuildVisibleTransfers`。`TransferView.axaml` の `ItemsControl` は `VisibleTransfers` を bind
- **対称キャンセル** (`CancelTransfer`): 送受信どちらからでも `FileReject` で相手に通知し、送信側は自分の `_sendCts`（`ConcurrentDictionary<Guid, CancellationTokenSource>`）を cancel、受信側は `_receiveStates` を破棄。`HandleFileReject` の `_activeTransfers` 分岐も `_sendCts` cancel を行う
- **一時停止 / 再開** (`PauseSendTransfer` / `ResumeSendTransfer`): 送信のみ対応。`_pausedSends`(`ConcurrentDictionary<Guid, byte>`) に TransferId を入れ、`SendChunksAsync` がチャンク送信ループ手前で `_pausedSends.ContainsKey` の間 `Task.Delay(100)` 待機。待機中は `TransferState.Paused`（色 `#FF9F0A`）を表示
- **自動リトライ**: `SendOneFileAsync` が `MaxSendAttempts=3` でリトライ。2 回目以降は `TransferItem.Note` に「リトライ中…(n/3)」を表示（`OnTransferError` は `_sendCtsByItem` 管理中の送信項目をスキップしてリトライループに委ねる）。`OperationCanceledException` はリトライせず Cancelled 扱い。**`PeerUnreachableException`（相手無応答＝オフライン/未起動/到達不可）もリトライせず即 Error 扱い**: offer に対し answer が `OfferAnswerWaitSeconds`(20s) 以内に来なかった場合 `ConnectionService.ConnectToPeerAsync` がこの専用型を投げる。相手がいないのに再接続を繰り返しても毎回 20s 待ちを空打ちするだけ（旧実装は一過性エラー扱いで 20s×3≒60s 浪費していた）なので、明確なオフラインメッセージを出してユーザーの手動「再送」に委ねる。**転送中の一過性切断（相手は生存・接続確立済み）はこの型を投げず従来どおりリトライ対象**
- **保存先アドレスバー**: 受信保存先を設定画面から `MainWindow` 上部の常時表示バーへ移動（📁 アイコン + readonly TextBox + 📂 で OS のファイラ起動 + 変更ボタン）。`SettingsView` 側の保存先ブロックは撤去
- **多重起動防止** (`SingleInstanceGuard`): 名前付き `Mutex`（`Ferry-SingleInstance-Mutex-v1`）で取得失敗時は **Named Pipe**（`Ferry-Activate-<user>-v1`）で既存インスタンスへ前面化シグナルを送って即終了。`Program.cs` の `VelopackApp...Run()` 直後に `TryAcquire`、`App.axaml.cs` で `StartActivationListener` を起動。`Mutex`・`NamedPipeServer/ClientStream` とも .NET 上で **Win/mac/Linux すべて対応**（Unix は UDS バック）なので、2 個目起動時の既存ウィンドウ前面化は全 OS で対称に動く（旧 `EventWaitHandle`〔Windows 専用〕から移行）
- **接続検出の短縮** (#5): `FirebaseSignaling.WaitForSdpAsync` の offer/answer ポーリング間隔を 1000ms → 400ms に短縮し、相手の送信開始から受信開始までの待ちを削減

#### 追加修正 (v1.0.47 後半)

- **複数ファイルを即時 N 行表示**: `SendFilesAsync` は **先に全ファイル分の `TransferItem` を生成・`AddTransfer`**（State=Pending で即 `VisibleTransfers` に並ぶ）してから、`SendItemAsync(item, peer)` を 1 件ずつ直列に送る。旧実装は `foreach await SendOneFileAsync` で 1 件完了まで次行が出ない症状だった。`SendOneFileAsync` は「item 生成 + AddTransfer + SendItemAsync」の薄いラッパ（ResendAsync 用）に分割
- **相手表示名の伝播**: `PairedPeer.DisplayName` を明示バッキングフィールド + `SetProperty` に変更（plain プロパティのままなので AOT の `PeerRegistryJsonContext` シリアライズは維持）。`PresencePollLoop` の `peer.DisplayName =` 代入が変更通知を出し、**左ペイン（ピアリスト）も右ペインも更新**される。旧 plain auto-property は通知が無く左ペインが古い名前のままだった
- **経路バッジ「状態取得中」固着の解消**: `ConnectionViewModel.ProbePeerRouteAsync` は **probe が `Unknown` を返しても既に有効な Route を持つピアは据え置く**（転送中の probe 競合タイムアウトで `Unknown` 退行しない）。`RefreshPeersAsync` は接続中ピア（`_connectionService.ConnectedPeer?.SessionId == peer.PeerId`）には probe せず `_connectionService.Route` を即反映。offline 時の `Unknown` 化は `!isOnline` 分岐が担当
- **ファイルパスのマーキー**: `Controls/MarqueeTextBlock`（`Decorator` 派生・テンプレート非依存・子 TextBlock をコード保持・`DispatcherTimer` で `TranslateTransform.X` を更新）。収まる時は静止、はみ出す時のみ左へ流す。`TransferView` の Row2 パス表示を差し替え
- **転送レート(bps)**: `TransferViewModel` の 1 秒 `DispatcherTimer`（`OnRateTimerTick`）が `VisibleTransfers` の InProgress 項目について **転送開始からの累積平均**（総転送バイト ÷ 経過秒）で bps を算出し `TransferItem.RateText` を更新（停止/完了/一時停止でクリア）。瞬間差分はチャンクのバースト/フロー制御待ちで乱高下するため累積平均に統一。整形は `Util.Formatting.FormatBitrate`（1000 区切り）。開始基準は素フィールド `RateStartBytes`/`RateStartTick`（一時停止/完了で `RateStartTick=0` にリセットし、停止区間を平均に含めない＝再開後は再開時点からの平均）
- **送受信日時**: `TransferItem.CreatedAt`（生成時 `DateTime.Now`）+ `CreatedAtText`（`yyyy-MM-dd HH:mm:ss`）を全履歴行（ファイル名行の右）に常時表示。重複回避のため `DisplayInfo` の完了時刻連結は撤去
- **受信フォルダを開くボタン**: 保存先バー（`MainWindow` 上部）の 📂 に一本化。OS ファイラ起動は `Util.ShellHelper.OpenFolder`、保存先は `MainWindow.axaml.cs` の `OnOpenSaveDirClick`（`_settingsService.Settings.SaveDirectory` 優先）から取得。`TransferView` ヘッダにも一時的に 📂 を置いていたが「開くボタンが 3 つある」状態を避けるため撤去し保存先バーのみに集約
- **Bridge の URL 貼り付けペアリング撤去**: `src/Ferry.Bridge/`（index.html + bridge.js）からモード B（URL ペースト）を削除。このページはカメラ付き端末（スマホ）でしか到達しないため。モード選択はカメラ 1 枚のみ（自動カメラ起動はしない方針は維持）

### プレゼンス監視（オンライン検出）

ConnectionViewModel が定期的に Firebase にハートビート送信・ピアの lastSeen をポーリング。

```
HeartbeatLoop (30秒):
  └ UpdatePresenceAsync(deviceId, displayName)
  └ Firebase の `presence/{deviceId}` に { LastSeen, DisplayName } を書き込み（PUT＝アップロード、DL 枠は消費しない）

PresencePollLoop (30秒):
  └ ① 前面（表示中かつ非最小化）のときだけ実行。トレイ格納/最小化中は停止（Heartbeat は継続するので相手からは online のまま）
  └ ② 取得対象: 選択中ピアは毎サイクル / 他ピアは FullPollEveryNCycles(4=2分) に1回 / ピア未選択時は一覧鮮度のため毎サイクル全ピア
  └ ④⑤ GetPresenceLastSeenAsync(peerId): presence/{peerId}/LastSeen のみを ETag 条件付き GET（未変更なら 304 で本文ゼロ）
  └ now - LastSeen < 60秒 なら IsOnline = true（false→true で WentOnline → 経路 Probe 発火）
```

> **大規模常時オンライン時の Firebase ダウンロード帯域節約 (①②③④⑤)**: 旧実装は「全ペアを 10秒ごとにフル取得」で、常時オンライン台数 N×ピア数 P が ~400 リンクを超えると Spark 無料枠 (10GB/月 download) が枯れる試算だった。対策として上記 5 施策を実装:
> - **① 可視性ゲート** (`MainWindow` → `ConnectionViewModel.SetPresencePollingActive`): トレイ常駐の大多数が寄与ゼロになる最大の削減。前面復帰時は `RefreshPeersAsync` で全ピア即フル取得（DisplayName 同期・経路再判定込み）。
> - **② 選択ピア優先** + **③ 間隔 10s→30s** (`PollIntervalMs`): 1ウィンドウあたりのリクエスト数を削減。
> - **④ ETag 条件付き GET** (`FirebaseSignaling.GetPresenceLastSeenAsync` + `_presenceCache`): `X-Firebase-ETag`/`If-None-Match` で未変更時は 304（本文ゼロ）。オフライン peer は LastSeen 不変なのでほぼ常時 304＝ほぼ無転送。
> - **⑤ LastSeen のみ取得**: ポーリングでは `presence/{id}/LastSeen.json`（数値単独）のみ取り DisplayName を載せない。表示名同期は手動更新/前面復帰の `GetPresenceAsync`（フル取得）に委譲。
>
> これで足りない規模（数千台超）は presence を Cloudflare (Workers Paid + KV / Durable Object) へ逃がすのが次手。`design-proposals.md` 参照。

### テスト

xUnit v3 + NSubstitute。テスト内の非同期メソッドには `TestContext.Current.CancellationToken` を渡すこと（xUnit1051 警告回避）。

### ログとデバッグ

**SuperLightLogger**（log4net 互換シム + 内蔵 File Target、Native AOT 安全）でファイル出力。出力先は OS 別に `Util.AppPaths.GetLogDirectory` が解決する: **Win=`%LOCALAPPDATA%\Ferry\logs`** / **mac=`~/Library/Logs/Ferry`**（慣習どおり Console.app から見える・常に存在し書込可。`LocalApplicationData` は mac で `~/.local/share` 隠し＝非慣習かつ空文字化のリスクがあるため明示パスに寄せている） / **Linux=`~/.local/share/Ferry/logs`**（XDG）。ファイル名は `Ferry_YYYYMMDD.log`。DEBUG は全レベル、Release は Info 以上（接続フォールバックの各段を本番でも追えるようにするため）。IP 等の PII はログ出力時に `Util.Logger.MaskIp` で末尾オクテットを伏せる。`Logger.Initialize` は失敗時に `%TEMP%`（mac/Linux は `$TMPDIR`）へフォールバックする（`Program.cs`）。なお settings.json / peers.json は DeviceId・ペア情報の移行リスクがあるため従来の `ApplicationData`（mac=`~/.config/Ferry`）配置のまま。`Util.Logger` は内部で `SuperLightLogger.ILog` を保持し、`LogManager.Configure(b => b.AddSuperLightFile(...).SetMinimumLevel("Trace"))` でローリング設定（旧 NLog から 2026-05 に移行）。

**通信デバッグのポイント:**
- SDP offer/answer ポーリング: `SDP 待機中` ログで現在の待機状態を確認（`createdAt=null` なら Firebase に offer が無い）
- 接続失敗時は常にログに原因が出力されるよう各所で `Util.Logger.Log(..., Util.LogLevel.Error)` を使用

## サーバー接続情報

- **WebSocket リレー**: Cloudflare Workers + Durable Objects (`wss://relay.ferry.nephilim.jp/ferry-relay`)。実装・デプロイ手順は [`infra/cloudflare/relay/README.md`](infra/cloudflare/relay/README.md) を参照
- **STUN**: Cloudflare 公開 STUN (`stun.cloudflare.com:3478`) を主、Google STUN (`stun.l.google.com:19302`) を従。自前運用は無し
- **Firebase**: Realtime DB (シグナリング・プレゼンス) と Hosting (Bridge ページ) は Firebase 据え置き (Spark 無料枠内)

旧 VPS (`C:\Users\IMT\dev\1llum1n4t1.net` リポジトリ管理) の `ferry-relay` (Node.js) と `coturn` コンテナは 2026-05 Cloudflare 移行に伴い撤去予定。撤去手順は [`docs/Cloudflare移行_作業依頼書_2026-05.md`](docs/Cloudflare移行_作業依頼書_2026-05.md) を参照。

## 既知の制限と注意事項

1. **同時接続の競合**: rere #D-003 で offer を per-sender ノード（`offers/{senderDeviceId}`）化したため、2台が同時に接続を試みても **offer の相互上書きは構造的に起きない**。さらに deviceId 序列の **deferral（`CompareOrdinal` で大きい側が answerer に委譲）** で「双方が offerer になり相互の answer を待ち続けるデッドロック」を収束させる。ただし deferral 判定の瞬間に相手がまだ offer を書いていない**同時ウィンドウ**は残る（完全収束は今後の課題）。基本は接続確立後にファイル送信するのが安全。
2. **Native AOT 制約**: JSON の動的シリアライズは使用不可。モデル追加時は `*JsonContext` も追加。
3. **信頼モデルは設計途上**: Firebase シグナリングは #D-001a で Custom Token Auth 化済み（PR #10）。E2E 暗号は `ConnectionService.CreateSecureChannel` / `StartSecureHandshake` / `ApplySecureStep` に配線済みで、v1.0.48 で設定トグル（旧 `EnableSecureChannel`）を撤去し**常時 ON 化**した。両端が PairSecret を保有していれば自動的に HMAC 相互認証 + AES-GCM 封筒化、保有していないペア（QR で公開鍵交換していない旧 peers.json）は **平文フォールバック**（互換維持）。改修方針は `memory-bank` の Ferry プロジェクト `design-proposals.md` を参照。
   - ⚠️ **PairSecret 交換と 2 台実機検証は未完**: 「QR ペアリング時の長期 ECDH 公開鍵交換」は実装中（Bridge 経由で `PkA`/`PkB` を `pairings/` に乗せる Phase）。それまで既存の peers.json は `PairSecret=null` のままで平文ルートに落ちる。再ペアリング後に SecureChannel が起動するか、別回線 2 台でログ「暗号セッション確立（HMAC 相互認証成功）」を確認するまで「実機検証完了」とは扱わない。詳細は `docs/design/rere-deferred-implementation-plan.md`。

> 設定（`settings.json` / `peers.json`）は一時ファイル→リネームでアトミックに保存し、読み込み失敗時は `.corrupt-<時刻>` に退避する。`DeviceId` は pairId / presence の基盤なので、破損で再生成されるとペアが消える点に注意。

## 言語

コード内コメント、コミットメッセージ、ユーザーへの応答はすべて **日本語** で行うこと。
