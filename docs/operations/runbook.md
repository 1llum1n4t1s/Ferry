# Ferry 運用 Runbook

本番運用時の障害対応 / 復旧 / リリース時のロールバック手順を集約。
rere レビュー #F-008 / #F-015 対応で新設。

> **2026-07 全面改訂 (rere #C-15)**: Firebase は 2026-07 に完全撤去済み（プロジェクト `ferry-edf09`
> はシャットダウン済み）だが、本 runbook は Firebase 時代の記述のまま残っていた。
> `relay-healthcheck.yml` が障害検知時に自動生成する Issue はこの runbook のシナリオ B へ誘導するため、
> 障害対応の初動で**必ず読まれる**経路に組み込まれている。存在しない Firebase Console を確認しに行く
> 事故を防ぐため、現行の Cloudflare 単独構成へ書き直した。

---

## 📋 目次

1. [システム構成の把握](#システム構成の把握)
2. [障害対応フロー (Day-2 Ops)](#障害対応フロー)
3. [リリース後 rollback 手順](#リリース後-rollback-手順)
4. [Secret rotation 手順](#secret-rotation-手順)
5. [障害シナリオ別 runbook](#障害シナリオ別-runbook)
6. [監視・ヘルスチェック](#監視ヘルスチェック)

---

## システム構成の把握

障害の切り分けに必要な最小限の構成。詳細は `CLAUDE.md` を参照。

| コンポーネント | 実体 | 落ちると何が起きるか |
|---|---|---|
| **relay Worker** (`watashiba.kagayoi.com`) | Cloudflare Workers | 下記すべての土台。全機能停止 |
| ├ 認証 `/auth/token` | 自前 HMAC bearer (cfToken, 1h) + ECDSA 署名チャレンジ、KV `DEVICE_KEY_BINDING` | **全 API が 401**。ペアリング・接続・presence が全滅 |
| ├ シグナリング `/sig/*` | **PairDO** (Durable Object) | 接続確立不可（既存転送は継続） |
| ├ presence / inbox | **DeviceDO** (Durable Object) | オンライン表示が消え、接続ノックが届かず着信が遅延 |
| ├ ペア台帳 `/pairs/*`・`/pair/*` | **D1** `ferry_ledger` | 新規ペアリング不可。`/pairs` 404 が続くと remote-unpair 誤検知の恐れ |
| └ リレー `/ferry-relay` | **RelayDO** (Hibernation 対応) | CGNAT/symmetric NAT 環境で転送不可（TCP/UDP 直結は無事） |
| **Bridge ページ** | 同 Worker の Static Assets (`public/`) | QR ペアリング不可（コード貼付ペアリングは可） |
| **配信** (`ferry.kagayoi.com`) | Cloudflare R2 `ferry-updates` | 自動更新・新規ダウンロード不可 |
| **ランディング** | Cloudflare Worker (`web/`) | サイト閲覧不可（アプリ動作には影響なし） |

**接続は 3 段フォールバック**: TCP 直結（LAN / IPv6）→ UDP ホールパンチ（STUN）→ WebSocket リレー。
上位が生きていればリレー障害でも転送できる。

**ログの場所（OS 別）**:

| OS | パス |
|---|---|
| Windows | `%LOCALAPPDATA%\Ferry\logs\Ferry_YYYYMMDD.log` |
| macOS | `~/Library/Logs/Ferry/Ferry_YYYYMMDD.log` |
| Linux | `~/.local/share/Ferry/logs/Ferry_YYYYMMDD.log` |

Release ビルドは Info 以上を出力し 7 日保持。アプリのトレイメニュー →「ログフォルダを開く」でも到達できる。
ログ内の deviceId / pairId / IP / ファイル名はマスク済み（`Util.Logger` の Mask* 群）。

---

## 障害対応フロー

### 初動 5 分で確認すること

1. **影響範囲確認**: 全ユーザー影響か、特定環境（CGNAT・特定 OS・特定バージョン）のみか

2. **`/health` を叩く**（依存の健全性まで検査する）

   ```bash
   curl -s -w "\nHTTP %{http_code}\n" https://watashiba.kagayoi.com/health
   ```

   - `HTTP 200` + `OK` → Worker と依存（D1 / KV / `SESSION_HMAC_SECRET` / `SALT`）はすべて健全
   - `HTTP 503` + `{"ok":false,"failed":["D1"]}` → **`failed` 配列が壊れている依存を名指しする**。シナリオ B へ
   - 応答なし / 5xx → Worker 自体かルーティングの問題。シナリオ B へ

3. **症状から当たりをつける**

   | 症状 | 疑う場所 |
   |---|---|
   | ペアリングできない | D1 (`/pair/*`)、cfToken、Bridge ページ → シナリオ A |
   | 相手がずっとオフライン表示 | DeviceDO (presence)、cfToken 失効 → シナリオ B |
   | ペア済みだが転送が始まらない | 3 段フォールバックのどこかで停止 → シナリオ C |
   | 転送が途中で切れる | リレー経路のフロー制御 / half-open → シナリオ D |
   | 自動更新失敗 | R2 / Velopack manifest → シナリオ E |

4. **サーバー側ログを見る**（拒否理由まで残る）

   ```bash
   cd infra/cloudflare/relay && pnpm dlx wrangler tail
   ```

   `reject` 行に `{status, code, message}` が出る（`BAD_SIGNATURE` / `DEVICE_PUBKEY_MISMATCH` /
   `CLOCK_SKEW` / `EXPIRED_SESSION` / `DEVICE_RATE_LIMIT` など）。
   Durable Object の例外は `PairDO error` / `DeviceDO error` として出る。

   > ⚠️ クライアントログに `SDP ポーリングエラー(CF, answer): ... 429: DEVICE_RATE_LIMIT` が出て
   > いる場合、続く「相手から応答がありません」は**相手のオフラインではなく送信側の枠切れ**。
   > `/sig/*` は `RATELIMIT_SIG`（600/60s）を使う設計で、`RATELIMIT_DEVICE`（30/60s）を流用すると
   > 接続 1 回（≒52 req）で自己閉塞する。`wrangler.toml` の binding を確認する。

5. **クライアントログを回収**（上表のパス。障害発生時刻の前後 60 分）

6. **Status pages**
   - Cloudflare: <https://www.cloudflarestatus.com/>
   - GitHub Actions: <https://www.githubstatus.com/>

---

## リリース後 rollback 手順

### Velopack 更新の rollback

> ⚠️ **重要**: `release.yml` と `scripts/release-local.ps1` の cleanup は、manifest 参照分と
> **直近 2 バージョン**（`KEEP_VERSIONS` / `$KeepVersionCount`）を除く**バージョン文字列付きの全オブジェクト**
> を削除する。旧実装の `.nupkg` 限定ではないので、zip / deb / rpm / AppImage も 2 世代を超えると消える。
> 削除済みオブジェクトは復元できないため、**踏み台バージョンの redeploy** で対処する。

**手順**:

1. `Directory.Build.props` の `<Version>` を **rollback したい旧バージョンより 1 patch 上** に変更
   （例: 1.0.70 で問題発覚 → 1.0.71 として 1.0.69 のコードを再リリース）
2. 該当バージョンのコードを取り出す（`git checkout <旧 commit> -- src/`）
3. `release/<新バージョン>` を push → `release.yml` が macOS / Linux を配信
4. Windows は `pwsh scripts/release-local.ps1` をローカル実行（コード署名のため CI 不可）
5. R2 に新 manifest が配置 → ユーザーは次回 `Check4Update` でこちらを取得

**TODO**: R2 cleanup を「archive/ への移動」に変更すれば、`archive/<old-version>` を manifest に
書き戻すことで即時 rollback が可能になる（rere #F-008 根本修正案）。

### relay Worker の rollback

```bash
cd infra/cloudflare/relay

# 直近 deploy 履歴を確認
pnpm dlx wrangler deployments list

# 1 つ前の deploy へ rollback
pnpm dlx wrangler rollback
```

> ⚠️ `wrangler rollback` は **Worker のコードだけ**を戻す。D1 スキーマは戻らない（下記参照）。

### D1 スキーマの適用と rollback

> ⚠️ **`wrangler deploy` は D1 スキーマを適用しない**。`wrangler.toml` の `[[migrations]]` は
> **Durable Object クラス**の migration であって D1 とは無関係。混同すると「コードは新しいのに
> スキーマが古い」状態を見落とす。

```bash
cd infra/cloudflare/relay

# スキーマ適用（手動。CI からは実行されない）
pnpm dlx wrangler d1 execute ferry_ledger --remote --file=schema.sql

# 現在のスキーマを確認
pnpm dlx wrangler d1 execute ferry_ledger --remote --command "SELECT name, sql FROM sqlite_master WHERE type='table'"

# 行数の確認（ペア台帳が消えていないか）
pnpm dlx wrangler d1 execute ferry_ledger --remote --command "SELECT COUNT(*) FROM pairs"
```

**スキーマ変更を含む PR を main にマージする場合の順序**:

1. **先に** `wrangler d1 execute` でスキーマを適用（後方互換な変更＝列追加のみにする）
2. その後にコードを push（`deploy-relay.yml` が自動配信）

逆順にすると、新コードが旧スキーマを触って `/pair/create`・`/pairs/*` が全ユーザーで失敗する。

**バックアップ**: `pairs` テーブルは永続 SSoT（消えると全ユーザーのペアが失われる）。
Cloudflare D1 の Time Travel（過去 30 日の任意時点へ復元）が使える。

```bash
# 復元可能な時点を確認
pnpm dlx wrangler d1 time-travel info ferry_ledger

# 指定時刻へ復元（破壊的。実行前に必ず影響範囲を確認する）
pnpm dlx wrangler d1 time-travel restore ferry_ledger --timestamp <ISO8601>

# 手動エクスポート（リリース前など、節目で取っておく）
pnpm dlx wrangler d1 export ferry_ledger --remote --output ferry_ledger_$(date +%Y%m%d).sql
```

### ランディングページの rollback

`web/` 配下を main に push すると `deploy-landing.yml` が配信する。git で旧版に戻して push し直す。

---

## Secret rotation 手順

### SESSION_HMAC_SECRET（cfToken の署名鍵）

> ⚠️ **全機能に影響する**。ローテーションすると稼働中クライアントの cfToken が即座に無効になる。
> v1.0.70 以降のクライアントは 401 を受けた時点でトークンを破棄して再取得するので**即時復旧**するが、
> それ以前のバージョンは自クロックの有効期限（最大 1h）まで古いトークンを使い続け、
> refresh ループ（約 50 分周期）が回るまで 401 のままになる。

1. **新シークレット生成**: `openssl rand -hex 32`
2. **Workers Secret 更新**: `cd infra/cloudflare/relay && pnpm dlx wrangler secret put SESSION_HMAC_SECRET`
3. **即座に確認**: `curl -s https://watashiba.kagayoi.com/health` が 200 であること（未設定だと 503 で `failed:["SESSION_HMAC_SECRET"]`）
4. **クライアント影響の確認**: `wrangler tail` で `reject` の `BAD_TOKEN` が一時的に増え、数分で収束することを確認

### SALT（pairId の DO 名ハッシュ用）

1. **新 SALT 生成**: `openssl rand -hex 32`
2. **Workers Secret 更新**: `cd infra/cloudflare/relay && pnpm dlx wrangler secret put SALT`
3. ⚠️ **進行中のリレー転送は切断される**（DO 名が変わり別インスタンスになるため）。閑散時間に実施する
4. クライアント側の変更は不要（pairId のハッシュ化は relay 内部のみ）

### CLOUDFLARE_API_TOKEN

1. **新トークン作成**: <https://dash.cloudflare.com/profile/api-tokens>
   - 権限: `Workers R2 Storage:Edit` + `Workers Scripts:Edit` + `D1:Edit`
   - スコープ: 対象アカウントのみ
2. **GitHub Secrets 更新**: Settings → Secrets and variables → Actions → `CLOUDFLARE_API_TOKEN`
3. **ローカル側も更新**: `C:\Users\IMT\dev\Secret\secrets.json` の `cloudflare.api_token`
   （`scripts/release-local.ps1` が実行時に読む）
4. **動作確認**: `deploy-relay.yml` を `workflow_dispatch` で手動実行して通ることを確認
5. **旧トークン revoke**: Cloudflare ダッシュボードで Delete

### CLOUDFLARE_ACCOUNT_ID

通常 rotation 不要（アカウント変更時のみ）。

### Apple 署名 / 公証関連（macOS リリース用）

`velopack.yml` が使う Apple Secrets（証明書 .p12 ×2、app-specific password 等）。
手順は [`docs/operations/macos-signing.md`](macos-signing.md) を参照。

### 漏洩時の対応

- **GitHub Secrets が漏洩した可能性**: 即時 revoke → 新トークン作成 → GitHub Secrets 更新 の順
- **チャット等に貼ってしまった場合**: 漏洩トークンを生かしたまま新トークンを作らない（revoke → 再発行 → 再保存）
- **`SESSION_HMAC_SECRET` 漏洩**: 任意の deviceId を騙る cfToken を偽造できるため**最優先で rotation**

---

## 障害シナリオ別 runbook

### シナリオ A: 「全ユーザーがペアリングできない」

**想定原因**:

1. D1 (`ferry_ledger`) の障害・スキーマ不整合
2. `/auth/token` が通らない（`SESSION_HMAC_SECRET` 消失、KV バインディング喪失）
3. Bridge ページ（Static Assets）の配信失敗 — QR 経路のみ影響
4. `deploy-relay.yml` の失敗でコードが古いまま

**確認手順**:

```bash
# 1. 依存の健全性（failed 配列が原因を名指しする）
curl -s https://watashiba.kagayoi.com/health

# 2. D1 のテーブルと行数
cd infra/cloudflare/relay
pnpm dlx wrangler d1 execute ferry_ledger --remote --command "SELECT COUNT(*) FROM pairs"
pnpm dlx wrangler d1 execute ferry_ledger --remote --command "SELECT COUNT(*) FROM sessions"

# 3. 拒否理由をリアルタイムで見る（BAD_SIGNATURE / CLOCK_SKEW 等が出る）
pnpm dlx wrangler tail

# 4. Bridge ページが配信されているか
curl -s -o /dev/null -w "%{http_code}\n" https://watashiba.kagayoi.com/

# 5. 本番のコードが最新か
gh run list --workflow deploy-relay.yml --limit 3
```

**復旧手順**:

- (1) スキーマ不整合なら `wrangler d1 execute --file=schema.sql` を実行。データ消失なら Time Travel で復元
- (2) `/health` の `failed` に応じて `wrangler secret put` で再設定
- (3) `wrangler deploy` で Static Assets ごと再配信
- (4) `deploy-relay.yml` を再実行。型チェック / テストで落ちているなら**まずそれを直す**（本番配信のゲート）

> 💡 `CLOCK_SKEW` が多発している場合はサーバー障害ではなく**ユーザー PC の時計ずれ**。
> v1.0.70 以降のクライアントはサーバーが返す `serverTime` で自動補正して再試行する。

### シナリオ B: 「relay Worker に繋がらない / オンライン表示が出ない」

**想定原因**:

1. `wrangler deploy` の途中失敗、または Worker 自体の障害
2. Secret（`SALT` / `SESSION_HMAC_SECRET`）消失
3. Cloudflare Workers の課金・上限超過
4. Durable Object の障害（PairDO / DeviceDO / RelayDO）

**確認手順**:

```bash
# 依存込みの健康診断（503 なら failed 配列を見る）
curl -s -w "\nHTTP %{http_code}\n" https://watashiba.kagayoi.com/health

# リアルタイムログ（DO の例外は "PairDO error" / "DeviceDO error" で出る）
cd infra/cloudflare/relay && pnpm dlx wrangler tail

# デプロイ履歴
pnpm dlx wrangler deployments list
```

**復旧手順**:

- (1) `pnpm dlx wrangler rollback` または再 deploy
- (2) `/health` の `failed` を見て `wrangler secret put <名前>` で再設定
- (3) <https://dash.cloudflare.com/> で Workers の使用量・請求状況を確認
- (4) `wrangler tail` の DO 例外を確認。TCP/UDP 直結は影響を受けないので、ユーザーには
      「同じ LAN なら転送できる」旨を案内できる

> 💡 **オンライン表示だけ出ない**場合は DeviceDO（presence / inbox）が疑わしい。
> 転送そのものは presence を必要としないので、相手を選んで送信すれば動くことがある。

### シナリオ C: 「ペアリングは成功するが転送が始まらない」

**想定原因**:

1. ファイアウォール未許可（`FirewallHelper` 失敗 / macOS のローカルネットワーク許可拒否）
2. STUN 両方（Cloudflare + Google）失敗
3. リレーへのフォールバックも失敗（RelayDO の 409 = スロット占有、または上限）
4. 接続ノックが届かず着信検知が遅延している

**確認手順**:

```bash
# STUN 疎通
nslookup stun.cloudflare.com
nslookup stun.l.google.com
```

クライアントログ（OS 別パスは上表）で以下を確認:

- `接続完了！ 経路:` — `direct` / `stunAssisted` / `relay` のどれで確立したか
- `TCP 直接接続` `UDP PUNCH` `WebSocket リレー接続開始` — どの段で止まっているか
- `CF inbox WebSocket 接続成立`（Debug）— ノック経路が生きているか

**復旧手順**:

- (1) Windows は Defender ファイアウォールの受信許可、macOS は「システム設定 → プライバシーとセキュリティ →
      ローカルネットワーク」で Ferry を許可、Linux は ufw/firewalld を手動許可
- (2) 別 STUN サーバーの検討（`AppConstants` の STUN URL）
- (3) `wrangler tail` で RelayDO の `409` を確認。CGNAT 環境では直結不可なのでリレー復旧が必須
- (4) v1.0.70 以降は inbox WS に KeepAlive タイムアウトがあり half-open を検出する。
      それ以前は最大 15 秒の安全網ポーリング待ちになるので、更新を案内する

### シナリオ D: 「大きいファイルの転送が途中で切れる」

**想定原因**:

1. リレー経路で Durable Object のメモリ上限（128MB）超過
2. WebSocket の half-open（経路が黙って死んだ）
3. 受信側のディスク空き容量不足

**確認手順**:

クライアントログで:

- `フロー制御 window 発火（受信ドレイン律速に移行）` — 出ていればアプリ層フロー制御は機能している
- `接続完了！ 経路: relay` — リレー経路かどうか
- 切断のタイミング（転送開始からの経過秒）

**復旧手順**:

- (1) 並列送信数を減らす（複数ファイルを一度にドロップしない）。
      ⚠️ フロー制御の窓は**転送 1 件あたり** 32MB なので、10 並列だと最大 320MB が DO に積まれうる
      （既知の課題。CLAUDE.md のマージン論証は単一転送前提）
- (2) v1.0.70 以降へ更新（KeepAliveTimeout により half-open を検出）
- (3) 受信側の空き容量を確認（承認時に `SetLength` で事前確保するため、承認時点でエラーになる）

### シナリオ E: 「自動更新が失敗する / 新規ダウンロードできない」

**想定原因**:

1. R2 の manifest とオブジェクトの不整合（cleanup の誤削除・部分失敗）
2. R2 の含有枠超過で書き込み失敗
3. リリース CI の途中失敗

**確認手順**:

```bash
# 全 5 チャンネルの manifest
for ch in win-x64 win-arm64 osx-arm64 linux-x64 linux-arm64; do
  printf "%-12s " "$ch"
  curl -s -o /dev/null -w "HTTP %{http_code}\n" "https://ferry.kagayoi.com/releases.$ch.json"
done

# 固定名インストーラ（ランディングページの参照先）
curl -s -o /dev/null -w "%{http_code}\n" -r 0-0 https://ferry.kagayoi.com/Ferry-win-x64-Setup.exe

# リリース CI の結果
gh run list --workflow release.yml --limit 3
```

**復旧手順**:

- (1) 対象バージョンを再リリース（踏み台バージョン。上記 rollback 手順）
- (2) Cloudflare ダッシュボードで R2 の使用量を確認し、不要な古い世代を手動削除
- (3) 失敗ジョブのログを確認して再実行。**cleanup の部分失敗は `::warning::` とジョブサマリに出る**
      （v1.0.70 以降）ので、成功扱いでも警告が出ていないか確認する

---

## 監視・ヘルスチェック

### 自動監視

`.github/workflows/relay-healthcheck.yml` が 15 分間隔で:

- `https://watashiba.kagayoi.com/health` への HTTP 200 確認
- 失敗時に Issue 自動作成（本文からこの runbook のシナリオ B へ誘導）

`/health` は D1 / KV / `SESSION_HMAC_SECRET` / `SALT` を実際に検査し、壊れている依存名を
`failed` 配列で返す（v1.0.70 以降）。それ以前は無条件 200 だったため、依存の障害を検知できなかった。

### 監視の穴（把握しておくこと）

| 項目 | 状態 |
|---|---|
| R2 のストレージ使用量 | **監視なし**。含有枠超過は書き込み失敗まで気付けない |
| 接続ノックの到達率 | `wrangler tail` の `knock` 行で `delivered` を確認できるが、集計・アラートはなし |
| 転送成功率 | クライアント側ログのみ。サーバー側に指標なし |
| D1 の行数増加 | `sessions` / `pairing_nonces` は読み取り時に TTL 判定するだけで sweeper がない |

### 手動監視ダッシュボード

| 対象 | URL |
|------|-----|
| Cloudflare Workers | <https://dash.cloudflare.com/?to=/:account/workers/services/view/ferry-relay> |
| Cloudflare D1 | <https://dash.cloudflare.com/?to=/:account/workers/d1> |
| Cloudflare R2 | <https://dash.cloudflare.com/?to=/:account/r2/buckets/ferry-updates> |
| GitHub Actions | <https://github.com/1llum1n4t1s/Ferry/actions> |
| Velopack 配信 | <https://ferry.kagayoi.com/releases.win-x64.json> |

### SLO / SLI (将来検討)

- ペアリング成功率: 99% (目標)
- Relay 稼働率: 99.9% (目標)
- 自動更新成功率: 99% (目標)

---

## インシデント記録 (`incidents/template.md` を参照)

過去のインシデントは `docs/operations/incidents/` 配下に。各インシデントで:

1. 発生時刻 / 発覚時刻 / 復旧時刻
2. 影響範囲 (全ユーザー / 一部)
3. 根本原因
4. 復旧手順
5. 再発防止策

を記録する。
