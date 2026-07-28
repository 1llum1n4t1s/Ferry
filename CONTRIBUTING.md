# 開発者向けガイド

利用者向けのインストール・使い方は [`README.md`](README.md)、全体像は [`docs/architecture.md`](docs/architecture.md)、
実装の正本（接続の非対称手順・Native AOT 制約・既知の落とし穴）は [`CLAUDE.md`](CLAUDE.md) を参照。

## 前提条件

- .NET 10 SDK
- Node.js + pnpm（relay Worker を触る場合のみ。lockfile は pnpm 固定）
- Cloudflare wrangler CLI（relay Worker / Bridge ページを手動デプロイする場合のみ）
- 対応 OS: Windows 10/11 (x64 / arm64), macOS (arm64), Linux (x64 / arm64)

## ビルドと実行

```bash
# デバッグビルド（単体プロジェクト / ソリューション全体）
dotnet build src/Ferry/Ferry.csproj
dotnet build Ferry.slnx

# ローカル起動して実機確認する
dotnet run --project src/Ferry/Ferry.csproj

# リリース発行 (Native AOT、ランタイム指定が必須)
dotnet publish src/Ferry/Ferry.csproj -c Release -r win-x64
```

## テスト

```bash
# アプリのテスト（全件 / フィルタ）
dotnet test tests/Ferry.Tests/Ferry.Tests.csproj
dotnet test tests/Ferry.Tests/Ferry.Tests.csproj --filter "FullyQualifiedName~FileChunkerTests"

# relay Worker の型チェック + テスト（全件 / ファイル指定）
cd infra/cloudflare/relay && pnpm exec tsc --noEmit && pnpm test
cd infra/cloudflare/relay && pnpm vitest run tests/signaling-ratelimit.test.ts
```

テスト内の非同期メソッドには `TestContext.Current.CancellationToken` を渡す（xUnit1051 警告回避）。

## CI

| workflow | トリガー | 内容 |
|---|---|---|
| `dotnet-build.yml`（".NET Build"） | main への PR / 手動 | .NET の build + test。**relay は素通りする** |
| `relay-check.yml`（"Relay Check"） | `infra/cloudflare/relay/**` の PR | relay の `tsc --noEmit` + `pnpm test` |
| `deploy-relay.yml` | `infra/cloudflare/relay/**` を main に push | 検証 → `wrangler deploy` で自動配信 |
| `deploy-landing.yml` | `web/**` を main に push | ランディングページを配信 |
| `release.yml` | `release/**` への push | macOS / Linux の発行・署名・公証・R2 アップロード |
| `relay-healthcheck.yml` | 15 分ごとの cron | relay の死活監視 |

## リリース

版数管理・commit・ブランチ作成・古いブランチ整理は `/vava` スキルが一括で行う。手動で追う場合の要点:

- バージョンは `Directory.Build.props` の `<Version>` が単一の正本
- **Windows (win-x64 / win-arm64) はローカル署名リリース** — `pwsh scripts/release-local.ps1`。
  Authenticode 署名に SimplySign Desktop のトークンログインとスマホ承認が要るため CI からは署名できない
- **macOS / Linux は CI** — `release/X.Y.Z` ブランチへの push で `release.yml` が発行 → Velopack パッケージ化 → R2 へアップロード
- relay Worker は上記のとおり main への push で自動デプロイされる（手動なら `cd infra/cloudflare/relay && pnpm dlx wrangler deploy`）

## 依存関係

`/deps` スキルが .NET / pnpm / GitHub Actions を横断して更新する。GitHub Actions は SHA 固定で参照し、
横に `# vX.Y.Z` のバージョンコメントを添える運用。
