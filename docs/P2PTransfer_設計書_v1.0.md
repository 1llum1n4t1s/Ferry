# P2P ファイル転送アプリ 設計書 v1.0

> **ドキュメントステータス**: Draft  
> **最終更新**: 2026-03-13  
> **対象環境**: Windows 11 25H2 以降

---

## 1. 概要

### 1.1 目的

2台の PC 間でファイルを直接転送する P2P アプリケーション。サーバーを介さずに LAN 内外を問わず動作し、スマートフォンを「橋」として QR コード読み取りだけで接続を確立する。

### 1.2 設計方針

- **サーバーレス**: ファイルデータはすべて P2P（WebRTC DataChannel）で直接転送。サーバーを経由しない
- **コストゼロ**: 使用するすべてのサービス・ライブラリが無料枠内で完結する
- **シンプル UI**: ドラッグ＆ドロップ＋宛先選択のミニマル設計
- **1 対 1 専用**: 初期リリースはペア接続のみ。多対多は将来拡張として検討

### 1.3 ユーザーストーリー

> 自宅の PC からカフェの PC にファイルを送りたい。USB メモリもクラウドストレージも使いたくない。  
> 両方の PC でアプリを起動し、手元のスマホで QR を 2 つ読むだけで接続完了。あとはドラッグ＆ドロップ。

---

## 2. 技術スタック

| カテゴリ | 技術 | 備考 |
|----------|------|------|
| 言語 | C# / .NET 10 | Native AOT 対応（※制約あり。後述） |
| UI フレームワーク | Avalonia UI (MVVM) | Zafiro.Avalonia 併用 |
| P2P 通信 | WebRTC DataChannel | NuGet: SIPSorcery |
| シグナリング | Firebase Realtime Database | 無料枠（同時接続 100, 1GB 転送/月） |
| NAT 越え (STUN) | Google 公開 STUN サーバー | `stun.l.google.com:19302` |
| QR コード生成 | QRCoder | NuGet |
| QR 橋渡し Web | Firebase Hosting（静的 HTML） | スマホブラウザで開く中継ページ |
| アプリ更新 | Velopack | 自動更新機構 |
| CI/CD | GitHub Actions | `release/*` ブランチで自動ビルド＆リリース |

### 2.1 NuGet パッケージ一覧

```
Avalonia
Avalonia.Desktop
Avalonia.Themes.Fluent
Zafiro.Avalonia
CommunityToolkit.Mvvm
SIPSorcery                    # WebRTC (ICE, DTLS, SCTP, DataChannel)
QRCoder                       # QR コード画像生成
FirebaseDatabase.net           # Firebase Realtime Database クライアント
Velopack                      # アプリ自動更新
```

### 2.2 Native AOT 互換性に関する注意

Native AOT ではリフレクションベースのライブラリに制約がある。以下の対応が必要になる可能性がある。

| パッケージ | AOT リスク | 対策 |
|-----------|-----------|------|
| SIPSorcery | 中：内部で一部リフレクション使用 | 動作検証を優先。問題時は通常ビルドにフォールバック |
| FirebaseDatabase.net | 高：Newtonsoft.Json 依存 | System.Text.Json ベースの薄いクライアントを自作する選択肢もあり |
| QRCoder | 低：画像生成のみ | AOT 互換の見込み高い |
| Avalonia | 対応済み | 公式で AOT サポート |

**判断**: 初期開発は通常ビルド（ReadyToRun）で進め、安定後に AOT 化を検討する。

---

## 3. アーキテクチャ

### 3.1 全体構成図

```
┌──────────────┐                                    ┌──────────────┐
│   PC-A       │                                    │   PC-B       │
│  (送信側)     │          WebRTC DataChannel         │  (受信側)     │
│              │◄══════════════════════════════════►│              │
│  Avalonia UI │          P2P 直接通信 (暗号化)       │  Avalonia UI │
└──────┬───────┘                                    └──────┬───────┘
       │ QR 表示                                           │ QR 表示
       │                                                   │
       │         ┌─────────────────────────┐               │
       │         │    スマートフォン         │               │
       └────────►│  ブラウザで QR スキャン    │◄──────────────┘
                 │  (アプリ不要)             │
                 └───────────┬─────────────┘
                             │
                             ▼
                 ┌─────────────────────────┐
                 │  Firebase Realtime DB    │
                 │  (シグナリングのみ)       │
                 │  - SDP Offer/Answer      │
                 │  - ICE Candidates        │
                 └─────────────────────────┘
```

### 3.2 レイヤー構成

```
┌─────────────────────────────────────────────┐
│  Presentation Layer (Avalonia UI / MVVM)     │
│  - MainWindow / QR 表示 / ファイルリスト     │
├─────────────────────────────────────────────┤
│  Application Layer (ViewModel / Service)     │
│  - ConnectionService (シグナリング管理)       │
│  - TransferService (ファイル転送管理)         │
│  - QrCodeService (QR 生成)                   │
├─────────────────────────────────────────────┤
│  Infrastructure Layer                        │
│  - WebRtcTransport (SIPSorcery ラッパー)     │
│  - FirebaseSignaling (シグナリング実装)       │
│  - FileChunker (チャンク分割 / 結合)          │
└─────────────────────────────────────────────┘
```

---

## 4. 接続フロー

### 4.1 事前準備

- Firebase プロジェクトを作成し、Realtime Database を有効化
- Firebase Hosting に QR 橋渡し用の静的 HTML をデプロイ（後述）

### 4.2 接続シーケンス

```
 PC-A (送信側)          スマホ (ブラウザ)         PC-B (受信側)         Firebase
      │                      │                      │                    │
      │  1. 起動・セッション生成                      │                    │
      ├─────────────────────────────────────────────────────────────────►│
      │     session/{sessionId-A} を書き込み          │                    │
      │                                              │                    │
      │  2. QR コード表示                             │  3. 起動・セッション生成
      │     (URL: bridge.html?sid=A&token=xxx)        │                    │
      │                                              ├───────────────────►│
      │                                              │  session/{sid-B}    │
      │                                              │                    │
      │                      │  4. QR-A スキャン       │  QR コード表示      │
      │                      │     bridge.html 開く    │                    │
      │                      │                        │                    │
      │                      │  5. QR-B スキャン       │                    │
      │                      │     sid-A と sid-B を   │                    │
      │                      │     Firebase に書き込み  │                    │
      │                      ├────────────────────────────────────────────►│
      │                      │  pairing/{sid-A + sid-B}                    │
      │                                                                   │
      │  6. ペアリング通知受信 ◄──────────────────────────────────────────│
      │                                              │  ペアリング通知受信 ◄┤
      │                                              │                    │
      │  7. PC-A が SDP Offer 生成・送信               │                    │
      ├──────────────────────────────────────────────────────────────────►│
      │                                              │                    │
      │                                              │  8. SDP Answer 生成 │
      │                                              ├───────────────────►│
      │  ◄──────────────────────────────────────────────────────────────┤
      │                                              │                    │
      │  9. ICE Candidate 交換（双方向）               │                    │
      │◄────────────────────────────────────────────►│                    │
      │                                              │                    │
      │  10. WebRTC DataChannel 確立 (P2P 直結)       │                    │
      │◄════════════════════════════════════════════►│                    │
      │            DTLS 暗号化済み通信                  │                    │
```

### 4.3 QR 橋渡しページ（Firebase Hosting）

スマホのブラウザで動作する軽量な静的 HTML。アプリのインストールは不要。

**動作仕様**:

1. 1 つ目の QR を読む → URL パラメータからセッション ID-A を取得・保持
2. 2 つ目の QR を読む → セッション ID-B を取得
3. 両方揃った時点で Firebase に `pairing` ノードを書き込み
4. 「接続中...」と表示 → 完了後「接続しました」と表示

**QR コードに埋め込む URL の形式**:

```
https://<project>.web.app/bridge.html?sid={sessionId}&token={authToken}
```

---

## 5. ファイル転送仕様

### 5.1 チャンク転送

WebRTC DataChannel にはメッセージサイズ上限がある（実装依存、概ね 16KB〜256KB）。ファイルを固定サイズのチャンクに分割して送信する。

| パラメータ | 値 | 理由 |
|-----------|-----|------|
| チャンクサイズ | 16KB (16,384 bytes) | DataChannel の最小共通サイズ。安全マージン |
| メタデータ | JSON ヘッダー (先頭チャンク) | ファイル名、サイズ、チャンク総数、ハッシュ |
| 完了確認 | 受信側から ACK | チャンク単位ではなく、ファイル単位で ACK |
| ハッシュ検証 | SHA-256 | 転送完了後にファイル全体を検証 |

### 5.2 転送プロトコル（DataChannel メッセージ形式）

```
[メッセージ種別 (1byte)] [ペイロード]

種別:
  0x01 = FILE_META     : JSON { fileName, fileSize, totalChunks, sha256 }
  0x02 = FILE_CHUNK    : [chunkIndex (4byte)] [data]
  0x03 = FILE_ACK      : [status (1byte)] [sha256 (32byte)]
  0x04 = FILE_REJECT   : [reason (UTF-8)]
  0x10 = PING          : keep-alive
  0x11 = PONG          : keep-alive response
```

### 5.3 フロー制御

DataChannel の `bufferedAmount` を監視し、バッファが閾値（64KB）を超えたら送信を一時停止する。`bufferedamountlow` イベントで送信を再開する。

### 5.4 受信ファイルの保存先

- デフォルト: `%USERPROFILE%\Downloads\P2PTransfer\`
- 設定画面で変更可能
- 同名ファイルは `filename (1).ext` 形式でリネーム

---

## 6. セキュリティ

### 6.1 通信の暗号化

- **WebRTC DataChannel**: DTLS 1.2+ による暗号化が標準で有効。追加設定不要
- **シグナリング (Firebase)**: HTTPS 経由のため盗聴リスクは低い

### 6.2 Firebase セキュリティルール

```json
{
  "rules": {
    "sessions": {
      "$sessionId": {
        ".read": "auth != null && data.child('token').val() === auth.token.token",
        ".write": "auth != null && (!data.exists() || data.child('uid').val() === auth.uid)"
      }
    },
    "pairing": {
      "$pairId": {
        ".read": "auth != null",
        ".write": "auth != null && !data.exists()"
      }
    },
    "signaling": {
      "$pairId": {
        ".read": "auth != null",
        ".write": "auth != null"
      }
    }
  }
}
```

### 6.3 セッション管理

| 項目 | 仕様 |
|------|------|
| セッション ID | UUID v4 (暗号的にランダム) |
| 認証トークン | Firebase Anonymous Auth で取得 |
| セッション有効期限 | 10 分（Firebase TTL で自動削除） |
| ペアリング後のシグナリングデータ | DataChannel 確立後に Firebase から削除 |

### 6.4 接続許可

- ペアリングリクエスト受信時、受信側 PC にダイアログ表示
- ユーザーが「許可」を明示的にクリックしない限り接続しない
- 拒否した場合、Firebase のペアリングノードを削除

---

## 7. UI 設計

### 7.1 画面構成

Avalonia UI + Zafiro.Avalonia で構築。MVVM パターン（CommunityToolkit.Mvvm）を使用。

```
┌─────────────────────────────────────────────────────┐
│  P2P Transfer                              ─  □  ✕  │
├─────────────────────────────────────────────────────┤
│                                                     │
│  ┌─── 接続パネル ──────────────────────────────────┐ │
│  │                                                 │ │
│  │   状態: 未接続 / 接続待機中 / 接続済み           │ │
│  │                                                 │ │
│  │   ┌──────────┐    相手の表示名:                  │ │
│  │   │  QR Code │    [PC-B (User)]                 │ │
│  │   │          │                                  │ │
│  │   └──────────┘    [切断]                        │ │
│  │                                                 │ │
│  └─────────────────────────────────────────────────┘ │
│                                                     │
│  ┌─── 転送パネル ──────────────────────────────────┐ │
│  │                                                 │ │
│  │   ┌─────────────────────────────────────────┐   │ │
│  │   │                                         │   │ │
│  │   │     ファイルをここにドラッグ＆ドロップ     │   │ │
│  │   │         または クリックして選択           │   │ │
│  │   │                                         │   │ │
│  │   └─────────────────────────────────────────┘   │ │
│  │                                                 │ │
│  │   転送履歴:                                      │ │
│  │   ✓ report.pdf          12.3 MB   完了           │ │
│  │   ► presentation.pptx   45.1 MB   67%  ████░░   │ │
│  │   ✕ image.png           2.1 MB    エラー         │ │
│  │                                                 │ │
│  └─────────────────────────────────────────────────┘ │
│                                                     │
│  ┌─── 設定 ────────────────────────────────────────┐ │
│  │  表示名: [My PC       ]                         │ │
│  │  保存先: [C:\Users\...\Downloads\P2PTransfer\ ] │ │
│  └─────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────┘
```

### 7.2 状態遷移

```
[起動] → [待機中] → (QR スキャン完了) → [ペアリング要求]
                                            │
                              ┌──────────────┤
                              ▼              ▼
                          [拒否→待機中]   [許可→接続確立]
                                            │
                                            ▼
                                      [ファイル転送可能]
                                            │
                                    ┌───────┴───────┐
                                    ▼               ▼
                              [切断→待機中]    [エラー→再接続試行]
```

---

## 8. プロジェクト構成

```
P2PTransfer/
├── src/
│   ├── P2PTransfer/                          # メインアプリ
│   │   ├── App.axaml
│   │   ├── Program.cs
│   │   ├── ViewModels/
│   │   │   ├── MainWindowViewModel.cs
│   │   │   ├── ConnectionViewModel.cs
│   │   │   └── TransferViewModel.cs
│   │   ├── Views/
│   │   │   ├── MainWindow.axaml
│   │   │   ├── ConnectionPanel.axaml
│   │   │   └── TransferPanel.axaml
│   │   ├── Services/
│   │   │   ├── IConnectionService.cs
│   │   │   ├── ITransferService.cs
│   │   │   ├── IQrCodeService.cs
│   │   │   └── ISettingsService.cs
│   │   ├── Infrastructure/
│   │   │   ├── WebRtcTransport.cs            # SIPSorcery ラッパー
│   │   │   ├── FirebaseSignaling.cs          # シグナリング実装
│   │   │   ├── FileChunker.cs                # チャンク分割/結合
│   │   │   └── QrCodeGenerator.cs            # QRCoder ラッパー
│   │   ├── Models/
│   │   │   ├── PeerInfo.cs
│   │   │   ├── TransferItem.cs
│   │   │   └── AppSettings.cs
│   │   └── P2PTransfer.csproj
│   │
│   └── P2PTransfer.Bridge/                   # QR 橋渡し Web (静的 HTML)
│       ├── index.html                        # QR スキャンページ
│       ├── bridge.js                         # Firebase SDK + ペアリングロジック
│       └── firebase.json                     # Hosting 設定
│
├── .github/
│   └── workflows/
│       └── release.yml                       # release/* で自動ビルド&リリース
│
├── docs/
│   └── design.md                             # 本設計書
│
├── Directory.Build.props
└── P2PTransfer.sln
```

---

## 9. CI/CD (GitHub Actions)

`release/*` ブランチへの push をトリガーとしてビルド・リリースを実行する。

### 9.1 ワークフロー概要

```yaml
trigger: push to release/*

jobs:
  build:
    - dotnet publish (win-x64, self-contained)
    - Velopack pack (インストーラー / アップデートパッケージ生成)
  
  release:
    - GitHub Releases にアップロード
    - Velopack リリースファイルを配置（GitHub Releases or 静的ホスティング）
```

### 9.2 Velopack 更新チャネル

| チャネル | ブランチ | 用途 |
|---------|---------|------|
| stable | `release/stable/*` | 正式版 |
| preview | `release/preview/*` | プレビュー版 |

---

## 10. 技術的リスクと対策

### 10.1 NAT 越えの限界（最重要リスク）

**問題**: STUN サーバーだけでは、両方の PC が Symmetric NAT（キャリアグレード NAT やモバイルルーター等）の場合に P2P 接続が確立できない。

**影響度**: 高。一定割合のユーザーが接続不可になる可能性がある。

**対策案**:

| 対策 | コスト | 効果 |
|------|--------|------|
| TURN サーバーを自前で建てる (coturn) | VPS 月額 ¥500〜 | 確実。Symmetric NAT でも通る |
| 無料 TURN サービスを探す | ¥0 | 不安定。帯域制限あり |
| 初期版は STUN のみで進め、接続失敗時にエラー表示 | ¥0 | MVP としては妥当 |
| Tailscale / ZeroTier 等の VPN 連携を案内 | ¥0 | ユーザーに追加手順が必要 |

**推奨**: 初期リリースは STUN のみで、接続失敗率を計測。必要に応じて TURN を追加。

### 10.2 Firebase 無料枠の上限

| リソース | 無料枠 | 想定消費 (1 接続あたり) |
|---------|--------|----------------------|
| 同時接続 | 100 | 2 (PC-A + PC-B) |
| データ保存 | 1 GB | 数 KB (シグナリングデータ) |
| ダウンロード | 10 GB/月 | 数 KB |

シグナリング用途のみであれば無料枠内で十分。ただし同時接続 100 は、同時に 50 ペアまでの制限を意味する。

### 10.3 大容量ファイル転送

- WebRTC DataChannel は理論上サイズ無制限だが、長時間の接続維持が必要
- 途中切断時のレジューム機能は v1.0 では未実装（将来課題）
- 数 GB 超のファイルではメモリ管理に注意（ストリーム処理必須）

### 10.4 SIPSorcery の成熟度

SIPSorcery は .NET 向け WebRTC 実装としては実績があるが、ブラウザの WebRTC 実装と比べると DataChannel の安定性検証が必要。初期段階でプロトタイプを作り、接続の安定性を評価する。

---

## 11. 将来の拡張候補

| 機能 | 優先度 | 概要 |
|------|--------|------|
| レジューム転送 | 高 | チャンクインデックスを記録し、中断箇所から再開 |
| フォルダ転送 | 高 | ディレクトリ構造をメタデータに含めて再構築 |
| 複数ピア接続 (1:N) | 中 | メッシュ接続またはリレー方式 |
| TURN サーバー統合 | 中 | Symmetric NAT 環境への対応 |
| E2E 暗号化（追加レイヤー） | 低 | DTLS に加えてアプリレイヤーでの暗号化 |
| クロスプラットフォーム (macOS/Linux) | 低 | Avalonia は対応済みだが検証が必要 |

---

## 12. 開発ロードマップ

### Phase 1: 基盤検証 (PoC)

1. Firebase プロジェクト作成 + Realtime Database 有効化
2. QRCoder で QR コード生成テスト
3. SIPSorcery で 2 プロセス間の DataChannel 接続テスト（ローカル）
4. Firebase シグナリング経由での DataChannel 接続テスト（リモート）

### Phase 2: MVP 実装

5. Avalonia UI シェル構築（接続パネル + 転送パネル）
6. QR 橋渡し Web ページ作成 + Firebase Hosting デプロイ
7. ファイルチャンク転送の実装
8. ドラッグ＆ドロップ対応 + プログレス表示

### Phase 3: 安定化・リリース

9. エラーハンドリング（切断検知、再接続、タイムアウト）
10. Velopack 統合 + GitHub Actions CI/CD 構築
11. NAT 越え成功率の評価（STUN のみで十分か判断）
12. v1.0 リリース

---

## 付録 A: QR 橋渡し Web ページ仕様

### 動作フロー

```javascript
// bridge.html がスマホブラウザで開かれた時の処理
// 1回目: sid パラメータを sessionStorage に保存
// 2回目: 保存済み sid と新しい sid で Firebase にペアリングを書き込み

const params = new URLSearchParams(location.search);
const currentSid = params.get('sid');
const savedSid = sessionStorage.getItem('firstSid');

if (!savedSid) {
  // 1台目の QR
  sessionStorage.setItem('firstSid', currentSid);
  showMessage('1台目を読み取りました。2台目の QR を読み取ってください');
} else {
  // 2台目の QR → ペアリング実行
  await firebase.database().ref(`pairing/${savedSid}_${currentSid}`).set({
    sidA: savedSid,
    sidB: currentSid,
    createdAt: firebase.database.ServerValue.TIMESTAMP
  });
  showMessage('接続中...');
}
```

### 必要な Firebase 設定

- Authentication: Anonymous Auth を有効化
- Hosting: 静的 HTML デプロイ
- Realtime Database: セキュリティルール設定（本書 §6.2 参照）

---

## 付録 B: 用語集

| 用語 | 説明 |
|------|------|
| DataChannel | WebRTC の機能で、ブラウザやアプリ間で任意のデータを P2P 送受信するチャネル |
| STUN | NAT 越えのために自分のグローバル IP とポートを知るためのプロトコル |
| TURN | STUN で P2P 接続できない場合にリレーサーバーを経由して通信するプロトコル |
| SDP | セッション記述プロトコル。WebRTC 接続時にメディア/データチャネルの能力を交換する |
| ICE | STUN/TURN を組み合わせて最適な接続経路を見つけるフレームワーク |
| シグナリング | WebRTC 接続確立前に SDP や ICE 候補を交換するための仲介通信 |
| チャンク | ファイルを小さな固定サイズの断片に分割したもの |
