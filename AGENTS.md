# AGENTS.md

この文書は Ferry の作業規約と必須検証を定める。利用者向けの説明は [README.md](README.md)、設計の正本は [DESIGN.md](DESIGN.md) を参照する。

## ビルド・テストコマンド

```bash
# デバッグビルド（単体プロジェクト / ソリューション全体 Ferry.slnx）
dotnet build src/Ferry/Ferry.csproj
dotnet build Ferry.slnx          # アプリ + Ferry.Tests（StartupSmoke は含まない）

# アプリをローカル起動して実機確認する（Debug は AvaloniaUI.DeveloperTools へ接続する）
# ビルドが通っても起動時に落ちる類のバグ（DevTools 二重アタッチ等）はここでしか見つからない
dotnet run --project src/Ferry/Ferry.csproj

# リリース発行 (Native AOT、ランタイム指定が必須)
# CI は win-x64 / win-arm64 / osx-arm64 / linux-x64 / linux-arm64 の 5 ランタイムを発行する
dotnet publish src/Ferry/Ferry.csproj -c Release -r win-x64

# テスト全実行
dotnet test tests/Ferry.Tests/Ferry.Tests.csproj

# テスト単体実行（クラス名 or メソッド名でフィルタ）
dotnet test tests/Ferry.Tests/Ferry.Tests.csproj --filter "FullyQualifiedName~FileChunkerTests"

# 実ウィンドウと実サービスの回帰検証（GUI を利用できる環境で実行）
dotnet run --project tests/Ferry.StartupSmoke/Ferry.StartupSmoke.csproj -c Release -- --regressions --output docs/verification/local-regressions

# relay Worker（シグナリング / リレー / Bridge ページ）の型チェック + テスト
cd infra/cloudflare/relay && pnpm exec tsc --noEmit && pnpm test

# relay Worker のテスト単体実行（ファイル指定 / テスト名フィルタ）
cd infra/cloudflare/relay && pnpm vitest run tests/signaling-ratelimit.test.ts
cd infra/cloudflare/relay && pnpm vitest run -t "rate limit"

# relay Worker の手動デプロイ（通常は main push で deploy-relay.yml が自動配信するので不要）
cd infra/cloudflare/relay && pnpm exec wrangler deploy
```

> Windows 向けリリースは `pwsh scripts/release-local.ps1` でローカル実行する（コード署名のため）。macOS / Linux は `release/**` ブランチへの push で CI が配信する。配信境界は [DESIGN.md](DESIGN.md#配布と運用の境界)、詳細手順は [references/architecture.md](references/architecture.md) の「自動更新と配信（CI/CD）」を参照する。
>
> 製品ページの配信は `../vps-web/deploy/deploy-lp.ps1` を使う。公開ホスト・更新ファイルの既存経路を維持する。
>
> PR（→ main）は `.github/workflows/dotnet-build.yml`（".NET Build"）が build + test で検証する。Markdown と `docs/**` だけの変更は対象外。配信 CI とは別ワークフローなので、コード変更の正否はこの PR CI で確認する。
>
> **relay（`infra/cloudflare/relay/**`）の PR は `relay-check.yml`（"Relay Check"）**の `tsc --noEmit` + `pnpm test` で検証する。.NET の CI では relay を検証しない。同じ検証は `deploy-relay.yml`（main push）でも配信前に実行する。

`Ferry.StartupSmoke` はソリューションと PR CI の対象外なので、起動時表示・言語・設定復旧・受信承認/中断・UDP 送信枠を変更した場合は上記の回帰検証を別途実行し、`result.json` と PNG を保存する。対象ケースと検証範囲は [検証プロジェクトの README](tests/Ferry.StartupSmoke/README.md) を参照する。PNG は Avalonia の描画結果であり OS の合成結果ではないため、macOS の表示不具合は macOS 上で配布アプリの実起動と Dock 復帰も確認する。Bridge を変更した場合は relay の検証に加え、端末名に `%` や URL エンコードに見える文字列を含む QR の読み取りを確認する。

## 依存関係の保守

- NuGet の直接依存は `src/Ferry/Ferry.csproj` と `tests/Ferry.Tests/Ferry.Tests.csproj`、解決結果は各ディレクトリの `packages.lock.json` で管理する。参照変更時は対応する lockfile も更新し、Avalonia 本体・Desktop・Fluent・Fonts.Inter の版を揃える。
- `tests/Ferry.StartupSmoke/` は本体へのプロジェクト参照を持ち、独自の `packages.lock.json` も照合する。現行 Dependabot の NuGet 対象には含まれない。
- relay は `infra/cloudflare/relay/package.json`、`pnpm-lock.yaml`、`pnpm-workspace.yaml` を照合する。インストールは同ディレクトリで `pnpm install --frozen-lockfile`。依存更新時は `allowBuilds`、リリース待機の例外、`undici` override の必要性も確認し、型チェックとテストを実行する。
- Dependabot は `.github/dependabot.yml` で GitHub Actions、上記 2 つの NuGet プロジェクト、relay の npm 依存を監視する。プロジェクト追加・移動時は更新対象も照合する。

## アーキテクチャ

システム全体の責務・境界・不変条件・設計判断は [DESIGN.md](DESIGN.md)、実装の詳細は [references/architecture.md](references/architecture.md) を正本とする。**下の領域を触る前に必ず該当節を読む**。

| 触る対象 | 読む節 |
| --- | --- |
| 全体像、プロジェクト構成 | 全体構造 |
| 画面、ViewModel、サービス登録 | Avalonia UI ネイティブ + MVVM サービス層 |
| 接続の確立、フォールバック、着信検知 | 接続フロー（3 階層フォールバック）/ 着信検知（接続ノック）と CF 使用量 |
| ペアリング、宛先リスト、オンライン検出 | ペアリングフロー / 宛先リスト / プレゼンス監視 |
| relay Worker、Cloudflare 側 | Cloudflare バックエンド構造（relay Worker） |
| ファイル転送の中身、承認まわり | 転送プロトコル / 承認プロトコル (v1〜v8 で大改修) |
| 転送 UI、接続短縮 | 転送 UI / 操作と接続短縮 |
| 多言語リソース | ローカライズ（18 言語） |
| AOT でのリフレクション、trim 警告 | Native AOT 制約 |
| Win / mac / Linux の差分 | プラットフォーム差の吸収 |
| 自動更新、CI/CD | 自動更新と配信（CI/CD） |
| テスト、ログ出力 | テスト / ログとデバッグ |

## 実装・検証上の制約

- 接続確立中の送信は既存接続に相乗りし、中断後も Connecting を抜けるまで待つ。InFlightConnectJoinMs（30 秒）と ConnectSettleWaitMs（3 秒）の経路を変更する場合は、ConnectionServiceInFlightJoinTests と実際の接続・送信経路を確認する。役割調停と同時接続の制限は [DESIGN.md](DESIGN.md#着信検知と接続確立) を参照する。
- Native AOT 向けに、JSON モデル追加時は対応する JsonSerializerContext も更新する。Debug の開発ツールのアタッチは Program.cs の WithDeveloperTools() に集約し、画面側で二重にアタッチしない。起動に関わる変更はビルドだけで終えず、上記のローカル起動で確認する。
- 暗号ハンドシェイクを変更する場合は、PairSecret を持つペアと旧ペアの互換経路を区別して確認する。認証と転送の不変条件は [DESIGN.md](DESIGN.md#重要な不変条件) を参照する。
- settings.json / peers.json の保存は一時ファイルからのリネームと破損 JSON の .corrupt-* 退避を維持する。DeviceId の再生成はペア関係に影響するため、保存・読み込みの変更時は既存 ID の保持を確認する。
- 受信承認・中断を変更する場合は、ファイル作成待ち中の切断/拒否/取消で状態が復活せず部分ファイルが削除されること、複数 peer 接続で Reject/FlowAck が元の送信 peer に届くこと、同名フォルダの保存先が peer と転送終了をまたいで混在しないことを確認する。
- relay の実装・デプロイ手順は [infra/cloudflare/relay/README.md](infra/cloudflare/relay/README.md)、障害切り分けは [docs/operations/runbook.md](docs/operations/runbook.md) を参照する。使用量の確認は Cloudflare GraphQL Analytics の workersInvocationsAdaptive / httpRequestsAdaptiveGroups を使う。
- Firebase と旧 VPS の ferry-relay / coturn を実行・配信経路に戻さない。現在のバックエンド構成は [DESIGN.md](DESIGN.md#主要コンポーネント)、移行理由は [docs/design/cf-only-migration.md](docs/design/cf-only-migration.md) を参照する。

## 言語

コード内コメント、コミットメッセージ、ユーザーへの応答はすべて **日本語** で行う。

## ドメイン移行（2026-07 開始・期限 2027/05/31）

屋号を **Kagayoi** に統一したため、配信ドメインを `nephilim.jp` から `kagayoi.com` へ移行中。方針の全体像はユーザーグローバルの `AGENTS.md` §屋号とドメイン を参照する。

- **旧ドメイン `nephilim.jp` はレジストラで廃止申請済みで 2027/05/31 に失効する**（延長しない）。それまでに出荷済みバイナリを新ドメインへ移行しきる。
- 旧ホストの Worker route / custom domain は**期限まで消さない**。消すと出荷済みアプリの自動更新が止まる。
- `nephilim.jp` の Redirect Rules は `/` だけを 301 する。`releases.*.json` / `*.nupkg` / `*-Setup.exe` は転送せず R2 が配信を続ける。
- 配信は `ferry.kagayoi.com`（R2 `ferry-updates`）、リレーは `watashiba.kagayoi.com`（渡し場）。旧 `ferry.nephilim.jp` / `relay.ferry.nephilim.jp` は wrangler の route に併記して残してある。`App.axaml.cs` の `UpdateBaseUrl` と relay の URL を書き換えるときは、旧ホストの route を消さないこと。
