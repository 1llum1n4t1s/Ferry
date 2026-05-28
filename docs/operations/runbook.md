# Ferry 運用 Runbook

本番運用時の障害対応 / 復旧 / リリース時のロールバック手順を集約。
rere レビュー #F-008 / #F-015 対応で新設。

---

## 📋 目次

1. [障害対応フロー (Day-2 Ops)](#障害対応フロー)
2. [リリース後 rollback 手順](#リリース後-rollback-手順)
3. [Secret rotation 手順](#secret-rotation-手順)
4. [障害シナリオ別 runbook](#障害シナリオ別-runbook)
5. [監視・ヘルスチェック](#監視ヘルスチェック)

---

## 障害対応フロー

### 初動 5 分で確認すること

1. **影響範囲確認**: 全ユーザー影響か、特定環境のみか
2. **症状の特定**:
   - ペアリングできない → Firebase 経路を疑う
   - ペアリング後の転送失敗 → 3 階層フォールバック (TCP/UDP/Relay) のいずれが死んでいるか
   - 自動更新失敗 → Cloudflare R2 / Velopack manifest を疑う
3. **ログ取得**: `%LOCALAPPDATA%\Ferry\logs\Ferry_YYYYMMDD.log` のうち障害発生時刻前後 60 分
4. **Status pages**:
   - Firebase: https://status.firebase.google.com/
   - Cloudflare: https://www.cloudflarestatus.com/
   - GitHub Actions: https://www.githubstatus.com/

---

## リリース後 rollback 手順

### Velopack 更新の rollback

> ⚠️ **重要**: 現在の `release.yml` の cleanup step は manifest 外の旧 `.nupkg` を **削除** する Aggressive 戦略。
> 緊急 rollback したい場合、削除済みの旧 nupkg は復元できないので、**踏み台バージョンの redeploy** で
> 対処する必要がある。

**手順**:
1. `Directory.Build.props` の `<Version>` を **rollback したい旧バージョンより 1 patch 上** に変更
   (例: 1.0.38 で問題発覚 → 1.0.39 として 1.0.37 のコードを再リリース)
2. 該当バージョンのコードを branch out (`git checkout v1.0.37 -- src/`)
3. `release/<新バージョン>` ブランチを push → release.yml が走る
4. R2 に新 nupkg が配置 → ユーザーは次回 Check4Update でこちらを取得

**TODO**: 将来的に R2 cleanup を「archive/ への移動」に変更すれば、archive/<old-version>.nupkg を
manifest に書き戻すことで 30 日以内の rollback が即時可能になる (rere #F-008 根本修正案)。

### Cloudflare Workers Relay の rollback

```bash
# 直近 deploy 履歴を確認
wrangler deployments list --name ferry-relay

# 1 つ前の deploy に rollback
wrangler rollback --name ferry-relay
```

### Firebase Hosting の rollback

```bash
# Firebase コンソール → Hosting → リリース履歴 から手動 rollback
# または CLI:
firebase hosting:clone <site-id>:<version> <site-id>:live
```

### Firebase Database rules の rollback

`database.rules.json` をリポジトリ管理に置いているので、git で旧版をチェックアウトして `firebase deploy --only database` で適用。

---

## Secret rotation 手順

### CLOUDFLARE_API_TOKEN

1. **新トークン作成**: https://dash.cloudflare.com/profile/api-tokens
   - 権限: `Workers R2 Storage:Edit` + `Zone:Workers Routes:Edit` + `Zone:DNS:Edit`
   - スコープ: `Account: 1llum1n4t1.net` の対象アカウントのみ
2. **GitHub Secrets 更新**: Settings → Secrets and variables → Actions → `CLOUDFLARE_API_TOKEN`
3. **動作確認**: 適当な release branch (例: `release/0.0.999-rotation-test`) を push して r2-upload が通ることを確認
4. **旧トークン revoke**: Cloudflare ダッシュボードで旧トークンを Delete

### CLOUDFLARE_ACCOUNT_ID

通常 rotation 不要 (アカウント変更時のみ)。

### FIREBASE_SERVICE_ACCOUNT_FERRY_EDF09

> 2026-05-29 認証方式移行: 旧 `FIREBASE_TOKEN` (`firebase login:ci`) は Google アカウント全権限相当の legacy 認証で Firebase 公式が non-recommended 化したため、Service Account JSON + 公式 GitHub Action (`FirebaseExtended/action-hosting-deploy@v0`) に置換した。

1. **Service Account 作成**: [Firebase Console](https://console.firebase.google.com/project/ferry-edf09) → ⚙️プロジェクト設定 → サービスアカウント タブ → 「新しい秘密鍵を生成」 → JSON ダウンロード
2. **権限確認**: 作成された SA は自動で `roles/firebase.developAdmin` 等が付与される。最小権限化したい場合は [GCP IAM Console](https://console.cloud.google.com/iam-admin/iam?project=ferry-edf09) で `roles/firebasehosting.admin` だけ残す
3. **GitHub Secrets 更新**: `gh secret set FIREBASE_SERVICE_ACCOUNT_FERRY_EDF09 --repo 1llum1n4t1s/Ferry < path/to/sa.json` で JSON 全文を登録 (ローカル JSON ファイルは登録後に **削除** すること)
4. **動作確認**: release branch を push して `Firebase Hosting Deploy (Bridge)` job が通ることを確認
5. **旧 Key 無効化**: Firebase Console → サービスアカウント → 過去の Key を「削除」

#### 旧 FIREBASE_TOKEN の片付け (移行直後のみ)

旧 `FIREBASE_TOKEN` がもし環境に残っていれば revoke + secret 削除:

```bash
firebase logout:ci <旧トークン値>         # ローカルで実行
gh secret delete FIREBASE_TOKEN --repo 1llum1n4t1s/Ferry  # 残っていれば削除
```

### Firebase Database SALT (Cloudflare Workers Relay)

1. **新 SALT 生成**: `openssl rand -hex 32` で 64 文字 hex を生成
2. **Workers Secret 更新**: `cd infra/cloudflare/relay && wrangler secret put SALT` で新値を入れる
3. **クライアント側に影響なし** (pairId hash は relay 内部のみで使用)

### 漏洩時の対応

- **GitHub Secrets が PR 経由で漏洩した可能性**: 即時 revoke + 新トークン作成 + GitHub Secrets 更新を順に実行
- **`.cf_token` ファイル (ローカル) が漏洩した可能性**: 即時 revoke + 再発行 + `notepad %USERPROFILE%\.cf_token` で再保存 (Linux/macOS なら `~/.cf_token`)
- **チャットに貼ってしまった場合**: 漏洩 token を生かしたまま新 token を作らない (revoke → 再発行 → 再保存 の順)

---

## 障害シナリオ別 runbook

### シナリオ A: 「全ユーザーがペアリングできない」

**想定原因**:
1. Firebase Realtime DB 全停止
2. Firebase rules 誤更新 (`pairings/` への write が deny)
3. firebase-cleanup.yml の cron が pairings/ や sessions/ も誤削除

**確認手順**:
```bash
# Firebase RTDB 直接アクセスで rules が機能しているか確認
curl -X PUT "https://ferry-edf09-default-rtdb.firebaseio.com/sessions/healthcheck.json" \
  -d '{"DisplayName":"test","CreatedAt":'$(date +%s%N | cut -b1-13)'}'
# → 200 if rules が緩い、401/403 if rules が deny

# Firebase rules の現状を表示
firebase database:get / --shallow=true
```

**復旧手順**:
- (1) なら Firebase 復旧待ち
- (2) なら `gcloud auth activate-service-account --key-file=<sa.json>` で SA 認証 → `firebase deploy --only database --project ferry-edf09` で正規版を再適用 (旧 `--token "$FIREBASE_TOKEN"` 経路は legacy のため非推奨)
- (3) なら firebase-cleanup.yml の対象 path を確認、誤削除した pair は再ペアリング案内

### シナリオ B: 「Cloudflare Workers Relay に繋がらない」

**想定原因**:
1. `wrangler deploy` の途中失敗
2. `SALT` secret 消失
3. Cloudflare Workers Paid プラン未支払
4. DO Hibernation バグ

**確認手順**:
```bash
# Relay 健康診断 (rere #F-011 で CI 化済み)
curl -s -o /dev/null -w "%{http_code}\n" https://relay.ferry.nephilim.jp/health
# → 200 if OK

# wrangler tail でリアルタイムログ
cd infra/cloudflare/relay && wrangler tail --name ferry-relay
```

**復旧手順**:
- (1) なら `wrangler rollback` または再 deploy
- (2) なら `wrangler secret put SALT` で再設定 (新 SALT 生成)
- (3) なら https://dash.cloudflare.com/billing で確認
- (4) なら Cloudflare Status を確認、TCP/UDP 経路へのフォールバックを案内

### シナリオ C: 「ペアリングは成功するが転送が始まらない」

**想定原因**:
1. Firewall 未許可 (`FirewallHelper.cs` 失敗)
2. STUN 両方 (Cloudflare + Google) 失敗
3. Cloudflare Workers Free 接続数到達
4. `_pendingSendApprovals` の TCS リーク

**確認手順**:
```bash
# STUN 疎通確認
nslookup stun.cloudflare.com
nslookup stun.l.google.com

# クライアント側ログで Probe 結果と TCP/UDP/Relay のフォールバック段階を確認
grep "経路 Probe\|TCP\|UDP\|Relay" %LOCALAPPDATA%\Ferry\logs\Ferry_*.log | tail -50
```

**復旧手順**:
- (1) なら Windows Defender Firewall 例外追加を案内
- (2) なら別 STUN サーバー利用を検討 (`ConnectionService.cs` で STUN URL 変更)
- (3) なら Cloudflare ダッシュボードで request 数確認
- (4) なら最新版へ更新依頼

---

## 監視・ヘルスチェック

### 自動監視 (rere #F-011 で CI 化)

`.github/workflows/relay-healthcheck.yml` が 15 分間隔で:
- `https://relay.ferry.nephilim.jp/health` への HTTP 200 確認
- 失敗時に Issue 自動作成

### 手動監視ダッシュボード

| 対象 | URL |
|------|-----|
| Cloudflare Workers | https://dash.cloudflare.com/?to=/:account/workers/services/view/ferry-relay |
| Cloudflare R2 | https://dash.cloudflare.com/?to=/:account/r2/buckets/ferry-updates |
| Firebase Realtime DB | https://console.firebase.google.com/project/ferry-edf09/database |
| Firebase Hosting | https://console.firebase.google.com/project/ferry-edf09/hosting |
| GitHub Actions | https://github.com/1llum1n4t1s/Ferry/actions |
| Velopack 配信 | https://ferry.nephilim.jp/releases.win-x64.json |

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
