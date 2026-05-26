# Ferry WebSocket リレー (Cloudflare Workers + Durable Objects)

旧 VPS Node.js リレー (`wss://1llum1n4t1.net/ferry-relay`) の置き換え。

- **エンドポイント**: `wss://relay.ferry.nephilim.jp/ferry-relay?pairId=<id>&role=<offer|answer>`
- **ランタイム**: Cloudflare Workers + Durable Objects (Hibernation API)
- **コスト**: Workers Paid $5/月 (RealTimeTranslator と共有)。Ferry の DO duration は無料枠で収まる試算

クライアント側は [`src/Ferry/Infrastructure/WebSocketRelayTransport.cs`](../../../src/Ferry/Infrastructure/WebSocketRelayTransport.cs) を参照。プロトコル変更時はクライアントと両方更新すること。

## 初回セットアップ (ゆろさん側)

1. **Workers Paid プラン契約** (RealTimeTranslator 用契約と共用)

2. **wrangler CLI を入れる**

   ```powershell
   npm install -g wrangler
   wrangler login
   # ブラウザで Cloudflare アカウントを選択して認可
   ```

3. **依存をインストール** (初回のみ)

   ```powershell
   cd infra/cloudflare/relay
   npm install
   ```

4. **pairId ハッシュ用 SALT を登録**

   ```powershell
   wrangler secret put SALT
   # 入力プロンプトに長めの任意文字列 (32文字以上推奨) を貼り付ける。
   # 一度設定すれば以降のデプロイでも保持される。
   ```

5. **デプロイ**

   ```powershell
   wrangler deploy
   ```

   初回は `relay.ferry.nephilim.jp` の Custom Domain 紐付けが Cloudflare Dashboard 側でも必要な場合がある。
   - Dashboard → Workers & Pages → ferry-relay → Settings → Triggers → Custom Domains → `relay.ferry.nephilim.jp` を Add
   - DNS は Cloudflare が自動で `nephilim.jp` Zone に AAAA / CNAME を貼る

6. **疎通確認**

   ```powershell
   curl https://relay.ferry.nephilim.jp/health
   # → OK が返れば疎通完了
   ```

## 開発

```powershell
# ローカル実行 (DO は memory mode、本番と微妙に挙動が違う点に注意)
wrangler dev

# ログをリアルタイム監視 (デプロイ後)
wrangler tail
```

`wrangler dev` の DO はメモリモードで本物の Durable Object とは挙動が違うため、Hibernation 周りの確認は必ず一度デプロイして確認すること。

## ロールバック

- **Workers 緊急停止**: Dashboard → Workers & Pages → ferry-relay → ⋯ → Disable
- **コードロールバック**: `wrangler rollback` (直前のデプロイに戻す) または `git revert` してから `wrangler deploy`

## プロトコル仕様

リレーは pairId ごとに 1 つの Durable Object インスタンスを割り当て、その内部で 2 つの WebSocket peer を相互に中継する。

```
Client A ──┐
           ├── RelayDO (pairId hash) ── 相互パススルー
Client B ──┘
```

| 項目 | 値 |
|---|---|
| 接続パス | `/ferry-relay` (`/health` はヘルスチェック) |
| 必須クエリ | `pairId=<id>` `role=<offer\|answer>` |
| DO ID 計算 | `SHA-256(pairId + "\|" + SALT)` の hex |
| 接続可能 peer 数 | 1 ペアあたり 2 peer (3 人目は 409 Conflict) |
| 接続成立通知 | DO が両 peer に `"ready"` テキストフレームを送る |
| メッセージリレー | バイナリフレームのみ。テキストは握りつぶす (誤送信ガード) |
| 最大メッセージサイズ | 1 MB (Workers WebSocket 仕様)。Ferry は 64 KB チャンクなので余裕 |
| 切断時 | 片側が切れたら相手側を `1001 Peer disconnected` で close |

## なぜハッシュ化が必要か

`idFromName(pairId)` に生 pairId を渡すと、Firebase ログや CDP 等で pairId が漏れた瞬間に任意の第三者が同じ DO インスタンス (= 同じルーム) に到達できる。SALT 付き SHA-256 でハッシュ化することで、Worker 側コードを読めない攻撃者は同じ DO ID を計算できない。SALT は Workers Secrets で管理し、ソースコードには出さない。
