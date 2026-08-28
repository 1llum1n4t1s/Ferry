# AGENTS.md

This file provides guidance to Codex and other coding agents working in this repository.

## ビルド・テストコマンド

```bash
# デバッグビルド（単体プロジェクト / ソリューション全体 Ferry.slnx）
dotnet build src/Ferry/Ferry.csproj
dotnet build Ferry.slnx          # アプリ + テストを一括ビルド

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

# relay Worker（シグナリング / リレー / Bridge ページ）の型チェック + テスト
cd infra/cloudflare/relay && pnpm exec tsc --noEmit && pnpm test

# relay Worker のテスト単体実行（ファイル指定 / テスト名フィルタ）
cd infra/cloudflare/relay && pnpm vitest run tests/signaling-ratelimit.test.ts
cd infra/cloudflare/relay && pnpm vitest run -t "rate limit"

# relay Worker の手動デプロイ（通常は main push で deploy-relay.yml が自動配信するので不要）
cd infra/cloudflare/relay && pnpm dlx wrangler deploy
```

> Windows 向けリリースは `pwsh scripts/release-local.ps1` でローカル実行する（コード署名のため）。macOS / Linux は `release/**` ブランチへの push で CI が配信する（後述「自動更新と配信」）。Bridge ページ（QR ペアリング）は relay Worker の Static Assets（`infra/cloudflare/relay/public/`）なので relay と一緒に配信される。
>
> PR（→ main）は `.github/workflows/dotnet-build.yml`（".NET Build"）が build + test で検証する。`release/**` トリガーの配信 CI（後述）とは別ワークフローなので、コード変更の正否はこの PR CI で確認する。
>
> **relay（`infra/cloudflare/relay/**`）の PR は別ワークフロー `relay-check.yml`（"Relay Check"）**が上記の `tsc --noEmit` + `pnpm test` をそのままゲートにする。`dotnet-build.yml` は .NET しか見ず relay を素通りするので、relay 変更の正否はこちらで確認する。⚠️ 旧構成では relay の TypeScript を検証する経路がどの workflow にも無く、**vitest スイートが一度も走らないまま main にマージされて本番へ配信されうる**状態だった（rere #C-05）。同じステップは `deploy-relay.yml`（main push）にも置いてあるので、PR を経由しない直 push でも検証は外れない。

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

## サーバー接続情報

- **relay Worker（シグナリング / プレゼンス / ペアリング / リレー / Bridge ページ）**: Cloudflare Workers + Durable Objects + D1（`https://watashiba.kagayoi.com`）。実装・デプロイ手順は [`infra/cloudflare/relay/README.md`](infra/cloudflare/relay/README.md) を参照。使用量は Cloudflare GraphQL Analytics（`workersInvocationsAdaptive` / zone の `httpRequestsAdaptiveGroups`）で確認できる
- **STUN**: Cloudflare 公開 STUN (`stun.cloudflare.com:3478`) を主、Google STUN (`stun.l.google.com:19302`) を従。自前運用は無し
- **Firebase**: **完全撤去済み（2026-07）**。RTDB は deny-all・Hosting は無効化・GitHub/CF Worker の Firebase 系 Secrets も削除済みで、プロジェクト `ferry-edf09` はシャットダウン済み（2026-08-01 完全削除予定）。移行設計は [`docs/design/cf-only-migration.md`](docs/design/cf-only-migration.md)

旧 VPS (`C:\Users\IMT\dev\1llum1n4t1.net` リポジトリ管理) の `ferry-relay` (Node.js) と `coturn` コンテナは 2026-05 の Cloudflare 移行で役目を終えた（撤去手順は [`docs/Cloudflare移行_作業依頼書_2026-05.md`](docs/Cloudflare移行_作業依頼書_2026-05.md)）。

## 既知の制限と注意事項

0. **確立途中の接続は送信操作で奪わない**: `ConnectToPeerAsync` は同じ peer が `PeerState.Connecting` なら、まず完走を待って**相乗り**する（`InFlightConnectJoinMs`=30s）。待たずに `ConnectCts.Cancel()` すると、着信(answer)側がリレー合流待ちまで進んだ接続をユーザーの「送信」が破棄し、続く offerer 経路の**シグナリング削除で相手の offer まで消える**。相手は既にリレーで待機しているので誰も answer を返さず、20s 後に `PeerUnreachableException`（相手から応答がありません）で必ず失敗する（2026-07-28 実測。相手はオンラインで到達可能だった）。中断した場合も `Connecting` を抜けるまで待つ（`ConnectSettleWaitMs`=3s）— 待たないと `WaitForListenerConnectedAsync` が**死にかけの旧接続の Connecting** を委譲先 listener の進捗と誤認し、直後の `Disconnected` 遷移で 15s 待たず 200ms でフォールバックする。回帰は `ConnectionServiceInFlightJoinTests`。
1. **同時接続の競合**: rere #D-003 で offer を per-sender キー（PairDO `offer:{senderDeviceId}`）化したため、2台が同時に接続を試みても **offer の相互上書きは構造的に起きない**。さらに deviceId 序列の **deferral（`CompareOrdinal` で大きい側が answerer に委譲）** で「双方が offerer になり相互の answer を待ち続けるデッドロック」を収束させる。ただし deferral 判定の瞬間に相手がまだ offer を書いていない**同時ウィンドウ**は残る（完全収束は今後の課題）。基本は接続確立後にファイル送信するのが安全。
2. **Native AOT 制約**: JSON の動的シリアライズは使えないため、モデル追加時は `*JsonContext` も追加する。
3. **信頼モデル**: シグナリング認可は CF 単独完結の cfToken（自前 HMAC bearer + ECDSA デバイス署名チャレンジ + KV first-write-wins 鍵束縛。§Cloudflare バックエンド構造）。E2E 暗号は `ConnectionService.CreateSecureChannel` / `StartSecureHandshake` / `ApplySecureStep` に配線済みで **常時 ON**（v1.0.48 で旧トグル撤去）。QR ペアリング時に長期 ECDH 公開鍵を交換して PairSecret を導出し、HMAC 相互認証 + AES-GCM 封筒化する。**v1.0.65 で 2 台実機検証済み**（別回線 2 台でログ「暗号セッション確立（HMAC 相互認証成功）」+ 数百 MB 転送の SHA-256 検証を確認）。PairSecret を持たない旧ペア（公開鍵交換前の peers.json）は**平文フォールバック**のまま — 再ペアリングすると暗号化される。

> 設定（`settings.json` / `peers.json`）は一時ファイル→リネームでアトミックに保存し、読み込み失敗時は `.corrupt-<時刻>` に退避する。`DeviceId` は pairId / presence の基盤なので、破損で再生成されるとペアが消える点に注意。

## 言語

コード内コメント、コミットメッセージ、ユーザーへの応答はすべて **日本語** で行う。

## ドメイン移行（2026-07 開始・期限 2027/05/31）

屋号を **Kagayoi** に統一したため、配信ドメインを `nephilim.jp` から `kagayoi.com` へ移行中。方針の全体像はユーザーグローバルの `AGENTS.md` §屋号とドメイン を参照する。

- **旧ドメイン `nephilim.jp` はレジストラで廃止申請済みで 2027/05/31 に失効する**（延長しない）。それまでに出荷済みバイナリを新ドメインへ移行しきる。
- 旧ホストの Worker route / custom domain は**期限まで消さない**。消すと出荷済みアプリの自動更新が止まる。
- `nephilim.jp` の Redirect Rules は `/` だけを 301 する。`releases.*.json` / `*.nupkg` / `*-Setup.exe` は転送せず R2 が配信を続ける。
- 配信は `ferry.kagayoi.com`（R2 `ferry-updates`）、リレーは `watashiba.kagayoi.com`（渡し場）。旧 `ferry.nephilim.jp` / `relay.ferry.nephilim.jp` は wrangler の route に併記して残してある。`App.axaml.cs` の `UpdateBaseUrl` と relay の URL を書き換えるときは、旧ホストの route を消さないこと。
