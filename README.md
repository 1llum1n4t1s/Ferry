# Ferry

QR コードでペアリングし、TCP 直接接続 / UDP ホールパンチ / WebSocket リレーで PC 間のファイルを P2P 転送するデスクトップアプリケーション。

## ダウンロード

Cloudflare R2 (`https://ferry.kagayoi.com`) から配信。**Setup インストーラと AppImage は常に最新版** を指す固定 URL で、起動後は Velopack による自動更新が走るので、最初に 1 度落とせば以降は手動更新不要です。

### Windows

| アーキテクチャ | 形式 | ダウンロード |
|---|---|---|
| x64 (Intel/AMD) | インストーラ | <https://ferry.kagayoi.com/Ferry-win-x64-Setup.exe> |
| x64 (Intel/AMD) | Portable zip | <https://ferry.kagayoi.com/Ferry-win-x64-Portable.zip> |
| ARM64 (Surface Pro X 等) | インストーラ | <https://ferry.kagayoi.com/Ferry-win-arm64-Setup.exe> |
| ARM64 | Portable zip | <https://ferry.kagayoi.com/Ferry-win-arm64-Portable.zip> |

### macOS

| アーキテクチャ | 形式 | ダウンロード |
|---|---|---|
| Apple Silicon (M1/M2/M3) | インストーラ pkg | <https://ferry.kagayoi.com/Ferry-osx-arm64-Setup.pkg> |

### Linux

| アーキテクチャ | 形式 | ダウンロード |
|---|---|---|
| x64 | AppImage | <https://ferry.kagayoi.com/Ferry-linux-x64.AppImage> |
| ARM64 | AppImage | <https://ferry.kagayoi.com/Ferry-linux-arm64.AppImage> |
| x64 (Debian/Ubuntu) | .deb | <https://ferry.kagayoi.com/ferry_1.0.76-1_amd64.deb> |
| ARM64 (Debian/Ubuntu) | .deb | <https://ferry.kagayoi.com/ferry_1.0.76-1_arm64.deb> |
| x86_64 (RHEL/Fedora) | .rpm | <https://ferry.kagayoi.com/ferry-1.0.76-1.x86_64.rpm> |
| aarch64 (RHEL/Fedora) | .rpm | <https://ferry.kagayoi.com/ferry-1.0.76-1.aarch64.rpm> |

> 💡 .deb / .rpm は **バージョン入りファイル名** で配信されます。最新バージョン番号は [`releases.linux-x64.json`](https://ferry.kagayoi.com/releases.linux-x64.json) などの manifest を参照してください。

### Velopack 自動更新フィード

| チャンネル | manifest URL |
|---|---|
| win-x64 | <https://ferry.kagayoi.com/releases.win-x64.json> |
| win-arm64 | <https://ferry.kagayoi.com/releases.win-arm64.json> |
| osx-arm64 | <https://ferry.kagayoi.com/releases.osx-arm64.json> |
| linux-x64 | <https://ferry.kagayoi.com/releases.linux-x64.json> |
| linux-arm64 | <https://ferry.kagayoi.com/releases.linux-arm64.json> |

クライアントは起動時と 24 時間ごとに manifest を取得し、新バージョンを検出するとダイアログ通知 → ワンクリックで適用されます。

## 使い方

1. **2 台の PC でそれぞれ Ferry を起動** し、「ペアリング追加」を選択
2. **手元のスマートフォン** で PC-A の QR をスキャン → Bridge ページ (`https://watashiba.kagayoi.com`) が開く
3. Bridge ページ内のカメラで **PC-B の QR** をスキャン → 両 PC にペアリング完了通知
4. 以降、ピア一覧から相手を選んでファイル / フォルダをドラッグ & ドロップで送信（検索・ソート・ピン留めで一覧を整理可能）
5. PC 再起動後も保存済みペア一覧から再接続できます

## 開発者向け

| 知りたいこと | ドキュメント |
|---|---|
| ビルド・テスト・CI・リリース手順 | [`CONTRIBUTING.md`](CONTRIBUTING.md) |
| 技術スタック・構成・接続フロー・転送プロトコル | [`docs/architecture.md`](docs/architecture.md) |
| 実装の正本（非対称な接続手順・Native AOT 制約・既知の落とし穴） | [`CLAUDE.md`](CLAUDE.md) |
| 障害切り分け | [`docs/operations/runbook.md`](docs/operations/runbook.md) |

## ライセンス

Private
