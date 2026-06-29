# マルチストリーム転送 PoC 計測手順

Relay 経路で 1 ファイルを複数 WebSocket 接続に分散送信すると実効スループットが上がるか（=単一 WS の送信直列化＋単一 Cloudflare DO 中継が律速か）を実測で判定するための手順。

## 仕組み（要点）

- `RelayStreamCount=N`（既定 1）で、Relay 接続時に sub-pairId `pairId#s0 / #s1 / …` を使った **N 本の独立 WS** を張り、`MultiStreamRelayTransport` で 1 論理 transport に束ねる。
- 各 `#s{i}` は relay 側で **別 Durable Object ルーム（別 2-peer room）** になるため、relay Worker は**無改修**（1 ルーム N 接続ではなく N ルーム×2 接続に分割）。
- 送信は **FileChunk(0x02) のみ round-robin 分散**。FileMeta/FileApprove/FileHash/FileFlowAck 等の制御フレームは `stream[0]` 固定（順序事故防止）。
- 受信側は `chunkIndex × ChunkSize` の Seek 書き込み + ビットマップで順不同到着を既に許容済みなので**無改修**（per-state lock のみ追加して並行 Write を安全化）。
- フロー制御は全 stream 合算で 32MB 窓を維持 → 各 DO に積まれる未ドレイン分は最悪 ~32MB/N（N=4 で DO あたり ~8MB、128MB に対し十分マージン）。
- **暗号は触らない**: `MultiStreamRelayTransport` は SecureChannel の下（transport 層）。本 PoC は PairSecret 未交換の平文フォールバック経路で行う（暗号 ON 時の per-stream nonce 名前空間問題を回避）。

## 設定（両端の PC で `settings.json` を直接編集）

設定ファイル: `%APPDATA%\Ferry\settings.json`（Windows / Roaming）。アプリ終了中に編集する。

```jsonc
{
  // …既存設定…
  "ForceRelay": true,       // TCP/UDP を skip して必ず Relay 経由（同一 LAN でも Relay 強制で再現性確保）
  "RelayStreamCount": 4     // 1=単一WS（baseline） / 4=4本マルチストリーム（after）
}
```

- **両端で `ForceRelay: true`** にする（片側だけだと TCP/UDP が成立して Relay に落ちない）。
- `RelayStreamCount` は **両端で同じ値**にする（sub-pairId 派生規則と本数が両端一致である必要がある）。
- UI からは設定しない（PoC 専用フラグ）。計測が終わったら両方消す（または `ForceRelay:false` / `RelayStreamCount:1`）だけで撤退完了。

## 計測手順

1. 対象ファイル: **512MB**（窓 32MB を確実に超え、フロー制御が発火する域）。可能なら 256MB / 512MB / 1GB の 3 点。
2. **baseline**: 両端 `ForceRelay:true` + `RelayStreamCount:1` で同じ 512MB を 3 回送り、中央値を baseline（MB/s）に。
3. **after**: 両端 `RelayStreamCount:4` で同条件 3 回、中央値を after に。
4. 比 `after / baseline` を算出。

### 計測点

- 送信側 UI の転送レート（`RateText`、累積平均 bps）。
- 送信完了までの wall-clock（ログの `ファイル送信開始` 〜 `ファイル送信完了` の timestamp 差）。
- ログ（`%LOCALAPPDATA%\Ferry\logs\Ferry_YYYYMMDD.log`）で確認:
  - `接続完了！ 経路: Relay`（Relay 経由になっているか）
  - `マルチストリームリレー 4 本確立完了`（N 本張れたか）
  - `フロー制御 window 発火（受信ドレイン律速に移行）`（送信が受信ドレインに律速されたか）

## 判定

| `after / baseline` | 結論 |
|---|---|
| **≧ 1.3 倍** | 単一 WS の `_sendLock` 直列化／単一 DO 中継が律速 → マルチストリームに効果あり。TCP/UDP への横展開を検討 |
| **~ 1.0 倍** | 律速は受信側ドレイン or ローカル帯域 → WS 多重化は無効。撤退（`RelayStreamCount=1`） |

> ⚠️ 同一 LAN + ForceRelay 計測はローカル帯域が広く N 効果が**過小評価**されやすい。理想は別回線 2 台。LAN で 1.0 倍でも、別 NAT 実回線で 1 回測って効果の有無を確認するのが望ましい。

## 撤退

`RelayStreamCount=1`（既定）で `MultiStreamRelayTransport` は生成されず、単一 `WebSocketRelayTransport` に完全後方互換で戻る。速度向上が確認できなければフラグを既定のままにするか、`MultiStreamRelayTransport.cs` を削除するだけで撤退できる（`ConnectionInfo` 拡張なし＝AOT / signaling 互換への波及なし）。
