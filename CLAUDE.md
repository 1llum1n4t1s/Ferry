# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ビルド・テストコマンド

```bash
# デバッグビルド
dotnet build src/Ferry/Ferry.csproj

# リリースビルド (Native AOT)
dotnet publish src/Ferry/Ferry.csproj -c Release

# テスト全実行
dotnet test tests/Ferry.Tests/Ferry.Tests.csproj

# テスト単体実行（メソッド名フィルタ）
dotnet test tests/Ferry.Tests/Ferry.Tests.csproj --filter "FullyQualifiedName~EnsureConnectedAsync_未接続時に接続を実行する"

# Bridge ページのデプロイ (Firebase Hosting)
cd src/Ferry.Bridge && firebase deploy --only hosting
```

## アーキテクチャ

### 全体構造

Ferry は QR コードでペアリングし、TCP 直接接続（LAN）/ UDP ホールパンチ（NAT 越え P2P）/ WebSocket リレー（最終手段）で PC 間ファイルを P2P 転送するデスクトップアプリ。ファイル転送に特化しており、チャット機能は含まない。

- **`src/Ferry/`** — .NET 10 Avalonia UI デスクトップアプリ（Native AOT、win-x64）
- **`src/Ferry.Bridge/`** — Firebase Hosting にデプロイする Web ページ（スマホでQRスキャン→2台のPCをペアリング）
- **`src/Ferry.Relay/`** — Node.js WebSocket リレーサーバー（NAT 越え用、VPS にデプロイ）
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

イベント駆動で固定タイムアウトに依存しない設計:

1. **Offer 側**: TCP リスナー起動 → offer 送信（STUN 情報なし）→ TCP accept と Answer ポーリングを `WhenAny` で同時待機
2. **Answer 側**: offer 受信 → TCP 接続試行 → 結果を answer に `route` フィールドで通知
   - TCP 成功 → `route = "direct"` → 両側 TCP で接続完了
   - TCP 失敗 → `route = "needRelay"` → Offer 側が即座に次ステップへ
3. **TCP 失敗時**: Offer 側が STUN クエリ実行 → UDP ホールパンチ試行（8秒）
4. **UDP 失敗時**: WebSocket リレーにフォールバック（`wss://1llum1n4t1.net/ferry-relay`）

STUN は 4 サーバーフォールバック（Google×2、Cloudflare、Nextcloud）。IPv4 明示指定。

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

### 転送プロトコル

TCP / WebSocket 上の長さプレフィクス付きバイナリプロトコル（`TransferProtocol.cs` + `FileChunker.cs` + `LengthPrefixedStream.cs`）。チャンクサイズ 16KB。転送中断時はチャンクレベルでレジューム可能。SHA-256 によるファイル整合性検証。

UDP ホールパンチ経由の場合は `UdpHolePunchTransport` が信頼性レイヤー（選択的 ACK・フラグメンテーション 1187 bytes・スライディングウィンドウ 128）を提供。

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

NLog でファイル出力。場所: `%LOCALAPPDATA%\Ferry\logs\Ferry_YYYYMMDD.log`。DEBUG ビルドは全レベル、Release は Warning 以上。

**通信デバッグのポイント:**
- SDP offer/answer ポーリング: `SDP 待機中` ログで現在の待機状態を確認（`createdAt=null` なら Firebase に offer が無い）
- 接続失敗時は常にログに原因が出力されるよう各所で `Util.Logger.Log(..., Util.LogLevel.Error)` を使用

## サーバー接続情報

WebSocket リレーサーバー・STUN サーバーの接続情報・デプロイ手順は **`C:\Users\szk\Work\1llum1n4t1.net` リポジトリの `docs/server.md`** を参照。

## 既知の制限と注意事項

1. **同時接続の競合**: 2台の PC が同時にファイル送信を試みると、両方が offer 側になり接続失敗する可能性がある。接続確立後にファイル送信すること。
2. **Native AOT 制約**: JSON の動的シリアライズは使用不可。モデル追加時は `*JsonContext` も追加。

## 言語

コード内コメント、コミットメッセージ、ユーザーへの応答はすべて **日本語** で行うこと。
