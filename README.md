# Ferry

QR コードまたはワンタイムコードでペアリングし、TCP 直接接続 / UDP ホールパンチ / WebSocket リレーで PC 間のファイルを P2P 転送するデスクトップアプリケーション。

## ダウンロード

**Setup インストーラと AppImage は最新版**を指す固定 URL からダウンロードできます。起動後の更新通知と手動確認については、下の「更新」を参照してください。

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
| x64 (Debian/Ubuntu) | .deb | <https://ferry.kagayoi.com/ferry_1.0.79-1_amd64.deb> |
| ARM64 (Debian/Ubuntu) | .deb | <https://ferry.kagayoi.com/ferry_1.0.79-1_arm64.deb> |
| x86_64 (RHEL/Fedora) | .rpm | <https://ferry.kagayoi.com/ferry-1.0.79-1.x86_64.rpm> |
| aarch64 (RHEL/Fedora) | .rpm | <https://ferry.kagayoi.com/ferry-1.0.79-1.aarch64.rpm> |

> 💡 .deb / .rpm は **バージョン入りファイル名** で配信されます。最新バージョン番号は [`releases.linux-x64.json`](https://ferry.kagayoi.com/releases.linux-x64.json) などの manifest を参照してください。

### 更新

起動時に更新を確認し、新しいバージョンがあればダイアログで通知します。更新はダイアログから適用できます。転送中は自動確認をスキップします。設定画面またはトレイメニューから手動確認もできます。更新フィードと配信方式の詳細は [`references/architecture.md`](references/architecture.md) の「自動更新と配信（CI/CD）」を参照してください。

## 使い方

1. **2 台の PC でそれぞれ Ferry を起動**し、「ペアリング追加」を選択
2. 次のどちらかでペアリング
   - **スマートフォンを使う場合**: PC-A の QR を読み取り、開いた Bridge ページ (`https://watashiba.kagayoi.com`) 内のカメラで PC-B の QR を読み取る
   - **スマートフォンを使わない場合**: 一方の PC に表示されたワンタイムのペアリングコードをコピーし、もう一方の PC の「相手のペアリングコード」欄へ貼り付けて実行する（旧版とのペアリングには QR コードを使用）
3. 両 PC のペアリング完了後、ピア一覧から相手を選んでファイル / フォルダをドラッグ & ドロップで送信（検索・ソート・ピン留めで一覧を整理可能）
4. PC 再起動後も保存済みペア一覧から再接続できます

## 開発者向け

| 知りたいこと | ドキュメント |
|---|---|
| ビルド・テスト・CI・リリース手順 | [`CONTRIBUTING.md`](CONTRIBUTING.md) |
| システムの目的・責務境界・不変条件・設計判断 | [`DESIGN.md`](DESIGN.md) |
| 接続フロー・Cloudflare 構成・転送プロトコルの実装詳細 | [`references/architecture.md`](references/architecture.md) |
| コーディングエージェントの作業規約・必須検証 | [`AGENTS.md`](AGENTS.md) |
| 障害切り分け | [`docs/operations/runbook.md`](docs/operations/runbook.md) |

## ライセンス

Private
