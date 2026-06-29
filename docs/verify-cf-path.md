# CF 経路 dual-path 2 台実機検証手順（Step 3.5/4.5 ゲート）

Firebase 撤去（`docs/design/cf-only-migration.md`）の Step 5（既定切替）に進む前に必須のゲート。CF インフラ（D1 / Worker / secret / Bridge）はライブで整備済みなので、**残るは「2 台実機で CF 経路がフル疎通するか」と「cold start が Firebase と同等以下か」の実測**だけ。

## 前提

- CF インフラはデプロイ済み（`relay.ferry.nephilim.jp`）。クライアントの dual-path 配線も完成済み（`App.axaml.cs` の `UseCloudflareSignaling` 分岐）。
- このゲートは **本番（Firebase 既定）に無影響**。検証する 2 台だけ手元でフラグを立てて試す。終わったらフラグを戻せば元通り。

## 設定（両端の PC で `settings.json` を編集）

設定ファイル: `%APPDATA%\Ferry\settings.json`（Windows / Roaming）。アプリ終了中に編集。

```jsonc
{
  // …既存設定…
  "UseCloudflareSignaling": true   // CF 経路を有効化（既定 false = Firebase）
}
```

- **両端で `true`** にする（片側だけだと Firebase ↔ CF の経路不一致でペアリング/接続が成立しない）。
- 検証は **新しいペアリングからやり直す**のが確実（既存 peers.json のペアは Firebase 時代の pairs SSoT を見るため）。再ペアリングで CF 側 D1 に pairs が書かれる。

## 確認項目（ログ: `%LOCALAPPDATA%\Ferry\logs\Ferry_YYYYMMDD.log`）

### 1. 認証（CfTokenProvider）
- 起動時に `signaling 経路: Cloudflare (CF 単独完結)` が出る（Firebase ではない）
- `/auth/token` で cfToken 取得成功（401/403 が出ないこと）

### 2. ペアリング（D1 + inbox WS）
- スマホで QR → Bridge（CF 版 `relay.ferry.nephilim.jp/`）でスキャン
- 両 PC に**ペア成立が出る**（DeviceDO inbox WS push）
- 再ペアリング後、`pairs/{pairId}` が D1 に書かれる

### 3. presence（DeviceDO poll）
- 相手がオンライン表示になる（lastSeen ハートビート）
- 相手アプリを閉じると一定時間で offline 表示

### 4. 接続確立（PairDO signaling）
- ファイル送信 → `接続完了！ 経路:` が出る（Direct / StunAssisted / Relay いずれか）
- offer/answer/endpoint 交換が CF `/sig/{pairId}/*` 経由で成立

### 5. E2E 暗号（PairSecret）
- `暗号セッション確立（HMAC 相互認証成功）` が出る
  - ⚠️ 出ない場合は PairSecret 未交換（平文フォールバック）。CLAUDE.md 既知制限どおり、ECDH 公開鍵交換の実機確認が未済なら別途要対応

### 6. unpair 伝播
- 片方でペア削除 → 相手側でもペアが消える（`DeletePairAsync` + inbox unpair push）

### 7. 継続性
- 1 時間以上接続維持して切断しないこと（cfToken の 50min refresh が効くか）

## cold start 実測（東京 pin）

DO は pairId/deviceId 単位で「毎回 cold」なので、初回応答が Firebase より遅くないかを測る。

- **QR スキャン → PC にペア出現** までの時間
- **接続開始 → 確立** までの時間

それぞれ数回測って p50/p95 を出し、Firebase 経路（`UseCloudflareSignaling=false`）の同条件と比較。**CF が同等以下なら合格**。

## 判定

| 結果 | 次のアクション |
|---|---|
| 全 7 項目 OK + cold start 同等以下 | **ゲート通過** → Step 5（既定 `UseCloudflareSignaling=true` 反転）へ。ただし dual-write 整合（Firebase pairs → D1 一括移植）を済ませてから |
| 一部 NG | NG 項目を記録 → 該当の CF 実装（CloudflareSignaling / InboxClient / Worker ルート）を修正してから再検証 |

## 撤退

`UseCloudflareSignaling` を `false`（既定）に戻すだけで Firebase 経路に完全復帰。検証は本番に無影響なので、いつでも中断・やり直し可能。
