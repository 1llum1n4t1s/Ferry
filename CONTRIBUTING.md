# 開発者向けガイド

利用者向けのインストール・使い方は [`README.md`](README.md)、全体像は [`DESIGN.md`](DESIGN.md)、
実装の詳細（接続の非対称手順・Native AOT 制約・既知の落とし穴）は [`references/architecture.md`](references/architecture.md)、
作業規約と必須検証は [`AGENTS.md`](AGENTS.md) を参照。

## 前提条件

- .NET 11 SDK
- Bun と pnpm 12.21.0（relay Worker を触る場合のみ。導入手順は [`AGENTS.md`](AGENTS.md#依存関係の保守) と [relay README](infra/cloudflare/relay/README.md)。lockfile は pnpm 固定）
- Cloudflare wrangler CLI（relay Worker / Bridge ページを手動デプロイする場合のみ。relay の `node_modules` 同梱版を使う）
- 対応 OS: Windows 10/11 (x64 / arm64), macOS (arm64), Linux (x64 / arm64)

## ビルド・実行・テスト

コマンドは二重管理でずれやすいため、正本を [`AGENTS.md`](AGENTS.md#ビルドテストコマンド) の「ビルド・テストコマンド」に一本化している。
デバッグビルド・ローカル起動・Native AOT 発行・アプリのテスト・relay Worker の型チェックとテスト・実ウィンドウの回帰検証はそちらを参照する。

テスト内の非同期メソッドには `TestContext.Current.CancellationToken` を渡す（xUnit1051 警告回避）。

## CI

| workflow | トリガー | 内容 |
|---|---|---|
| `dotnet-build.yml`（".NET Build"） | main への PR / main への push / 手動 | .NET の build + test（テスト本体は `test.yml`、`release.yml` の配信前にも使う）。**relay は素通りする** |
| `relay-check.yml`（"Relay Check"） | `infra/cloudflare/relay/**` の PR | relay の `tsc --noEmit` + `bun run test` |
| `deploy-relay.yml` | `infra/cloudflare/relay/**` を main に push | 検証 → `wrangler deploy` で自動配信 |
| `release.yml` | 軽量タグ `v<x.y.z>` の push | macOS / Linux の発行・署名・公証・R2 アップロード |
| `relay-healthcheck.yml` | 15 分ごとの cron | relay の死活監視 |

## リリース

版数管理・commit・版タグの作成・push は `/vava` スキルが一括で行う。手動で追う場合の要点:

- バージョンは `Directory.Build.props` の `<Version>` が単一の正本
- **Windows (win-x64 / win-arm64) も CI** — `release.yml` の `windows-release` ジョブが macOS / Linux の配信後に
  `scripts/release-local.ps1` を実行し、Sigilune で Authenticode 署名する（Secrets は Environment `release` から渡す。承認待ちはない）。
  ローカル実行は `-SkipUpload` の検証と `-UploadOnly` の再開用
- **macOS / Linux は CI** — main の commit に付けた軽量タグ `vX.Y.Z` の push で `release.yml` が（`guard` ジョブでタグ・commit・版を検査したうえで）発行 → Velopack パッケージ化 → R2 へアップロード
- relay Worker は上記のとおり main への push で自動デプロイされる（手動デプロイの手順は [`AGENTS.md`](AGENTS.md#ビルドテストコマンド) を参照。製品ページの配信は `vps-web` 側の `deploy-lp.ps1`）

## 依存関係

`/deps` スキルが .NET / relay（pnpm lockfile）/ GitHub Actions を横断して更新する。GitHub Actions は SHA 固定で参照し、
横に `# vX.Y.Z` のバージョンコメントを添える運用。
