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

Ferry は QR コードでペアリングし、WebRTC DataChannel で PC 間ファイルを P2P 転送するデスクトップアプリ。

- **`src/Ferry/`** — .NET 10 Avalonia UI デスクトップアプリ（Native AOT、win-x64）
- **`src/Ferry.Bridge/`** — Firebase Hosting にデプロイする Web ページ（スマホでQRスキャン→2台のPCをペアリング）

### MVVM + サービス層

手動 DI（`App.axaml.cs` で組み立て）。DI コンテナは未使用。

```
ViewModels/          → CommunityToolkit.Mvvm の ObservableObject / ObservableProperty
Services/            → インターフェース (I*Service) + 実装 + Stub 実装
Infrastructure/      → FirebaseSignaling, WebRtcTransport, FileChunker, QrCodeGenerator
```

主要サービスインターフェース:
- `IConnectionService` — ペアリング（QR）とオンデマンド接続（WebRTC）を分離管理
- `ITransferService` — ファイルチャンク転送とレジューム
- `IPeerRegistryService` — ペア情報の永続化（`%APPDATA%\Ferry\peers.json`）
- `ISettingsService` — アプリ設定（現在 Stub 実装のみ）

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
signaling/{pairId}/offer                = SDP 文字列
signaling/{pairId}/answer               = SDP 文字列
signaling/{pairId}/candidatesA/{key}    = ICE candidate
signaling/{pairId}/candidatesB/{key}    = ICE candidate
signaling/{pairId}/createdAt            = タイムスタンプ
```

全ノードに `CreatedAt` を入れており、GitHub Actions（毎時）で1時間超の古いデータを自動削除。

### Native AOT 制約

- JSON シリアライズは Source Generator 必須（`FileMetaJsonContext`, `PeerRegistryJsonContext`）
- リフレクションベースのシリアライズは使用不可

### 転送プロトコル

DataChannel 上のバイナリプロトコル（`TransferProtocol.cs` + `FileChunker.cs`）。チャンクサイズ 16KB。転送中断時はチャンクレベルでレジューム可能。

## 言語

コード内コメント、コミットメッセージ、ユーザーへの応答はすべて **日本語** で行うこと。
