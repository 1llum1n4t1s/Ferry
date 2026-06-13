# macOS コード署名・公証 運用手順

未署名の `Ferry-osx-arm64-Setup.pkg` は macOS の Gatekeeper に「開発元を確認できなかった」と弾かれる。
Apple の **Developer ID 署名 + 公証 (notarization)** を施すと、ユーザー側の操作なしで無言通過する。

## どこで署名するか（プラットフォーム別の整理）

| OS | 署名方式 | 実行場所 |
|----|---------|---------|
| Windows (win-x64 / win-arm64) | Authenticode (Certum / SimplySign) | **ローカル** `scripts/release-local.ps1` (クラウド署名が CI 不可のため) |
| **macOS (osx-arm64)** | **Developer ID 署名 + notarytool 公証 (app-specific password 方式)** | **CI** `.github/workflows/velopack.yml` (macos ランナー) |
| Linux (linux-x64 / linux-arm64) | 署名不要 | CI |

mac は win と逆で、Apple の署名・公証は macOS 上でしか実行できないため **macos-latest ランナー (CI) で署名する**。
ゆろ君が毎リリース Mac を起動する必要はなく、`release/**` push の既存フローにそのまま乗る。

仕組み: `vpk pack` に `--signAppIdentity` / `--signInstallIdentity` / `--notaryProfile` を渡すと、
Velopack が **`.app` の codesign → `.pkg` の productsign → notarytool への公証申請 → stapler でチケット添付** まで自動実行する。

> ⚠️ **公証は app-specific password 方式を使う（App Store Connect API キー方式は避ける）**。
> notarytool は **Team Key + Developer 権限** の API キーでないと弾き、しかもエラーを `Error: invalidAsn1`
> (.p8 形式エラーに見せかけて) 返す。個人キー/権限不足だと .p8 自体が健全でも invalidAsn1 になり詰む
> (v1.0.53 で踏破)。app-specific password 方式なら Apple ID が Developer アカウントなので種類/権限の罠が無い。

---

## クレデンシャルの単一の真実の源 (single source of truth)

署名・公証の素材はすべて **`C:\Users\IMT\dev\Secret\secrets.json` の `apple_signing` ブロック** と
`C:\Users\IMT\dev\Secret\apple_signing\` ディレクトリ (証明書 `.p12` ×2) に集約してある。

```jsonc
"apple_signing": {
  "team_id": "SL228Y8UUR",
  "apple_id": "<Apple Developer アカウントのメールアドレス>",          // 公証用
  "app_specific_password": "xxxx-xxxx-xxxx-xxxx",                      // appleid.apple.com 発行
  "developer_id_application": { "p12_file": "apple_signing/DeveloperID_Application.p12",
                                "identity": "Developer ID Application: Yuichiro Shinozaki (SL228Y8UUR)" },
  "developer_id_installer":   { "p12_file": "apple_signing/DeveloperID_Installer.p12",
                                "identity": "Developer ID Installer: Yuichiro Shinozaki (SL228Y8UUR)" },
  "p12_password": "********"
}
```

> パス (`p12_file`) は `C:\Users\IMT\dev\Secret\` ルートからの相対。`developer_id_*` はネストオブジェクトで
> identity 文字列は **`.identity` サブフィールド** にある。
> (`app_store_connect_api` ブロックは API キー方式の名残。notarytool では使わない。)

GitHub Secrets はこのブロックから派生させた値にすぎない (下記スクリプトで再生成できる)。

---

## GitHub Secrets (リポジトリ `1llum1n4t1s/Ferry`、計 8 個)

| Secret 名 | 由来 (`apple_signing.*`) |
|-----------|--------------------------|
| `APPLE_CERT_APP_P12_BASE64` | `developer_id_application.p12_file` を base64 |
| `APPLE_CERT_INSTALLER_P12_BASE64` | `developer_id_installer.p12_file` を base64 |
| `APPLE_CERT_PASSWORD` | `p12_password` |
| `APPLE_SIGN_APP_IDENTITY` | `developer_id_application.identity` |
| `APPLE_SIGN_INSTALL_IDENTITY` | `developer_id_installer.identity` |
| `APPLE_ID` | `apple_id`（公証用 Apple ID メール） |
| `APPLE_TEAM_ID` | `team_id`（= SL228Y8UUR） |
| `APPLE_APP_PASSWORD` | `app_specific_password` |

### 再投入 / ローテーション (secrets.json から自動生成して gh で投入)

証明書更新・パスワード再発行時などに Secrets を入れ直すスクリプト。値は標準出力に出さず、キー名だけ表示する:

```powershell
$ErrorActionPreference = 'Stop'
$repo = '1llum1n4t1s/Ferry'
$root = 'C:/Users/IMT/dev/Secret'
$a = (Get-Content "$root/secrets.json" -Raw | ConvertFrom-Json).apple_signing
function B64([string]$rel) { [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root $rel))) }
$secrets = [ordered]@{
  APPLE_CERT_APP_P12_BASE64       = B64 $a.developer_id_application.p12_file
  APPLE_CERT_INSTALLER_P12_BASE64 = B64 $a.developer_id_installer.p12_file
  APPLE_CERT_PASSWORD             = $a.p12_password
  APPLE_SIGN_APP_IDENTITY         = $a.developer_id_application.identity
  APPLE_SIGN_INSTALL_IDENTITY     = $a.developer_id_installer.identity
  APPLE_ID                        = $a.apple_id
  APPLE_TEAM_ID                   = $a.team_id
  APPLE_APP_PASSWORD              = $a.app_specific_password
}
foreach ($k in $secrets.Keys) { gh secret set $k --repo $repo --body ([string]$secrets[$k]); "set: $k" }
```

> ⚠️ Secrets 未投入のまま `release/**` を push すると、osx ジョブが意図的に fail する
> (未署名 pkg の配信を防ぐガード)。証明書/パスワードを入れ替えたら上記で再投入してからリリースする。

---

## 証明書・パスワードを新規発行 / 更新する場合 (Mac 実機)

`apple_signing` の素材を作り直すときの手順。

1. **Developer ID 証明書** (両方必要):
   - `security find-identity -v -p codesigning` で `Developer ID Application` と `Developer ID Installer` を確認
   - 無ければ Xcode → Settings → Accounts → Manage Certificates → **+** で発行
   - キーチェーンアクセスで各証明書 (秘密鍵込み) を **個別に `.p12` でエクスポート** →
     `DeveloperID_Application.p12` / `DeveloperID_Installer.p12` として `Secret/apple_signing/` に保存。
     エクスポートパスワードを `apple_signing.p12_password` に記録
2. **公証用 app-specific password**:
   - [appleid.apple.com](https://appleid.apple.com) → サインインとセキュリティ → **アプリ用パスワード** → 生成
   - `apple_signing.app_specific_password` に記録。`apple_id` に Apple Developer アカウントのメール、
     `team_id` に 10 桁の Team ID (`security find-identity` の identity 括弧内) を記録
3. `secrets.json` の `apple_signing` を更新 → 上記スクリプトで GitHub Secrets を再投入

---

## リリースと検証

1. 通常どおり `/vava` → `release/x.y.z` push
2. CI の Velopack ジョブが osx-arm64 を署名・公証して R2 (`ferry-updates`) に配信
3. Mac でダウンロードして確認:

```bash
# 配布 pkg 自体の検証
spctl -a -vvv -t install Ferry-osx-arm64-Setup.pkg

# インストール後のアプリ検証
codesign -dv --verbose=4 /Applications/Ferry.app
spctl -a -vvv /Applications/Ferry.app
```

`accepted` かつ `source=Notarized Developer ID` が出れば成功。ダブルクリックで警告なく起動する。

---

## トラブルシュート

- **`notarytool` が `Error: invalidAsn1`**: app-specific password 方式では通常出ない。API キー(.p8)方式に
  戻すと、API キーが Team Key + Developer 権限でない場合にこのエラーになる (本ファイル冒頭の警告参照)。
  app-specific password 方式を維持すること。

- **公証結果が `Invalid`**: Native AOT の hardened runtime で許可が足りない可能性。
  `build/resources/app/App.entitlements` を作り、velopack.yml の macOS `vpk pack` に
  `--signEntitlements build/resources/app/App.entitlements` を足す。.NET の推奨 entitlements:

  ```xml
  <?xml version="1.0" encoding="UTF-8"?>
  <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
  <plist version="1.0">
  <dict>
    <key>com.apple.security.cs.allow-jit</key><true/>
    <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
    <key>com.apple.security.cs.allow-dyld-environment-variables</key><true/>
    <key>com.apple.security.cs.disable-library-validation</key><true/>
  </dict>
  </plist>
  ```

  （v1.0.53 時点では entitlements 無し = Velopack デフォルトで公証が通った。AOT で弾かれた場合のみ追加する）

- **公証ログの確認** (CI ログに submission-id が出る。Mac から):
  ```bash
  xcrun notarytool log <submission-id> --keychain-profile ferry-notary
  ```

- **証明書の有効期限**: Developer ID 証明書は 5 年。失効時は「新規発行 / 更新」手順をやり直して
  `secrets.json` 更新 → Secrets 再投入。app-specific password は失効時に appleid.apple.com で再発行。

---

## 今すぐ未署名 pkg を入れたい場合 (暫定)

署名対応は **v1.0.53 以降** の pkg に効く。それ以前の未署名 pkg を入れたいときは、
ダウンロード後に隔離属性を剥がす:

```bash
xattr -dr com.apple.quarantine ~/Downloads/Ferry-osx-arm64-Setup.pkg
open ~/Downloads/Ferry-osx-arm64-Setup.pkg
# インストール後もアプリ起動時に弾かれたら
xattr -dr com.apple.quarantine /Applications/Ferry.app
```
