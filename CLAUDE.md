# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ビルドコマンド

```bash
# デバッグビルド
dotnet build src/Ferry/Ferry.csproj

# リリースビルド (Native AOT)
dotnet publish src/Ferry/Ferry.csproj -c Release

# Bridge ページのデプロイ (Firebase Hosting)
cd src/Ferry.Bridge && firebase deploy --only hosting
```

テストプロジェクトは現時点で存在しない。

## アーキテクチャ

### 全体構造

Ferry は QR コードでペアリングし、TCP 直接接続（LAN）または WebSocket リレー（NAT 越え）で PC 間ファイルを P2P 転送するデスクトップアプリ。

- **`src/Ferry/`** — .NET 10 Avalonia UI デスクトップアプリ（Native AOT、win-x64）
- **`src/Ferry.Bridge/`** — Firebase Hosting にデプロイする Web ページ（スマホでQRスキャン→2台のPCをペアリング）
- **`src/Ferry.Relay/`** — Node.js WebSocket リレーサーバー（NAT 越え用、VPS にデプロイ）

### MVVM + サービス層

手動 DI（`App.axaml.cs` で組み立て）。DI コンテナは未使用。

```
ViewModels/          → CommunityToolkit.Mvvm の ObservableObject / ObservableProperty
Services/            → インターフェース (I*Service) + 実装
Infrastructure/      → FirebaseSignaling, TcpDirectTransport, WebSocketRelayTransport, FileChunker, QrCodeGenerator
```

主要サービスインターフェース:
- `IConnectionService` — ペアリング（QR）とオンデマンド接続（TCP 直接 / WebSocket リレー）を管理
- `ITransferService` — ファイルチャンク転送（SHA-256 検証・レジューム対応）
- `IPeerRegistryService` — ペア情報の永続化（`%APPDATA%\Ferry\peers.json`）
- `ISettingsService` — アプリ設定（`%APPDATA%\Ferry\settings.json`）

### 接続フロー

1. Offer 側が TCP リスナーを起動 → IP:port を Firebase 経由で送信
2. Answer 側が TCP 直接接続を試行（LAN 内、5秒タイムアウト）
3. TCP 失敗時 → WebSocket リレーにフォールバック（`wss://1llum1n4t1.net/ferry-relay`）

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
signaling/{pairId}/offer                = ConnectionInfo JSON (ips, port, relayUrl)
signaling/{pairId}/answer               = ConnectionInfo JSON
signaling/{pairId}/createdAt            = タイムスタンプ
```

全ノードに `CreatedAt` を入れており、GitHub Actions（毎時）で1時間超の古いデータを自動削除。

### Native AOT 制約

- JSON シリアライズは Source Generator 必須（`FileMetaJsonContext`, `PeerRegistryJsonContext`, `ConnectionInfoJsonContext`, `AppSettingsJsonContext`）
- リフレクションベースのシリアライズは使用不可

### 転送プロトコル

TCP ストリーム上の長さプレフィクス付きバイナリプロトコル（`TransferProtocol.cs` + `FileChunker.cs` + `LengthPrefixedStream.cs`）。チャンクサイズ 16KB。転送中断時はチャンクレベルでレジューム可能。SHA-256 によるファイル整合性検証。

## サーバー接続情報

WebSocket リレーサーバー・TURN/STUN サーバーの接続情報・認証方式・デプロイ手順は **`C:\Users\szk\Work\1llum1n4t1.net` リポジトリの `docs/server.md`** を参照。

## 言語

コード内コメント、コミットメッセージ、ユーザーへの応答はすべて **日本語** で行うこと。
