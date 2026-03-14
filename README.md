# Ferry

QR コードでペアリングし、WebRTC DataChannel で PC 間のファイルを P2P 転送するデスクトップアプリケーション。

## 技術スタック

| レイヤー | 技術 |
|---------|------|
| UI | Avalonia UI 11.3 (Fluent テーマ) |
| アーキテクチャ | MVVM (CommunityToolkit.Mvvm) |
| ランタイム | .NET 10 / Native AOT (win-x64) |
| P2P 通信 | WebRTC DataChannel (SIPSorcery) |
| シグナリング | Firebase Realtime Database (FirebaseDatabase.net) |
| ペアリング | QR コード (QRCoder) → Firebase Hosting Bridge ページ |
| 自動更新 | Velopack (GitHub Releases) |
| ログ | NLog (ローリングファイル) |

## プロジェクト構成

```
Ferry/
├── src/
│   ├── Ferry/                    # デスクトップアプリ (Avalonia)
│   │   ├── Models/               # データモデル
│   │   ├── ViewModels/           # MVVM ViewModel
│   │   ├── Views/                # XAML ビュー
│   │   ├── Services/             # サービスインターフェース & 実装
│   │   ├── Infrastructure/       # WebRTC, Firebase, ファイルチャンカー
│   │   ├── Converters/           # XAML コンバーター
│   │   └── Util/                 # ログユーティリティ
│   └── Ferry.Bridge/             # Firebase Hosting (QR ペアリング用 Web ページ)
├── .github/workflows/            # CI/CD
│   ├── dotnet-build.yml          # PR ビルド
│   ├── velopack-release.yml      # リリースパッケージ作成
│   └── firebase-cleanup.yml      # Firebase ゴミデータ定期削除
└── docs/                         # 設計書
```

## アーキテクチャ

### ペアリングフロー

スマートフォンを「橋渡し」として 2 台の PC をペアリングする:

1. **PC-A** がセッション登録 → QR コード表示（Bridge ページ URL + セッション ID）
2. **スマートフォン** で QR スキャン → Bridge ページが開く
3. Bridge ページ内の **カメラ** で **PC-B** の QR をスキャン
4. Bridge が Firebase `pairings/` に両セッション書き込み → 両 PC に通知
5. ペア情報をローカル保存 (`%APPDATA%\Ferry\peers.json`) → Firebase セッション即削除

### ペアリングと接続の分離

「誰と繋がるか」(ペアリング) と「実際の通信」(接続) を分離:

- **初回ペアリング**: QR スキャン → Firebase で一時ハンドシェイク → ペア情報をローカル保存 → Firebase 切断
- **ファイル送信時**: オンデマンドで Firebase シグナリング → WebRTC 確立 → チャンク送信 → 転送完了後に切断
- **PC 再起動後**: 保存済みペア一覧から選択するだけで再接続可能

### 接続経路の可視化

WebRTC の ICE 候補タイプを追跡し、接続経路をピアごとに UI 表示:

| 経路 | 表示 | 説明 |
|------|------|------|
| Direct | 🟢 LAN 直接 | ホスト候補による直接接続（最速） |
| StunAssisted | 🟡 P2P（STUN） | NAT 越え P2P（サーバー非経由） |
| Relay | 🔴 リレー（TURN） | TURN サーバー経由（サーバーがボトルネック） |

### 転送プロトコル

DataChannel 上のバイナリプロトコル:

| メッセージ | コード | 内容 |
|-----------|--------|------|
| FileMeta | `0x01` | ファイル名・サイズ・SHA-256 (JSON) |
| FileChunk | `0x02` | チャンクインデックス + データ (16KB) |
| FileAck | `0x03` | 受信完了確認 + SHA-256 検証 |
| FileReject | `0x04` | 受信拒否 |
| Ping/Pong | `0x10/0x11` | キープアライブ |
| ResumeRequest | `0x20` | 転送再開リクエスト |
| ResumeResponse | `0x21` | 転送再開応答 |

### 転送レジューム

接続断時に転送を `Suspended` 状態で保持し、再接続後にチャンクレベルで途中から再開。

### オンデマンド接続マネージャー

- 転送完了後 30 秒アイドルで自動切断
- 転送中の切断時は指数バックオフ (1s→2s→4s→8s→16s) で最大 5 回再接続試行
- 再接続成功時に自動レジューム

### Firebase データのクリーンアップ

- **正常時**: WebRTC 接続確立後に `sessions/`, `pairings/`, `signaling/` を即削除
- **異常時**: GitHub Actions で毎時、`CreatedAt` が 1 時間超の古いデータを自動削除

## ビルド

```bash
# デバッグビルド
dotnet build src/Ferry/Ferry.csproj

# リリースビルド (Native AOT)
dotnet publish src/Ferry/Ferry.csproj -c Release

# Bridge ページのデプロイ
cd src/Ferry.Bridge && firebase deploy --only hosting
```

### 前提条件

- .NET 10 SDK
- Windows 10/11 (x64)
- Firebase CLI（Bridge ページデプロイ時のみ）

## 開発状況

未実装:

- [ ] 設定の永続化 (`ISettingsService` のファイル実装)

実装済み:

- [x] MVVM アーキテクチャ (接続・転送・設定パネル)
- [x] Firebase Realtime Database シグナリング (`FirebaseSignaling.cs`)
- [x] WebRTC DataChannel (`WebRtcTransport.cs` / SIPSorcery)
- [x] QR コード生成 & スマホ Bridge ペアリング
- [x] Bridge ページ (html5-qrcode によるカメラスキャン)
- [x] ペアリング情報の永続化 (`PeerRegistryService`)
- [x] ファイルチャンキング & バイナリプロトコル
- [x] 転送レジュームプロトコル
- [x] オンデマンド接続マネージャー
- [x] 接続経路表示（LAN 直接 / STUN P2P / TURN リレー）
- [x] ドラッグ＆ドロップファイル送信 UI
- [x] トレイアイコン & 最小化設定
- [x] Velopack 自動更新
- [x] NLog ローリングファイルログ
- [x] GitHub Actions CI/CD
- [x] Firebase ゴミデータ定期削除 (GitHub Actions)
- [x] Firebase Hosting デプロイ済み

## ライセンス

Private
