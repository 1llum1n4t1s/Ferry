# Ferry

QR コードでペアリングし、TCP 直接接続 / UDP ホールパンチ / WebSocket リレーで PC 間のファイルを P2P 転送するデスクトップアプリケーション。

## ダウンロード

Cloudflare R2 (`https://ferry.nephilim.jp`) から配信。**Setup インストーラと AppImage は常に最新版** を指す固定 URL で、起動後は Velopack による自動更新が走るので、最初に 1 度落とせば以降は手動更新不要です。

### Windows

| アーキテクチャ | 形式 | ダウンロード |
|---|---|---|
| x64 (Intel/AMD) | インストーラ | <https://ferry.nephilim.jp/Ferry-win-x64-Setup.exe> |
| x64 (Intel/AMD) | Portable zip | <https://ferry.nephilim.jp/Ferry-win-x64-Portable.zip> |
| ARM64 (Surface Pro X 等) | インストーラ | <https://ferry.nephilim.jp/Ferry-win-arm64-Setup.exe> |
| ARM64 | Portable zip | <https://ferry.nephilim.jp/Ferry-win-arm64-Portable.zip> |

### macOS

| アーキテクチャ | 形式 | ダウンロード |
|---|---|---|
| Apple Silicon (M1/M2/M3) | インストーラ pkg | <https://ferry.nephilim.jp/Ferry-osx-arm64-Setup.pkg> |

### Linux

| アーキテクチャ | 形式 | ダウンロード |
|---|---|---|
| x64 | AppImage | <https://ferry.nephilim.jp/Ferry-linux-x64.AppImage> |
| ARM64 | AppImage | <https://ferry.nephilim.jp/Ferry-linux-arm64.AppImage> |
| x64 (Debian/Ubuntu) | .deb | <https://ferry.nephilim.jp/ferry_1.0.44-1_amd64.deb> |
| ARM64 (Debian/Ubuntu) | .deb | <https://ferry.nephilim.jp/ferry_1.0.44-1_arm64.deb> |
| x86_64 (RHEL/Fedora) | .rpm | <https://ferry.nephilim.jp/ferry-1.0.44-1.x86_64.rpm> |
| aarch64 (RHEL/Fedora) | .rpm | <https://ferry.nephilim.jp/ferry-1.0.44-1.aarch64.rpm> |

> 💡 .deb / .rpm は **バージョン入りファイル名** で配信されます。最新バージョン番号は [`releases.linux-x64.json`](https://ferry.nephilim.jp/releases.linux-x64.json) などの manifest を参照してください。

### Velopack 自動更新フィード

| チャンネル | manifest URL |
|---|---|
| win-x64 | <https://ferry.nephilim.jp/releases.win-x64.json> |
| win-arm64 | <https://ferry.nephilim.jp/releases.win-arm64.json> |
| osx-arm64 | <https://ferry.nephilim.jp/releases.osx-arm64.json> |
| linux-x64 | <https://ferry.nephilim.jp/releases.linux-x64.json> |
| linux-arm64 | <https://ferry.nephilim.jp/releases.linux-arm64.json> |

クライアントは起動時と 24 時間ごとに manifest を取得し、新バージョンを検出するとダイアログ通知 → ワンクリックで適用されます。

## 使い方

1. **2 台の PC でそれぞれ Ferry を起動** し、「ペアリング追加」を選択
2. **手元のスマートフォン** で PC-A の QR をスキャン → Bridge ページ (`https://ferry.nephilim.jp/bridge/`) が開く
3. Bridge ページ内のカメラで **PC-B の QR** をスキャン → 両 PC にペアリング完了通知
4. 以降、ピア一覧から相手を選んでファイル / フォルダをドラッグ & ドロップで送信
5. PC 再起動後も保存済みペア一覧から再接続できます

## 技術スタック

| レイヤー | 技術 |
|---------|------|
| UI | Avalonia UI 12.0 (Fluent テーマ) |
| アーキテクチャ | MVVM (CommunityToolkit.Mvvm) |
| ランタイム | .NET 10 / Native AOT (win-x64 / win-arm64 / osx-arm64 / linux-x64 / linux-arm64) |
| P2P 通信 | TCP 直接接続 / UDP ホールパンチ (STUN: Cloudflare + Google) / WebSocket リレー |
| シグナリング | Firebase Realtime Database (FirebaseDatabase.net) |
| ペアリング | QR コード (QRCoder) → Firebase Hosting Bridge ページ |
| 自動更新 | Velopack (Cloudflare R2 ferry-updates) |
| リレー | Cloudflare Workers + Durable Objects (Hibernation 対応) |
| ログ | SuperLightLogger (Native AOT 互換のローリングファイル) |
| テスト | xUnit v3 + NSubstitute |

## プロジェクト構成

```
Ferry/
├── src/
│   ├── Ferry/                    # デスクトップアプリ (Avalonia)
│   │   ├── Models/               # データモデル
│   │   ├── ViewModels/           # MVVM ViewModel
│   │   ├── Views/                # XAML ビュー
│   │   ├── Services/             # サービスインターフェース & 実装
│   │   ├── Infrastructure/       # TCP/UDP/WebSocket トランスポート, Firebase, STUN, ファイルチャンカー
│   │   ├── Converters/           # XAML コンバーター
│   │   └── Util/                 # ログユーティリティ
│   └── Ferry.Bridge/             # Firebase Hosting (QR ペアリング用 Web ページ)
├── infra/
│   └── cloudflare/relay/         # Cloudflare Workers + Durable Objects WebSocket リレー (TypeScript)
├── tests/
│   └── Ferry.Tests/              # ユニットテスト (xUnit v3 + NSubstitute)
├── .github/workflows/            # CI/CD
│   ├── dotnet-build.yml          # PR ビルド
│   ├── release.yml               # リリースパッケージ作成
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
- **ファイル送信時**: オンデマンドで Firebase シグナリング → 接続確立 → チャンク送信 → 転送完了後に切断
- **PC 再起動後**: 保存済みペア一覧から選択するだけで再接続可能

### 接続フロー（3 階層フォールバック）

イベント駆動で固定タイムアウトに依存しない設計:

1. **Offer 側** が TCP リスナー起動 → offer 送信 → TCP accept と Answer ポーリングを同時待機
2. **Answer 側** が TCP 接続試行 → 結果を answer の `route` フィールドで通知
3. TCP 成功 → 即完了（LAN 内、STUN 通信ゼロ）
4. TCP 失敗 → STUN クエリ → UDP ホールパンチ（NAT 越え P2P、サーバー非経由）
5. UDP 失敗 → WebSocket リレーにフォールバック（Cloudflare Workers + Durable Objects 経由）

### 接続経路の可視化

接続経路をピアごとに UI 表示:

| 経路 | 表示 | 説明 |
|------|------|------|
| Direct | 🟢 LAN 直接 | TCP 直接接続（最速） |
| StunAssisted | 🟡 P2P（STUN） | UDP ホールパンチによる NAT 越え P2P |
| Relay | 🔴 リレー | WebSocket リレー経由（最終手段） |

### 転送プロトコル

TCP / WebSocket ストリーム上の長さプレフィクス付きバイナリプロトコル:

| メッセージ | コード | 内容 |
|-----------|--------|------|
| FileMeta | `0x01` | ファイル名・サイズ・TransferId・相対パス (JSON) |
| FileChunk | `0x02` | TransferId + チャンクインデックス + データ (64KB) |
| FileAck | `0x03` | 受信完了確認 + SHA-256 検証結果 |
| FileReject | `0x04` | 受信拒否 (TransferId プレフィクス付き) |
| FileHash | `0x05` | SHA-256 ハッシュ後送り (送信側がチャンク送信後に送付) |
| FileApprove | `0x06` | 受信承認通知 (受信側が承認時に送信、送信側はこれを待ってチャンク送信開始) |
| Ping/Pong | `0x10/0x11` | キープアライブ |
| ResumeRequest | `0x20` | 転送再開リクエスト |
| ResumeResponse | `0x21` | 転送再開応答 |

UDP ホールパンチ経由の場合は `UdpHolePunchTransport` が信頼性レイヤー（選択的 ACK・フラグメンテーション・スライディングウィンドウ）を提供。

### 転送レジューム

接続断時に転送を `Suspended` 状態で保持し、再接続後に先頭から再送して復旧。

### Firebase データのクリーンアップ

- **正常時**: 接続確立後に `sessions/`, `pairings/`, `signaling/` を即削除
- **異常時**: GitHub Actions で毎時、`CreatedAt` が 1 時間超の古いデータを自動削除

## ビルド (開発者向け)

```bash
# デバッグビルド
dotnet build src/Ferry/Ferry.csproj

# リリースビルド (Native AOT、ランタイム指定必須)
dotnet publish src/Ferry/Ferry.csproj -c Release -r win-x64

# テスト
dotnet test tests/Ferry.Tests/Ferry.Tests.csproj
```

リリースは `release/X.Y.Z` ブランチへの push で GitHub Actions が 5 ランタイム (win-x64 / win-arm64 / osx-arm64 / linux-x64 / linux-arm64) を Native AOT 発行 → Velopack パッケージ化 → Cloudflare R2 にアップロードします。`/vava` スキルが版数管理・ブランチ作成・古いブランチ掃除を一括実行します。

Bridge ページの手動デプロイ (Service Account JSON 経由、`FirebaseExtended/action-hosting-deploy@v0` が CI 経路):

```bash
# Service Account を ADC として展開
export GOOGLE_APPLICATION_CREDENTIALS=/path/to/sa.json
cd src/Ferry.Bridge && firebase deploy --only hosting --project ferry-edf09
```

### 前提条件

- .NET 10 SDK
- クロスプラットフォーム: Windows 10/11 (x64 / arm64), macOS (arm64), Linux (x64 / arm64)
- Firebase CLI + Service Account JSON（Bridge ページを手動デプロイする場合のみ）
- Cloudflare wrangler CLI（リレー Worker をデプロイ・更新する場合のみ）

## ライセンス

Private
