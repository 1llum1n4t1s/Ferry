# Firebase Custom Token Auth + ペア削除 SSoT 設計（v2 改訂版）

**対象バージョン**: v1.0.62
**関連**: rere #D-001a（保留解除）/ Codex P2 #3318454466 / 本 PR で Phase B 完成
**ステータス**: 着手前合意済（6観点×敵対的検証で 19件 designUpdates と 10件 open questions 反映済・全 GO）
**改訂履歴**: v1 (2026-06-19) 初版 / v2 (2026-06-19) Workflow 検証反映・Q1-Q4 判断反映

---

## 1. 動機とスコープ

### 1.1 動機
1. **ペア削除の片側残骸問題**: 現状 `RemovePeerAsync` はローカル `peers.json` から消すだけで相手に伝わらない（[`PeerRegistryService.cs:61`](../../src/Ferry/Services/PeerRegistryService.cs#L61)）
2. **#D-001a Firebase Custom Token Auth**: 厳格 rules は draft 済みだが Anonymous Auth 実装が無いため deploy できない（[`database.rules.json:6`](../../src/Ferry.Bridge/database.rules.json#L6) の DO_NOT_DEPLOY フラグ）
3. **Codex P2 #3318454466**: `pairings/` parent collection read 構造的問題が残存
4. **Ghost peer 強制注入の構造的脆弱性**: anonymous Bridge が任意 victim deviceId 配下の pairings/ に書ける（v2 改訂で本 PR で完全排除する判断）

これらを「Firebase Custom Token Auth フルセット」で同時解決する。

### 1.2 スコープ（v2 改訂で拡大）
- ✅ Workers `/auth/token` エンドポイント（PC 用 Custom Token・長期 ECDSA 署名チャレンジ）
- ✅ **Workers `/pair/token` エンドポイント**（Bridge 用 short-lived Custom Token・sessions/{sid}/PairingNonce 紐付け）⭐ v2 追加
- ✅ クライアント `FirebaseAuthClient`（Custom Token 取得→Firebase Auth ログイン・idToken refresh + AsObservable 再購読）
- ✅ `FirebaseSignaling` の全リクエストに `?auth=<idToken>` 自動注入
- ✅ `pairings/$deviceId/$pid` per-device path 化（PC と Bridge の atomic mirror write）
- ✅ 新ノード `pairs/{pairId}` SSoT（auth 当事者のみ R/W）
- ✅ `PairSyncService`（ハイブリッド: 起動時即 + 5min + 1h ポーリング + robustness 強化）⭐ v2 強化
- ✅ `RemovePeerAsync` 拡張（Firebase 側も DELETE + PendingPairDeletes キュー）
- ✅ `pairs/{pairId}` 書込 fallback（deviceId 大きい方が 30s 後に確認・冗長化）⭐ v2 追加
- ✅ `database.rules.json` 厳格化（pairings は auth.uid==$deviceId 強制で Ghost peer 完全排除）
- ✅ Bridge を Custom Token Auth に置換（signInAnonymously 撤去）⭐ v2 追加
- ✅ identity.key 紛失 clean slate UI（401 DEVICE_PUBKEY_MISMATCH 検出 → DeviceId 再生成）⭐ v2 追加
- ✅ `presence/{deviceId}` に Version フィールド追加（Step 8 直前の v1.0.62 機械検証用）⭐ v2 追加
- ✅ `firebase-cleanup.yml` を SA 認証化 + per-device 構造対応 + サイレント失敗修正 ⭐ v2 追加
- ✅ Emulator + Workers ユニットテスト整備 ⭐ v2 追加

### 1.3 非スコープ
- ❌ #D-001b 暗号配線（既に Phase 1-3 完了、別軌道）
- ❌ #D-005 段階2（差分レジューム）
- ❌ Workers `/auth/rotate` API（identity 紛失リカバリーはクライアント clean slate で対応・Q2 判断）

---

## 2. 設計判断（Q1-Q4 反映後）

| 判断点 | 採用案 | 理由 / 反映先 |
|---|---|---|
| Workers エンドポイント配置 | 既存 relay Worker（`infra/cloudflare/relay/`）に同居 | wrangler.toml 1 つ・KV 共有・デプロイ単位最小化 |
| Custom Token 発行責務 | Workers 単独（クライアントは SA 鍵を持たない） | SA 鍵流出リスク最小化 |
| deviceId なりすまし対策 | `DeviceIdentity` に新規 `Sign()` メソッドを追加し ECDSA P-256 で署名 | 既存 ECDH 鍵パラメータを ECDSA に再エクスポート。**IEEE P1363 raw 64 byte 形式**で .NET と Web Crypto を揃える（DER は不採用）。NIST 推奨外の同一鍵 ECDH/ECDSA 流用リスクは既知制限 |
| deviceId 形式 | `Guid.NewGuid().ToString("N")` の **32 文字 hex** | AppSettings.cs:13 の既存実装。rules でも `auth.uid.length == 32` で format validate |
| deviceId↔publicKey binding | Workers KV に first-write-wins で永続 | 既存 KV 利用・低コスト |
| **Bridge の Auth (Q1)** | **Custom Token 化（短期トークン）** | `/pair/token` で sessions/{sid}/PairingNonce + QR スキャン時刻に紐付けた **5min 期限の Custom Token** を発行。anonymous 完全排除で Ghost peer 構造的に不可能に |
| `pairings` 構造 | `pairings/$deviceId/$pid` per-device + **atomic multi-path update** | Bridge と PC 両方が `db.ref().update({ "pairings/$dA/$pid": data, "pairings/$dB/$pid": data })` で同時書込。片側成功・片側失敗を排除 |
| `pairs/{pairId}` rules | `auth != null && auth.uid.length == 32 && $pairId.matches(/^[a-f0-9]{32}_[a-f0-9]{32}$/) && ($pairId.beginsWith(auth.uid + '_') \|\| $pairId.endsWith('_' + auth.uid))` | `contains()` は短 uid で false-match する穴。format validate と begins/ends で厳密化 |
| **PairSync 周期 (Q3)** | **ハイブリッド: 起動時即 + 5min + 1h** | 起動直後と 5min は即時性、以降は ETag 304 で帯域節約 |
| **書込責務 (Q4)** | deviceId Ordinal 小さい方が責任者 + **大きい方が 30s 後 fallback** | クラッシュ耐性・冗長化 |
| **identity 紛失リカバリー (Q2)** | **クライアント clean slate UI** | Workers `/auth/token` の 401 DEVICE_PUBKEY_MISMATCH 検出 → モーダル承認で DeviceId 再生成 + peers.json reset |
| rules deploy タイミング | クライアント v1.0.62 配信 + presence Version 機械確認後に手動 deploy | 不可逆 deploy を実機検証後に倒す |

---

## 3. Firebase ノード構造（after）

```
ferry-edf09 (Realtime DB)
│
├── sessions/{sid}                            # 既存・QR セッション一時データ
│     ├── DisplayName, CreatedAt, PublicKey, PairingNonce ⭐ 追加
│     └── 書: auth.uid == $sid && CreatedAt が server now ±60s（厳格化）
│
├── pairings/{deviceId}/{pid}                 # ⭐ restructure: per-device inbox
│     ├── SidA, SidB, NameA, NameB, PkA, PkB, CreatedAt
│     ├── 読: auth != null && auth.uid == $deviceId
│     └── 書: auth != null && auth.uid == $deviceId
│            ← PC は Custom Token (PC 用)、Bridge は Custom Token (short-lived) で両方とも書込時に auth.uid 強制
│            ← atomic multi-path update で両 deviceId 配下に同時書込
│
├── pairs/{pairId}                            # ⭐ 新規・SSoT・永続
│     ├── PairId, NameA, NameB, CreatedAt
│     ├── 読書: auth != null && auth.uid.length == 32 && $pairId.matches(/^[a-f0-9]{32}_[a-f0-9]{32}$/)
│     │         && ($pairId.beginsWith(auth.uid+'_') || $pairId.endsWith('_'+auth.uid))
│     └── PC 側がペア成立時に責任者書込 + fallback 書込。cleanup 対象外
│
├── signaling/{pairId}/...                    # 既存
│     ├── 読書: 同上の pairs と同じ厳格化
│     └── offers/{senderDeviceId}, answers/{answererDeviceId}, endpoints/{senderDeviceId} は
│            `.write` に `auth.uid == $senderDeviceId/$answererDeviceId` を追加して sender なりすまし防止
│
└── presence/{deviceId}                       # 既存 + Version 追加
      ├── 書: auth.uid == $deviceId
      ├── LastSeen, DisplayName, Version ⭐ 追加（"1.0.62" 等）
      └── Step 8 直前に両 PC が v1.0.62 を書き込んでいることを機械確認
```

---

## 3a. 信頼境界と脅威モデル（新規セクション）

### 3a.1 信頼境界
- **PC クライアント**: `identity.key` を持ち、ECDSA 署名で deviceId 所有権を証明できる。Workers `/auth/token` 経由で長期 Custom Token を取得
- **Bridge（スマホ Web）**: 物理的に scanQR 操作を伴うことが信頼の源。Workers `/pair/token` 経由で **sessions/{sid}/PairingNonce + ペア成立直前 5min** だけ有効な short-lived Custom Token を取得
- **Firebase Realtime DB**: rules で `auth.uid` ベースの厳格な書込権限を強制
- **Cloudflare Workers + KV**: Firebase SA 鍵を保持し、deviceId↔pubKey の binding を first-write-wins で管理

### 3a.2 排除した脅威（v2 で完全閉鎖）
- ✅ **Ghost peer 強制注入**: Bridge も Custom Token 化したことで `auth.uid == $deviceId` 強制 rule が機能し、任意の anonymous 攻撃者は victim deviceId 配下に書込不能
- ✅ **deviceId 主張のなりすまし**: ECDSA 署名チャレンジ + KV first-write-wins binding
- ✅ **pairs/{pairId} の他人領域書込**: rules の `$pairId.matches(...)` + `beginsWith/endsWith(auth.uid)` で機械的に当事者限定
- ✅ **signaling per-sender なりすまし**: rules `.write` で `auth.uid == $senderDeviceId` 強制（#D-003 の per-sender 化を rules 層で完成）

### 3a.3 残留リスク（受容）
- 🟡 **presence/{deviceId} の `.read: auth != null`**: 任意の Custom Token 保有者が任意 deviceId の online 状態を観測可能。これは ペア相手の online 検知の正当な用途と区別できないため受容
- 🟡 **probeOffers/{nonce} / probeAnswers/{nonce}**: nonce が deviceId と無関係なので payload.From を rules で検証できない。暗号配線 #D-001b 完了後に payload 検証で補う方針
- 🟡 **NIST 推奨外**: 同一 P-256 鍵を ECDH と ECDSA で兼用。実装簡素化のため受容（識別と鍵交換の同時用途・既存資産流用）

---

## 4. Workers エンドポイント spec

### 4.1 `/auth/token`（PC 用・長期）

#### リクエスト
```http
POST https://watashiba.kagayoi.com/auth/token
Content-Type: application/json

{
  "deviceId": "2222e9a8...",           // Guid.NewGuid().ToString("N") の 32 hex
  "pubKeySpki": "MFkwEwYHKoZIzj0...",  // base64url ECDSA P-256 SPKI（既存 DeviceIdentity の SubjectPublicKeyInfo）
  "ts": 1781878456000,                 // unix ms
  "sig": "..."                         // base64url ECDSA P-256 SHA-256 IEEE P1363 raw 64byte
}
```

`sig` = `ECDSA-SHA256(privKey, UTF8("ferry-auth-v1|" + deviceId + "|" + pubKeySpki + "|" + ts))` を **IEEE P1363 raw** 形式で出力。

#### Workers 検証
1. `deviceId.matches(/^[a-f0-9]{32}$/)` でない → 400 INVALID_DEVICE_ID
2. `Math.abs(ts - Date.now()) < 60_000` でない → 400 CLOCK_SKEW（応答に `serverTime` 含めてクライアントの時計同期 UI に使う）
3. `crypto.subtle.importKey('spki', base64urlDecode(pubKeySpki), {name:'ECDSA', namedCurve:'P-256'}, false, ['verify'])`
4. `crypto.subtle.verify({name:'ECDSA', hash:'SHA-256'}, key, base64urlDecode(sig), encoder.encode(data))` で raw 64byte 署名検証
5. KV `device-pubkey:{deviceId}` 取得
   - 未登録 → first-write-wins で `pubKeySpki` を保存
   - 登録済み一致 → OK
   - 登録済み不一致 → 401 DEVICE_PUBKEY_MISMATCH（クライアントは clean slate UI 発動）
6. Firebase Custom Token JWT を **SA 鍵で RS256 署名** して返す
   - claims: `iss = <SA client_email>, sub = <SA client_email>, aud = "https://identitytoolkit.googleapis.com/google.identity.identitytoolkit.v1.IdentityToolkit", iat, exp = iat+3600, uid = deviceId`
   - レスポンス: `{"customToken": "<JWT>", "expiresIn": 3600}`

#### JWT 署名実装方針
- `firebase-admin` は Cloudflare Workers で動かない（Node.js 専用）→ 不採用
- `crypto.subtle.importKey('pkcs8', pemToDer(sa.private_key), {name:'RSASSA-PKCS1-v1_5', hash:'SHA-256'}, false, ['sign'])` で手書き JWT 構築（`header.payload.signature` の base64url 連結）
- 軽量化のため hono/jwt のような外部 lib は不採用

### 4.2 `/pair/token`（Bridge 用・短期）⭐ v2 追加

#### リクエスト
```http
POST https://watashiba.kagayoi.com/pair/token
Content-Type: application/json

{
  "sessionId": "abc123...",            // QR コード経由でスマホが取得した 32hex
  "pairingNonce": "xyz789..."          // QR コードに埋め込まれた 32hex nonce
}
```

#### Workers 検証
1. `sessionId.matches(/^[a-f0-9]{32}$/)` でない → 400
2. Firebase Realtime DB の `sessions/{sessionId}` を SA トークンで GET
3. `sessions/{sessionId}/PairingNonce` が `pairingNonce` と一致しない → 401 INVALID_NONCE
4. `sessions/{sessionId}/CreatedAt` が 1h 超 → 401 EXPIRED_SESSION
5. Custom Token JWT を発行（claims: `uid = sessionId, exp = iat+300`（5min・短期）)
6. レスポンス: `{"customToken": "<JWT>", "expiresIn": 300}`

### 4.3 レート制限
- **/auth/token**: IP 単位 60req/min・deviceId 単位 30req/min（緩和・10→30）
- **/pair/token**: IP 単位 60req/min・sessionId 単位 5req/min
- 429 受信時のクライアント側バックオフ上限は **300s** に拡張（jitter ±25%）
- Cloudflare Workers の標準 ratelimit binding（`RATELIMIT_IP` / `RATELIMIT_DEVICE` / `RATELIMIT_SESSION`）を使用

### 4.4 配置と wrangler 設定
既存 [`infra/cloudflare/relay/src/index.ts`](../../infra/cloudflare/relay/) に `/auth/token` と `/pair/token` ルートを追加。`wrangler.toml` に追加するブロック:

```toml
[[kv_namespaces]]
binding = "DEVICE_KEY_BINDING"
id = "<wrangler kv namespace create で取得>"

[[unsafe.bindings]]
name = "RATELIMIT_IP"
type = "ratelimit"
namespace_id = "1001"
simple = { limit = 60, period = 60 }

[[unsafe.bindings]]
name = "RATELIMIT_DEVICE"
type = "ratelimit"
namespace_id = "1002"
simple = { limit = 30, period = 60 }

[[unsafe.bindings]]
name = "RATELIMIT_SESSION"
type = "ratelimit"
namespace_id = "1003"
simple = { limit = 5, period = 60 }
```

#### Secret 投入
SA 鍵は ~2KB なので 1KB 推奨上限を超えうる。**`FIREBASE_PRIVATE_KEY`（PEM）と `FIREBASE_CLIENT_EMAIL` の 2 secret に分割**して投入する:

```bash
# secrets.json の sa_key_path 経由で
SA_PATH=$(node -p "require('C:/Users/IMT/dev/Secret/secrets.json').firebase.sa_key_path")
node -p "require('$SA_PATH').private_key" | pnpm dlx wrangler secret put FIREBASE_PRIVATE_KEY
node -p "require('$SA_PATH').client_email" | pnpm dlx wrangler secret put FIREBASE_CLIENT_EMAIL
```

---

## 5. クライアント認証フロー（C# 側）

### 5.1 起動シーケンス
```
[App.axaml.cs 起動時]
  ↓
  DeviceIdentity.LoadOrCreate()               # 既存（%APPDATA%\Ferry\identity.key）
  ↓
  FirebaseAuthClient.SignInAsync()            # 新規
    ├─ POST /auth/token (sig付き)            # Workers
    ├─ ← (status, body)
    │
    ├─ 401 DEVICE_PUBKEY_MISMATCH
    │     → IdentityLostEvent 発火 → MainWindow が clean slate モーダル表示
    │
    ├─ 200 OK
    │     ← {customToken, expiresIn}
    │     ↓
    │     Firebase REST: identitytoolkit signInWithCustomToken
    │     ← {idToken, refreshToken, expiresIn}
    │     ↓
    │     idToken をメモリ保持（refreshToken は永続化しない・毎起動 SignIn）
    │
    └─ 429 / 5xx → 指数バックオフ + jitter（1s, 2s, 4s, ..., 300s 上限）
  ↓
  FirebaseSignaling 全リクエストに ?auth=<idToken> を付与
  ↓
  バックグラウンドで 50min 経過時に再 SignIn → 新 idToken → IdTokenRefreshed イベント発火
  ↓
  StartWatchingPairing 等の AsObservable 購読は IdTokenRefreshed で Dispose → 再 Subscribe
```

### 5.2 AOT セーフ実装方針
- `System.IdentityModel.Tokens.Jwt` は AOT 非対応 → **NuGet 追加しない**
- JWT payload decode は不要（uid は自分の deviceId として自明、expiresIn はレスポンスから取得）
- 戻り値型: `(string idToken, string refreshToken, int expiresIn)` の素朴な ValueTuple
- 既存 `System.Net.Http` + `System.Text.Json`（Source Generator）で完結

### 5.3 identity.key 紛失リカバリー UI（Q2 採用案）⭐ v2 追加

#### 発火条件
`FirebaseAuthClient.SignInAsync()` で `/auth/token` から `401 DEVICE_PUBKEY_MISMATCH` を受信。

#### UI フロー
```
1. MainWindow に「identity 鍵紛失」モーダル表示
   タイトル: 「端末識別の鍵が壊れています」
   本文: 「OS 再インストール / ディスクエラー / Velopack 更新失敗で identity.key が
         紛失した可能性があります。新しい鍵を生成してペアリングをやり直しますか？
         （現在のペア一覧は全消去されます）」
   ボタン: [やり直す] / [後で]

2. [やり直す] 押下時:
   - DeviceIdentity.RegenerateAndSaveAsync() で新規鍵生成
   - settings.json の DeviceId を新規 Guid.NewGuid().ToString("N") に上書き
   - peers.json を空配列で reset（PairSecret も含めて全消去）
   - FirebaseAuthClient.SignInAsync() を再実行
   - 新 deviceId で KV first-write-wins binding 成立 → 新規ペアリングから開始

3. [後で] 押下時:
   - アプリはオフラインモードで起動（peers.json は読めるが Firebase 不通）
   - 各機能で Firebase エラーが出るのでユーザーが認識して対応
```

#### README 警告（必須）
README の「設定ファイル」セクションに以下を追加:

> ⚠️ **`identity.key` を絶対に削除しないでください**。`%APPDATA%\Ferry\identity.key`（macOS: `~/.config/Ferry`、Linux: 同左）は端末識別の長期秘密鍵で、紛失すると Firebase での認証ができなくなります。万が一紛失した場合、アプリ起動時に「端末識別の鍵が壊れています」モーダルが出ますので「やり直す」を選んでペアリングをやり直してください（既存ペアは全消去されます）。

### 5.4 失敗時の挙動
- Workers 接続失敗 → 指数バックオフ + jitter（1s, 2s, 4s, ..., 300s 上限）
- Workers 429 → バックオフ + jitter で再試行（リトライ集中防止）
- Workers 400 CLOCK_SKEW → モーダル「端末の時計を同期してください」表示
- Firebase REST 5xx → 同様にバックオフ
- 全失敗 → アプリは UDP ホールパンチ転送そのものは続行可能（peers.json + signaling 経路のみ Firebase 依存）

### 5.5 FirebaseSignaling 配線詳細
- [`FirebaseSignaling.cs:55`](../../src/Ferry/Infrastructure/FirebaseSignaling.cs#L55) の `new FirebaseClient(databaseUrl)` を以下に変更:
  ```csharp
  new FirebaseClient(databaseUrl, new FirebaseOptions {
    AuthTokenAsyncFactory = () => _authClient.GetIdTokenAsync(),
    AsAccessToken = false
  })
  ```
  これで `Child().PutAsync/OnceSingleAsync/AsObservable` 全経路に `?auth=<idToken>` 自動付与
- **`GetPresenceLastSeenAsync`（line 782-）は独自 HttpClient 経路**なので、URL に `?auth={Uri.EscapeDataString(idToken)}` を別途付与する手当が必要
- **`AsObservable`（SSE long-stream）は接続時の idToken を使い続け 1h で expire**→ permission_denied で切断するため、`FirebaseAuthClient.IdTokenRefreshed` イベントで購読を **50min ごとに Dispose → 再 Subscribe**

---

## 6. `pairs/{pairId}` ライフサイクル

### 6.1 書込（ペア成立時）⭐ v2: fallback 含む

#### 書込責務分担
- `pairId` の導出: `ConnectionService.GeneratePairId(devA, devB)`（既存・[`ConnectionService.cs:1680-1685`](../../src/Ferry/Services/ConnectionService.cs#L1680)）。`string.Compare(a, b, StringComparison.Ordinal)` で小さい方 + "_" + 大きい方
- **責任者**: `string.Compare(myDeviceId, peerDeviceId, StringComparison.Ordinal) < 0` の PC が `pairs/{pairId}` を書込
- **fallback**: 責任者でない PC は PairingDetected の **30s 後**に `pairs/{pairId}` を GET し、未存在なら自分が書込（責任者のクラッシュ救済）
- **セルフチェック**: 責任者は書込直後に `pairs/{pairId}` を GET して書込成功を確認

#### 擬似コード
```csharp
// ConnectionViewModel.OnPairingDetected 内
var pairId = ConnectionService.GeneratePairId(_deviceId, peerId);
var isResponsible = string.Compare(_deviceId, peerId, StringComparison.Ordinal) < 0;
if (isResponsible)
{
    await _signaling.PutPairAsync(pairId, new PairRecord { ... });
    var check = await _signaling.GetPairAsync(pairId);
    if (check == null) Util.Logger.Log("pairs/{pairId} 書込セルフチェック失敗", LogLevel.Warning);
}
else
{
    // fallback: 30s 後にチェックして未存在なら自分が書く
    _ = Task.Delay(30_000).ContinueWith(async _ =>
    {
        var existing = await _signaling.GetPairAsync(pairId);
        if (existing == null)
        {
            Util.Logger.Log($"pairs/{pairId} 未作成・fallback で書込");
            await _signaling.PutPairAsync(pairId, new PairRecord { ... });
        }
    });
}
```

`peers.json` に新フィールドを追加せず、`pairId` は呼出側で都度導出（PairedPeer 構造変更なし→AOT SourceGen 更新不要）。

### 6.2 PairSyncService 読込（Q3=ハイブリッド + robustness 強化）

#### ポーリングスケジュール
```
起動時 (即時): 全ペアを fresh fetch（ETag 無視）
↓ 5min 後: 同上
↓ 以降 1h ごと: ETag 条件付き GET（304 はそのまま）
```

#### Robustness 仕様
```csharp
class PairSyncService
{
    // ペア毎の連続 404 カウンタ
    private Dictionary<string, int> _consecutive404 = new();
    private const int Consecutive404Threshold = 3;  // 3 回連続 404 で初削除候補
    private DateTime _appStartedAt = DateTime.UtcNow;
    private TimeSpan _gracePeriod = TimeSpan.FromMinutes(5);  // 起動直後 5min は削除判定 skip

    private async Task CheckOnce()
    {
        if (DateTime.UtcNow - _appStartedAt < _gracePeriod && !_isFirstFire) return;
        // ※ 起動時の即時チェックは _isFirstFire=true で例外的に実行（gracePeriod は 2 回目以降に効く）

        foreach (var peer in _peerRegistry.Peers)
        {
            var pairId = ConnectionService.GeneratePairId(_deviceId, peer.PeerId);
            var (status, body) = await _signaling.GetPairWithStatusAsync(pairId);

            if (status == HttpStatusCode.OK)
            {
                _consecutive404[peer.PeerId] = 0;  // リセット
            }
            else if (status == HttpStatusCode.NotFound && body == "null")
            {
                var count = _consecutive404.GetValueOrDefault(peer.PeerId, 0) + 1;
                _consecutive404[peer.PeerId] = count;
                if (count >= Consecutive404Threshold)
                {
                    Util.Logger.Log($"pairs/{pairId} が {count} 回連続 404 → ローカル削除");
                    await _peerRegistry.RemovePeerAsync(peer.PeerId);
                }
            }
            // 401/403/5xx/timeout/network error → 不明・未操作（_consecutive404 もリセットしない）
        }
    }
}
```

#### ETag 戦略
- 200 OK のみ ETag をキャッシュ
- 404 は ETag 外（毎回 fresh fetch）。`If-None-Match` を 404 リソースに送ると 304 で誤って「存在する」と判定するため

#### Visibility gate
`ConnectionViewModel.SetPresencePollingActive` と同パターン（前面時のみ稼働）を `PairSyncService.SetActive(bool)` で提供。MainWindow の可視性イベントから両方を駆動。

### 6.3 削除（ユーザーが「ペアリング解除」UI 操作）

```csharp
RemovePeerAsync(string peerId):
  var pairId = ConnectionService.GeneratePairId(_deviceId, peerId);  // ⭐ PairedPeer.PairId 参照ではない
  try {
      await _signaling.DeletePairAsync(pairId);  // Firebase DELETE
  } catch (Exception ex) {
      // オフライン中は永続キューに積んで起動時 retry
      _pendingPairDeletes.Add(new PendingPairDelete { PairId = pairId, RetryCount = 0, LastRetryAt = DateTime.UtcNow });
      await _peerRegistry.SavePendingDeletesAsync();
  }
  await _peerRegistry.RemovePeerAsync(peerId);  // ローカル
```

#### PendingPairDeletes キュー仕様
- `peers.json` のトップレベルに新フィールド `pendingDeletes: [{ pairId, lastRetryAt, retryCount }, ...]` を追加
- `PeerRegistryJsonContext`（AOT SourceGen）を拡張
- アプリ起動時に全件 iterate、`lastRetryAt + backoff(retryCount)` が now より小さければ retry（exponential: 1min, 5min, 30min, 2h, 12h）
- `retryCount >= 5` で打ち切り（Warning ログ + queue から remove）
- PairSyncService と同じ visibility gate を適用

### 6.4 1ヶ月オフラインケース
PC-A がオフライン中に PC-B が削除 → `pairs/{pairId}` 消滅 → PC-A が1ヶ月後起動 → 起動時 PairSyncService 即時 fetch + 5min 後 fetch で連続 404 を 3 回計上 → ローカル削除（gracePeriod 5min は 2 回目以降に効くが、初回 fetch のみ判定に組み入れることで誤検出も防ぎつつ即時性を維持）。

---

## 7. `database.rules.json` 厳格版

[`src/Ferry.Bridge/database.rules.json`](../../src/Ferry.Bridge/database.rules.json) を以下で更新。DO_NOT_DEPLOY コメントは削除。

```json
{
  "rules": {
    ".read": false,
    ".write": false,

    "sessions": {
      "$sid": {
        ".read": "auth != null",
        ".write": "auth != null && auth.uid == $sid && newData.child('CreatedAt').val() > now - 60000 && newData.child('CreatedAt').val() < now + 60000",
        "DisplayName": { ".validate": "newData.isString() && newData.val().length <= 64" },
        "CreatedAt": { ".validate": "newData.isNumber()" },
        "PublicKey": { ".validate": "newData.isString() && newData.val().length <= 256" },
        "PairingNonce": { ".validate": "newData.isString() && newData.val().matches(/^[a-f0-9]{32}$/)" },
        "$other": { ".validate": false }
      }
    },

    "pairings": {
      "$deviceId": {
        ".read": "auth != null && auth.uid == $deviceId",
        "$pid": {
          ".write": "auth != null && auth.uid == $deviceId",
          "SidA": { ".validate": "newData.isString() && newData.val().matches(/^[a-f0-9]{32}$/)" },
          "SidB": { ".validate": "newData.isString() && newData.val().matches(/^[a-f0-9]{32}$/)" },
          "NameA": { ".validate": "newData.isString() && newData.val().length <= 64" },
          "NameB": { ".validate": "newData.isString() && newData.val().length <= 64" },
          "CreatedAt": { ".validate": "newData.isNumber()" },
          "PkA": { ".validate": "newData.isString() && newData.val().length <= 256" },
          "PkB": { ".validate": "newData.isString() && newData.val().length <= 256" },
          "$other": { ".validate": false }
        }
      }
    },

    "pairs": {
      "$pairId": {
        ".read": "auth != null && auth.uid.length == 32 && $pairId.matches(/^[a-f0-9]{32}_[a-f0-9]{32}$/) && ($pairId.beginsWith(auth.uid + '_') || $pairId.endsWith('_' + auth.uid))",
        ".write": "auth != null && auth.uid.length == 32 && $pairId.matches(/^[a-f0-9]{32}_[a-f0-9]{32}$/) && ($pairId.beginsWith(auth.uid + '_') || $pairId.endsWith('_' + auth.uid))",
        "PairId": { ".validate": "newData.isString() && newData.val().length <= 80" },
        "NameA": { ".validate": "newData.isString() && newData.val().length <= 64" },
        "NameB": { ".validate": "newData.isString() && newData.val().length <= 64" },
        "CreatedAt": { ".validate": "newData.isNumber()" },
        "$other": { ".validate": false }
      }
    },

    "signaling": {
      "$pairId": {
        ".read": "auth != null && auth.uid.length == 32 && $pairId.matches(/^[a-f0-9]{32}_[a-f0-9]{32}$/) && ($pairId.beginsWith(auth.uid + '_') || $pairId.endsWith('_' + auth.uid))",
        ".write": "auth != null && auth.uid.length == 32 && $pairId.matches(/^[a-f0-9]{32}_[a-f0-9]{32}$/) && ($pairId.beginsWith(auth.uid + '_') || $pairId.endsWith('_' + auth.uid))",
        "offers": {
          "$senderDeviceId": {
            ".write": "auth != null && auth.uid == $senderDeviceId"
          }
        },
        "answers": {
          "$answererDeviceId": {
            ".write": "auth != null && auth.uid == $answererDeviceId"
          }
        },
        "endpoints": {
          "$senderDeviceId": {
            ".write": "auth != null && auth.uid == $senderDeviceId"
          }
        }
      }
    },

    "presence": {
      "$deviceId": {
        ".read": "auth != null",
        ".write": "auth != null && auth.uid == $deviceId",
        "LastSeen": { ".validate": "newData.isNumber() && newData.val() > now - 60000 && newData.val() < now + 60000" },
        "DisplayName": { ".validate": "newData.isString() && newData.val().length <= 64" },
        "Version": { ".validate": "newData.isString() && newData.val().length <= 16" },
        "$other": { ".validate": false }
      }
    }
  }
}
```

---

## 8. マイグレーション順序（v2 改訂・3 件新 Step 追加）

| Step | 作業 | 担当 | 不可逆 |
|---|---|---|---|
| 1 | Firebase SA 鍵発行（Console UI） | ゆろ君 | — |
| 2 | Anonymous Auth 有効化（Console UI） | ゆろ君 | — |
| 3 | Workers `/auth/token` + `/pair/token` 実装 + KV 作成 + secret 投入 | Claude | — |
| 4 | クライアント実装（DeviceIdentity 拡張 / FirebaseAuthClient / Signaling auth 注入 / PairSyncService / RemovePeerAsync 拡張 / 紛失リカバリー UI） | Claude | — |
| **5** | **Bridge を Custom Token 化** | Claude | — |
|  | (5a) `index.html` に `firebase-auth-compat.js` を SRI ハッシュ込みで追加 |  |  |
|  | (5b) `bridge.js` の main() 冒頭で `/pair/token` 経由 customToken 取得 → `firebase.auth().signInWithCustomToken(token)` await・失敗時はエラー表示で halt |  |  |
|  | (5c) `performPairing` を `db.ref().update({ "pairings/${sidA}/${pid}": data, "pairings/${sidB}/${pid}": data })` の **atomic multi-path update** に書換 |  |  |
|  | (5d) PC 内 URL ペースト経路 `FirebaseSignaling.SubmitPairingAsync` も per-device mirror write に書換・Helper 共通化 |  |  |
|  | (5e) C# 側 `StartWatchingPairing` の購読 path を `Child("pairings").Child(_sessionId)` に変更 |  |  |
| **5.5** | **Emulator + Workers ユニットテスト全 green** ⭐ 新規 | Claude | — |
|  | (a) `FirebaseAuthClientTests` (sig 構築・refresh ロジック・401/500 分岐) |  |  |
|  | (b) `PairSyncServiceTests` (404/401/5xx 区別・N 回連続 404 のみ削除・grace period・PendingPairDeletes retry) |  |  |
|  | (c) `infra/cloudflare/relay/tests/auth-token.test.ts` (vitest + miniflare・sig 検証・KV first-write-wins・JWT claims (iss/sub/aud)・rate limit) |  |  |
|  | (d) `firebase emulators:start` で Realtime DB emulator + 新 rules 検証（Bridge mock の Custom Token + mirror write、PC mock の pairs read/write、deviceId format validate） |  |  |
| 6 | テスト全 green + main マージ + `/vava` → v1.0.62 配信 | Claude | — |
| 7 | ゆろ君が両 PC を v1.0.62 に更新 + ペア作成・削除を確認 | ゆろ君 | — |
| **7.5** | **`firebase-cleanup.yml` を SA 認証化 + per-device 構造対応** ⭐ 新規 | Claude | — |
|  | (a) `curl -sf` を `curl --fail-with-body -s -w "%{http_code}"` に変更（fail silently 防止） |  |  |
|  | (b) GitHub Actions Secrets に `FIREBASE_SA_KEY_JSON` 追加 + `google-github-actions/auth` で access_token 取得 + `?access_token=...` 付与 |  |  |
|  | (c) pairings の jq ループを 2 段（`for $deviceId in keys[]; for $pid in keys[];`）に書換 |  |  |
|  | (d) `workflow_dispatch` + auth 付き dry-run で疎通確認 |  |  |
| 7.6 | presence Version の機械検証（両 PC が "1.0.62" を書き込んでいるか Firebase Console or REST で確認） | ゆろ君 | — |
| 8 | **`firebase deploy --only database`**（rules 厳格化） | Claude or ゆろ君 | **⚠️ 不可逆** |
| 9 | 旧クライアント混在 break 確認（実質ゆろ君と相手 PC のみなので影響限定的） | ゆろ君 | — |

Step 8 は **両 PC が v1.0.62 に上がってから・Step 7.5 完了後**実行する。順序を守らないと cleanup workflow が無音で停止 / Bridge が全停止 / クライアントが全停止のいずれかが起こる。

---

## 9. 2台実機検証チェックリスト（Step 7・拡張版）

別 NAT・両 PC v1.0.62 で:

### 認証経路
- [ ] 両 PC で起動時に Workers `/auth/token` から Custom Token 取得成功（ログ `Firebase Auth 認証成功 uid=...`）
- [ ] スマホ Bridge で QR スキャン時に `/pair/token` から short-lived Custom Token 取得成功
- [ ] presence / signaling / sessions / pairings / pairs の Firebase 操作が `?auth=` 付きで通る
- [ ] **Workers /auth/token から不正リクエスト（sig 不正 / ts ±60s 超過 / 既登録 deviceId の pubKey 不一致）が 400/401 を返すか synthetic test**

### ペアリング
- [ ] 新規ペア作成（QR）成功 → `pairings/{deviceA}/{pid}` と `pairings/{deviceB}/{pid}` の **両 path** に Bridge が書込（Firebase Console で確認）
- [ ] PC 側がペア成立通知を受けて `pairs/{pairId}` を書込（責任者書込 or 30s fallback）
- [ ] `StartWatchingPairing` の購読 path が `pairings/{_sessionId}` に変わって正常購読

### 削除と同期
- [ ] PC-A が削除 → PC-B のリストから即時（起動時即 + 5min ポーリング内）に消える
- [ ] PC-B をオフライン状態で PC-A が削除 → PC-B 起動時に 404 検出してローカル削除（連続 3 回 + gracePeriod 5min）
- [ ] Firebase 側 DELETE（pairs と pairings 両方）の成功確認 — ローカル削除だけでは不十分

### 長時間動作
- [ ] 1h+ 経過後も pairings watch と signaling subscribe が継続（idToken refresh + AsObservable 再購読が動く）
- [ ] presence Version フィールドに両 PC が "1.0.62" を書き込んでいる

### 既存機能との互換
- [ ] UDP ホールパンチ転送（前回 PR #9 の双方向確認）が引き続き機能
- [ ] 暗号トグル ON/OFF いずれでも auth ヘッダ付きの SDP offer/answer 交換 → 転送完走
- [ ] PairSecret 永続が `peers.json` に残っており、暗号 ON 時にも auth と PairSecret が一貫適用

### 攻撃耐性
- [ ] **Ghost peer 試行**: 異なる Custom Token を使って他人 deviceId 配下の `pairings/{victim}/{pid}` に書き込みを試して 401 PERMISSION_DENIED を確認
- [ ] **pairs 他人領域試行**: 自分が当事者でない `pairId` に対して書き込み/読込を試して 401 を確認
- [ ] **signaling sender なりすまし試行**: 自分以外の deviceId で offers/answers/endpoints に書き込みを試して 401 を確認

### 紛失リカバリー
- [ ] `identity.key` を一旦リネームしてアプリ再起動 → 401 DEVICE_PUBKEY_MISMATCH → 紛失モーダル表示 → [やり直す] で新規ペアリングできる
- [ ] [後で] でオフラインモードに入れる

---

## 10. ロールバック手順

### 10.1 Step 8 (rules deploy) 後に問題発見
1. **git-backed の rules 復元**:
   ```bash
   git checkout <旧 commit> -- src/Ferry.Bridge/database.rules.json
   firebase deploy --only database
   ```
   （Firebase Console UI ではなく git で確実復元・**Console UI 操作はファイル化されないため git 経由を必須**とする）
2. クライアントは `?auth=<idToken>` 付きアクセスを継続（緩い rules でも問題なし）

### 10.2 Workers /auth/token 障害
- `wrangler rollback` で前 deploy に戻す
- クライアント側に短期フェイルセーフ: Workers `/auth/token` が **5xx を返す場合のみ** 直前にキャッシュした最後の idToken を使い続ける（4xx の binding 不一致は除く・clean slate UI に流す）

### 10.3 Step 8 前
- コード側で対応可能。`feature/firebase-auth-and-pair-ssot` ブランチで修正→PR 更新

### 10.4 Anonymous Auth 設定
- Firebase Console UI 操作（git 管理外・手動復元）
- 「Authentication > Sign-in method > 匿名」のトグルを再操作

---

## 11. Bridge Custom Token 化詳細（v2 新規）

### 11.1 sessions/{sid}/PairingNonce
PC がセッション登録時に `Guid.NewGuid().ToString("N")` で 32hex の `PairingNonce` を生成し、`sessions/{sid}/PairingNonce` に書込。QR コードに `?sid=...&nonce=...&pk=...&name=...` 形式で埋め込む。

スマホが QR をスキャン → Bridge ページが `sid` と `nonce` を取得 → Workers `/pair/token` に送って short-lived Custom Token を取得 → Firebase Auth ログイン。

### 11.2 Bridge 改修詳細

#### `index.html`
```html
<!-- 既存の firebase-app-compat.js / firebase-database-compat.js の後に追加 -->
<script src="https://www.gstatic.com/firebasejs/9.22.0/firebase-auth-compat.js"
        integrity="sha384-..."
        crossorigin="anonymous"></script>
```

#### `bridge.js` main() 冒頭
```javascript
async function ensureAuth(sessionId, nonce) {
  const resp = await fetch('https://watashiba.kagayoi.com/pair/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId, pairingNonce: nonce })
  });
  if (!resp.ok) throw new Error('Bridge auth failed: ' + resp.status);
  const { customToken } = await resp.json();
  await firebase.auth().signInWithCustomToken(customToken);
}

async function main() {
  const params = getParams();
  try {
    await ensureAuth(params.sid, params.nonce);
  } catch (e) {
    document.body.innerHTML = '<p>認証エラー: ' + e.message + '</p>';
    return;
  }
  // 既存のフロー
}
```

#### `performPairing` を atomic multi-path update に
```javascript
async function performPairing(sidA, nameA, sidB, nameB, pkA, pkB) {
  const pid = sidA < sidB ? sidA + '_' + sidB : sidB + '_' + sidA;
  const data = { SidA: sidA, SidB: sidB, NameA: nameA, NameB: nameB, PkA: pkA, PkB: pkB, CreatedAt: firebase.database.ServerValue.TIMESTAMP };
  const updates = {};
  updates['pairings/' + sidA + '/' + pid] = data;
  updates['pairings/' + sidB + '/' + pid] = data;
  await db.ref().update(updates);
}
```

### 11.3 PC 内 URL ペースト経路
`FirebaseSignaling.SubmitPairingAsync`（[`FirebaseSignaling.cs:95-114`](../../src/Ferry/Infrastructure/FirebaseSignaling.cs#L95)）も同じ atomic multi-path update に書換。Helper メソッド `BuildPairingMultiPathUpdate(sidA, sidB, ...)` を共通化。

---

## 12. 参考

- 既存 rules draft + DO_NOT_DEPLOY コメント: [`src/Ferry.Bridge/database.rules.json`](../../src/Ferry.Bridge/database.rules.json)
- Codex P2 #3318454466 の指摘: 上記 rules ファイルのコメント参照
- DeviceIdentity（既存）: [`src/Ferry/Infrastructure/DeviceIdentity.cs`](../../src/Ferry/Infrastructure/DeviceIdentity.cs)
- Bridge 現コード: [`src/Ferry.Bridge/bridge.js`](../../src/Ferry.Bridge/bridge.js)
- 既存 FirebaseSignaling: [`src/Ferry/Infrastructure/FirebaseSignaling.cs`](../../src/Ferry/Infrastructure/FirebaseSignaling.cs)
- Workers リレー: [`infra/cloudflare/relay/`](../../infra/cloudflare/relay/)
- rere deferred 実装計画: [`docs/design/rere-deferred-implementation-plan.md`](rere-deferred-implementation-plan.md)
- Workflow 6観点検証結果: タスク `wjeuxflnd.output`（19件 designUpdates + 10件 newOpenQuestions）

---

## 13. 未解決判断（参考）

実装中・実装後に判断する事項（Q1-Q4 は決定済み）:

- **Q5 (presence Version)**: 採用（Step 7.6 で機械検証）
- **Q6 (Workers 同居 vs 分離)**: 同居採用 + relay-healthcheck.yml に `/auth/token` 専用 synthetic healthcheck（sig 付き test request → 200 + JWT 形式確認）を追加
- **Q7 (FirebaseDatabase.net AOT)**: 既存 SignalingValue と同じシンプル構造（string + long のみ）に揃える制約を厳守。System.Text.Json への移行は別 PR
- **Q8 (Anonymous Auth IaC 化)**: 不採用（手動管理・ロールバックも手動）
- **Q9 (cleanup の auth bypass)**: Firebase Admin SDK の SA JSON を GitHub Secrets に格納する方式
- **Q10 (pairs 書込 fallback)**: 採用（§6.1 で実装）
