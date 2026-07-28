# Ferry アーキテクチャ概要

開発者向けの概観。**詳細の正本は [`CLAUDE.md`](../CLAUDE.md)** で、非対称な接続手順・失敗事例・実装上の落とし穴まで含めてそちらに集約している。このページは「全体像を掴む」ための入口。

ビルドやリリース手順は [`CONTRIBUTING.md`](../CONTRIBUTING.md)、障害対応は [`operations/runbook.md`](operations/runbook.md) を参照。

## 技術スタック

| レイヤー | 技術 |
|---------|------|
| UI | Avalonia UI 12.1 (Fluent テーマ) |
| アーキテクチャ | MVVM (CommunityToolkit.Mvvm)、手動 DI |
| ランタイム | .NET 10 / Native AOT (win-x64 / win-arm64 / osx-arm64 / linux-x64 / linux-arm64) |
| P2P 通信 | TCP 直接接続 / UDP ホールパンチ (STUN: Cloudflare + Google) / WebSocket リレー |
| シグナリング | Cloudflare Workers + Durable Objects + D1 |
| ペアリング | QR コード (QRCoder) → Cloudflare Workers Static Assets の Bridge ページ |
| 自動更新 | Velopack (Cloudflare R2 `ferry-updates`) |
| リレー | Cloudflare Workers + Durable Objects (Hibernation 対応) |
| ログ | SuperLightLogger (Native AOT 互換のローリングファイル) |
| テスト | xUnit v3 + NSubstitute / relay は vitest |

## プロジェクト構成

```
Ferry/
├── src/Ferry/                 # デスクトップアプリ (Avalonia)
│   ├── Models/                # データモデル
│   ├── ViewModels/            # MVVM ViewModel
│   ├── Views/                 # AXAML ビュー + コードビハインド
│   ├── Services/              # サービスインターフェース & 実装
│   ├── Infrastructure/        # TCP/UDP/WebSocket トランスポート, Cloudflare signaling, STUN, ファイルチャンカー
│   ├── Resources/Locales/     # 18 言語のローカライズ辞書
│   ├── Converters/            # XAML コンバーター
│   └── Util/                  # ログ・パス・パス安全性などのユーティリティ
├── infra/cloudflare/relay/    # Cloudflare Workers + Durable Objects + D1
│                              # (シグナリング / プレゼンス / ペア台帳 / WebSocket リレー / QR Bridge ページ)
├── web/                       # ダウンロード用ランディングページ
├── tests/Ferry.Tests/         # ユニットテスト
├── .github/workflows/         # CI/CD
└── docs/                      # 設計書・運用手順
```

## ペアリングと接続の分離

「誰と繋がるか」（ペアリング）と「実際の通信」（接続）を分離している。

- **初回ペアリング**: QR スキャン → Cloudflare 経由で一時ハンドシェイク → ペア情報をローカル保存
- **ファイル送信時**: オンデマンドでシグナリング → 接続確立 → チャンク送信 → 転送完了後に切断
- **PC 再起動後**: 保存済みペア一覧から選択するだけで再接続できる

## ペアリングフロー

スマートフォンを「橋渡し」として 2 台の PC をペアリングする。

1. **PC-A** がセッション登録 → QR コード表示（Bridge ページ URL + セッション ID + 長期 ECDH 公開鍵）
2. **スマートフォン**で QR スキャン → Bridge ページが開く
3. Bridge ページ内のカメラで **PC-B** の QR をスキャン
4. Bridge が relay Worker の `/pair/create` を叩く → D1 で両セッションを検証し、両 PC の Durable Object inbox へ push
5. ペア情報をローカル保存（`%APPDATA%\Ferry\peers.json`）→ セッションは TTL 経過で自動失効

スマートフォンを使わない「コード貼付ペアリング」もある（`/pair/link`、認可モデルが別）。

## 接続フロー（3 階層フォールバック）

イベント駆動で固定タイムアウトに依存しない設計。

1. **Offer 側**が TCP リスナー起動（IPv6 デュアルスタック）→ offer 送信 → TCP accept と Answer ポーリングを同時待機
2. **Answer 側**が TCP 接続試行 → 結果を answer の `route` フィールドで通知
3. TCP 成功 → 即完了（LAN 内、STUN 通信ゼロ）
4. TCP 失敗 → STUN クエリ → UDP ホールパンチ（NAT 越え P2P、サーバー非経由）
5. UDP 失敗 → WebSocket リレーにフォールバック

> ⚠️ この 3 段は**手順が非対称**で、offer-v1 / offer-v2 の読み直しや外部エンドポイント交換の順序を間違えると cross-NAT で必ずリレーに落ちる。実装に触る前に [`CLAUDE.md`](../CLAUDE.md) の「接続フロー」節を読むこと。

### 接続経路の可視化

| 経路 | 表示 | 説明 |
|------|------|------|
| Direct | 🟢 LAN 直接 | TCP 直接接続（最速） |
| StunAssisted | 🟡 P2P（STUN） | UDP ホールパンチによる NAT 越え P2P |
| Relay | 🔴 リレー | WebSocket リレー経由（最終手段） |

## 転送プロトコル

TCP / WebSocket ストリーム上の長さプレフィクス付きバイナリプロトコル。

| メッセージ | コード | 内容 |
|-----------|--------|------|
| FileMeta | `0x01` | ファイル名・サイズ・TransferId・相対パス (JSON) |
| FileChunk | `0x02` | TransferId + チャンクインデックス + データ (64KB) |
| FileAck | `0x03` | 受信完了確認 + SHA-256 検証結果 |
| FileReject | `0x04` | 受信拒否 (TransferId プレフィクス付き) |
| FileHash | `0x05` | SHA-256 ハッシュ後送り (送信側がチャンク送信後に送付) |
| FileApprove | `0x06` | 受信承認通知 (送信側はこれを待ってチャンク送信を開始) |
| FileFlowAck | `0x07` | フロー制御 ACK (リレー経路の中継バッファ溢れ防止) |
| Ping / Pong | `0x10` / `0x11` | キープアライブ |
| ResumeRequest / ResumeResponse | `0x20` / `0x21` | 転送再開 |

UDP ホールパンチ経由では `UdpHolePunchTransport` が信頼性レイヤー（選択的 ACK・フラグメンテーション・スライディングウィンドウ）を提供する。順序保証はトランスポート層ではなくチャンクインデックスによる書き込みで担保している。

**転送レジューム**: 接続断時に転送を `Suspended` で保持し、再接続後に先頭から再送して復旧する。

## シグナリングデータのクリーンアップ

- **正常時**: 接続確立後にセッション・signaling データを即削除
- **異常時**: D1 のセッション / nonce は 1 時間 TTL で自動失効（古いデータは次回アクセス時に無効化）、PairDO は書込時に仕掛けた alarm で自己削除

## さらに読む

- [`CLAUDE.md`](../CLAUDE.md) — 実装の正本（接続の非対称手順・Native AOT 制約・OS 差の吸収点・既知の落とし穴）
- [`design/cf-only-migration.md`](design/cf-only-migration.md) — Firebase から Cloudflare 単独完結への移行設計
- [`operations/runbook.md`](operations/runbook.md) — 障害切り分け手順
- [`operations/macos-signing.md`](operations/macos-signing.md) — macOS の署名と公証
