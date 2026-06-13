# macOS コード署名・公証 運用手順

未署名の `Ferry-osx-arm64-Setup.pkg` は macOS の Gatekeeper に「開発元を確認できなかった」と弾かれる。
Apple の **Developer ID 署名 + 公証 (notarization)** を施すと、ユーザー側の操作なしで無言通過する。

## どこで署名するか（プラットフォーム別の整理）

| OS | 署名方式 | 実行場所 |
|----|---------|---------|
| Windows (win-x64 / win-arm64) | Authenticode (Certum / SimplySign) | **ローカル** `scripts/release-local.ps1` (クラウド署名が CI 不可のため) |
| **macOS (osx-arm64)** | **Developer ID 署名 + notarytool 公証 (App Store Connect API キー方式)** | **CI** `.github/workflows/velopack.yml` (macos ランナー) |
| Linux (linux-x64 / linux-arm64) | 署名不要 | CI |

mac は win と逆で、Apple の署名・公証は macOS 上でしか実行できないため **macos-latest ランナー (CI) で署名する**。
ゆろ君が毎リリース Mac を起動する必要はなく、`release/**` push の既存フローにそのまま乗る。

仕組み: `vpk pack` に `--signAppIdentity` / `--signInstallIdentity` / `--notaryProfile` を渡すと、
Velopack が **`.app` の codesign → `.pkg` の productsign → notarytool への公証申請 → stapler でチケット添付** まで自動実行する。

---

## クレデンシャルの単一の真実の源 (single source of truth)

署名・公証の素材はすべて **`C:\Users\IMT\dev\Secret\secrets.json` の `apple_signing` ブロック** と
`C:\Users\IMT\dev\Secret\apple_signing\` ディレクトリ (証明書 `.p12` ×2 + API キー `.p8`) に集約してある。

```jsonc
"apple_signing": {
  "service": "...",
  "team_id": "SL228Y8UUR",
  "developer_id_application": { "p12_file": "apple_signing/DeveloperID_Application.p12",
                                "identity": "Developer ID Application: Yuichiro Shinozaki (SL228Y8UUR)" },
  "developer_id_installer":   { "p12_file": "apple_signing/DeveloperID_Installer.p12",
                                "identity": "Developer ID Installer: Yuichiro Shinozaki (SL228Y8UUR)" },
  "p12_password": "********",
  "app_store_connect_api": { "key_id": "7Z24XWH2Z3",
                             "issuer_id": "********",
                             "p8_file": "apple_signing/AuthKey_7Z24XWH2Z3.p8",
                             "note": "..." }
}
```

> パス (`p12_file` / `p8_file`) は `C:\Users\IMT\dev\Secret\` ルートからの相対。`developer_id_*` は
> ネストオブジェクトで、**identity 文字列は `.identity` サブフィールド** にある点に注意。

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
| `APPLE_API_KEY_P8_BASE64` | `app_store_connect_api.p8_file` を base64 |
| `APPLE_API_KEY_ID` | `app_store_connect_api.key_id` |
| `APPLE_API_ISSUER_ID` | `app_store_connect_api.issuer_id` |

### 再投入 / ローテーション (secrets.json から自動生成して gh で投入)

証明書更新時などに Secrets を入れ直すスクリプト。値は標準出力に出さず、キー名だけ表示する:

```powershell
$ErrorActionPreference = 'Stop'
$repo = '1llum1n4t1s/Ferry'
$root = 'C:/Users/IMT/dev/Secret'
$s = (Get-Content "$root/secrets.json" -Raw | ConvertFrom-Json).apple_signing
function B64([string]$rel) { [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root $rel))) }
$secrets = [ordered]@{
  APPLE_CERT_APP_P12_BASE64       = B64 $s.developer_id_application.p12_file
  APPLE_CERT_INSTALLER_P12_BASE64 = B64 $s.developer_id_installer.p12_file
  APPLE_CERT_PASSWORD             = $s.p12_password
  APPLE_SIGN_APP_IDENTITY         = $s.developer_id_application.identity
  APPLE_SIGN_INSTALL_IDENTITY     = $s.developer_id_installer.identity
  APPLE_API_KEY_P8_BASE64         = B64 $s.app_store_connect_api.p8_file
  APPLE_API_KEY_ID                = $s.app_store_connect_api.key_id
  APPLE_API_ISSUER_ID             = $s.app_store_connect_api.issuer_id
}
foreach ($k in $secrets.Keys) { gh secret set $k --repo $repo --body ([string]$secrets[$k]); "set: $k" }
```

> ⚠️ Secrets 未投入のまま `release/**` を push すると、osx ジョブが意図的に fail する
> (未署名 pkg の配信を防ぐガード)。証明書を入れ替えたら上記で再投入してからリリースする。

---

## 証明書・API キーを新規発行 / 更新する場合 (Mac 実機)

`apple_signing` の素材を作り直すときの手順。

1. **Developer ID 証明書** (両方必要):
   - `security find-identity -v -p codesigning` で `Developer ID Application` と `Developer ID Installer` を確認
   - 無ければ Xcode → Settings → Accounts → Manage Certificates → **+** で発行
   - キーチェーンアクセスで各証明書 (秘密鍵込み) を **個別に `.p12` でエクスポート** →
     `DeveloperID_Application.p12` / `DeveloperID_Installer.p12` として `Secret/apple_signing/` に保存。
     エクスポートパスワードを `apple_signing.p12_password` に記録
2. **App Store Connect API キー** (公証用):
   - [App Store Connect](https://appstoreconnect.apple.com) → ユーザーとアクセス → インテグレーション (キー) →
     **+** で Developer 権限のキーを生成 → `AuthKey_<KEYID>.p8` をダウンロード (再DL不可)
   - `Secret/apple_signing/` に保存。Key ID と、同ページ上部の **Issuer ID** を
     `app_store_connect_api.key_id` / `issuer_id` に記録
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

  （まずは entitlements 無し = Velopack デフォルトで通す。AOT で弾かれた場合のみ上記を追加する）

- **公証ログの確認** (CI ログに submission-id が出る。Mac から):
  ```bash
  xcrun notarytool log <submission-id> --key AuthKey_7Z24XWH2Z3.p8 --key-id 7Z24XWH2Z3 --issuer <ISSUER_ID>
  ```

- **証明書の有効期限**: Developer ID 証明書は 5 年。失効時は「新規発行 / 更新」手順をやり直して
  `secrets.json` 更新 → Secrets 再投入。

---

## 今すぐ未署名 pkg を入れたい場合 (暫定)

署名対応は **次回リリース以降** の pkg に効く。現在 R2 にある未署名 pkg を今すぐ入れたいときは、
ダウンロード後に隔離属性を剥がす:

```bash
xattr -dr com.apple.quarantine ~/Downloads/Ferry-osx-arm64-Setup.pkg
open ~/Downloads/Ferry-osx-arm64-Setup.pkg
# インストール後もアプリ起動時に弾かれたら
xattr -dr com.apple.quarantine /Applications/Ferry.app
```
