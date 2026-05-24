# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ビルド・テストコマンド

```bash
# デバッグビルド
dotnet build src/Ferry/Ferry.csproj

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

> リリースは手動 `dotnet` ではなく、`release/**` ブランチへの push で CI が行う（後述「自動更新と配信」）。

## アーキテクチャ

### 全体構造

Ferry は QR コードでペアリングし、TCP 直接接続（LAN）/ UDP ホールパンチ（NAT 越え P2P）/ WebSocket リレー（最終手段）で PC 間ファイルを P2P 転送するデスクトップアプリ。ファイル転送に特化しており、チャット機能は含まない。

- **`src/Ferry/`** — .NET 10 Avalonia UI デスクトップアプリ（Native AOT、クロスプラットフォーム: win-x64 / win-arm64 / osx-arm64 / linux-x64 / linux-arm64）
- **`src/Ferry.Bridge/`** — Firebase Hosting にデプロイする Web ページ（スマホでQRスキャン→2台のPCをペアリング。`bridge.js` + `index.html`、ライブラリは CDN 直リンク）
- **`tests/Ferry.Tests/`** — xUnit v3 + NSubstitute によるユニットテスト

> WebSocket リレーサーバー（Node.js）は **本リポジトリには無い**。別リポジトリ `1llum1n4t1.net` から VPS にデプロイする（後述「サーバー接続情報」）。

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

イベント駆動で固定タイムアウトに依存しない設計:

1. **Offer 側**: TCP リスナー起動 → offer 送信（STUN 情報なし）→ TCP accept と Answer ポーリングを `WhenAny` で同時待機
2. **Answer 側**: offer 受信 → TCP 接続試行 → 結果を answer に `route` フィールドで通知
   - TCP 成功 → `route = "direct"` → 両側 TCP で接続完了
   - TCP 失敗 → `route = "needRelay"` → Offer 側が即座に次ステップへ
3. **TCP 失敗時**: Offer 側が STUN クエリ実行 → UDP ホールパンチ試行（8秒）
4. **UDP 失敗時**: WebSocket リレーにフォールバック（`wss://1llum1n4t1.net/ferry-relay`）

STUN は **自前 coturn (`1llum1n4t1.net:3478`) を主、公開 STUN (Google / Cloudflare) を従** の 3 サーバーフォールバック。IPv4 明示指定（`AddressFamily.InterNetwork`、VPS は IPv4/IPv6 両対応だが `49.212.230.244` を引く）。TURN/TURNS は VPS に立ってるが、Ferry は現状 WebSocket リレーで代替しているため未使用（接続詳細は `1llum1n4t1.net/docs/server.md`）。

### ペアリングフロー

1. PC-A がセッション登録 → QR コード表示（Bridge ページ URL + セッションID）
2. スマホで QR スキャン → Bridge ページが開く
3. Bridge ページ内カメラで PC-B の QR をスキャン
4. Bridge が Firebase `pairings/` に両セッション書き込み → 両 PC に通知
5. ペア情報をローカル保存 → Firebase セッション削除

### Firebase 構造

```
sessions/{sessionId}                    = { DisplayName, CreatedAt }
pairings/{pairingId}                    = { SidA, SidB, NameA, NameB, CreatedAt }
signaling/{pairId}/offer                = ConnectionInfo JSON (ips, port, externalIp, externalPort, relayUrl, route)
signaling/{pairId}/answer               = ConnectionInfo JSON
signaling/{pairId}/{role}Endpoint       = "ip:port"（UDP ホールパンチ用外部エンドポイント）
signaling/{pairId}/createdAt            = タイムスタンプ
```

全ノードに `CreatedAt` を入れており、GitHub Actions（6時間おき）で1時間超の古いデータを自動削除。

### Native AOT 制約

- JSON シリアライズは Source Generator 必須（`FileMetaJsonContext`, `PeerRegistryJsonContext`, `ConnectionInfoJsonContext`, `AppSettingsJsonContext`）
- リフレクションベースのシリアライズは使用不可
- `ConnectionInfo` にプロパティを追加する場合は `ConnectionInfoJsonContext` の更新が必要

### 自動更新と配信（CI/CD）

Velopack による自動更新の配信元は **Cloudflare R2**（カスタムドメイン `https://ferry.nephilim.jp`、bucket `ferry-updates`）。クライアントは `App.axaml.cs` の `UpdateBaseUrl` 定数 + `Velopack.Sources.SimpleWebSource` で更新を取得する（旧 `GithubSource` から移行済み）。`Check4Update` は起動時 + 24時間ごとに実行。

リリースは `release/**` ブランチへの push で `.github/workflows/release.yml` が発火し、以下を順に呼ぶ（GitHub Releases は使わず R2 単独配信）:

- `build.yml` — 5 ランタイムを Native AOT 発行
- `package.yml` — ユーザー向け配布物（zip / deb / rpm / AppImage）
- `velopack.yml` — Velopack 自動更新パッケージ（`vpk pack --channel <runtime>` → `releases.<channel>.json` + nupkg）
- `r2-upload` job — フィードとインストーラを `wrangler` で R2 にアップロード（要 Secrets: `CLOUDFLARE_API_TOKEN` / `CLOUDFLARE_ACCOUNT_ID`）

バージョンは `Directory.Build.props` の `<Version>` 単一管理（`version` job が抽出）。GitHub Actions はコミット SHA で固定。

### 転送プロトコル

TCP / WebSocket 上の長さプレフィクス付きバイナリプロトコル（`TransferProtocol.cs` + `FileChunker.cs` + `LengthPrefixedStream.cs`）。チャンクサイズ 16KB。

チャンクメッセージは `[0x02 FileChunk][TransferId 16byte][chunkIndex 4byte][data]`（ヘッダ長 `ChunkHeaderSize`=21）。受信側（`TransferService.HandleFileChunk`）は **TransferId で受信状態を引き、`chunkIndex × 16KB` のオフセットへ `Seek` して書き込む**ため、UDP の順不同到着でも正しく再構成できる。受信完了は全 chunkIndex 受信（ビットマップ `ReceivedChunkSet`）で判定し、最後に SHA-256 でファイル整合性を検証する。受信ファイル名・相対パスはパストラバーサル防止のため保存先ディレクトリ配下に収まることを検証する。

UDP ホールパンチ経由の場合は `UdpHolePunchTransport` が信頼性レイヤー（選択的 ACK・フラグメンテーション 1187 bytes・スライディングウィンドウ 128）を提供する。順序保証はトランスポート層ではなく上記の chunkIndex ベース書き込みで担保している。

> **レジュームは「先頭から再送」方式**（`ResumeTransferAsync`、`startChunk=0`）。受信側は承認時にファイルを再作成するため、部分再送ではなく全チャンクを送り直す。
>
> チャンクメッセージ形式は 2026-05 に `chunkIndex` 単独から `TransferId + chunkIndex` に変更済み。**旧形式とは非互換**（既存の配布クライアントは存在しないため移行問題なし）。形式を再度変える場合は送受信（`FileChunker.CreateChunkMessage` ↔ `HandleFileChunk`）と `FileChunkerTests` のオフセットを揃えること。

### プレゼンス監視（オンライン検出）

ConnectionViewModel が定期的に Firebase にハートビート送信・ピアの lastSeen をポーリング。

```
HeartbeatLoop (30秒):
  └ UpdatePresenceAsync(deviceId, displayName)
  └ Firebase の `presence/{deviceId}` に { lastSeen, displayName } を書き込み

PresencePollLoop (10秒):
  └ GetPresenceAsync(peerId)
  └ Firebase の `presence/{peerId}` から lastSeen を取得
  └ now - lastSeen < 60秒 なら IsOnline = true
```

### テスト

xUnit v3 + NSubstitute。テスト内の非同期メソッドには `TestContext.Current.CancellationToken` を渡すこと（xUnit1051 警告回避）。

### ログとデバッグ

**SuperLightLogger**（log4net 互換シム + 内蔵 File Target、Native AOT 安全）でファイル出力。場所: `%LOCALAPPDATA%\Ferry\logs\Ferry_YYYYMMDD.log`。DEBUG は全レベル、Release は Info 以上（接続フォールバックの各段を本番でも追えるようにするため）。IP 等の PII はログ出力時に `Util.Logger.MaskIp` で末尾オクテットを伏せる。`Logger.Initialize` は失敗時に `%TEMP%` へフォールバックする（`Program.cs`）。`Util.Logger` は内部で `SuperLightLogger.ILog` を保持し、`LogManager.Configure(b => b.AddSuperLightFile(...).SetMinimumLevel("Trace"))` でローリング設定（旧 NLog から 2026-05 に移行）。

**通信デバッグのポイント:**
- SDP offer/answer ポーリング: `SDP 待機中` ログで現在の待機状態を確認（`createdAt=null` なら Firebase に offer が無い）
- 接続失敗時は常にログに原因が出力されるよう各所で `Util.Logger.Log(..., Util.LogLevel.Error)` を使用

## サーバー接続情報

WebSocket リレーサーバー・STUN サーバーの接続情報・デプロイ手順は **`C:\Users\szk\Work\1llum1n4t1.net` リポジトリの `docs/server.md`** を参照。

## 既知の制限と注意事項

1. **同時接続の競合**: 2台の PC が同時にファイル送信を試みると、両方が offer 側になり接続失敗する可能性がある。接続確立後にファイル送信すること（role 調停は未実装の設計課題）。
2. **Native AOT 制約**: JSON の動的シリアライズは使用不可。モデル追加時は `*JsonContext` も追加。
3. **信頼モデルは設計途上**: 現状 Firebase シグナリングは匿名アクセス（セキュリティルール要確認）、トランスポートのピア認証なし、転送ペイロードは平文（リレー経由時は中継サーバが内容を読める）。E2E 暗号・ペア相互認証・Firebase ルールは未実装の設計事項。改修方針は `memory-bank` の Ferry プロジェクト `design-proposals.md` を参照。

> 設定（`settings.json` / `peers.json`）は一時ファイル→リネームでアトミックに保存し、読み込み失敗時は `.corrupt-<時刻>` に退避する。`DeviceId` は pairId / presence の基盤なので、破損で再生成されるとペアが消える点に注意。

## 言語

コード内コメント、コミットメッセージ、ユーザーへの応答はすべて **日本語** で行うこと。
