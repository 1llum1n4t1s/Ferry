# Cloudflare 単独完結 移行設計（Firebase 撤去）

**ステータス**: Step 1-4 は **main にマージ済**（サーバ基盤 + クライアント dual-path + CF 版 Bridge）。**Step 5 = 既定フラグ反転（`UseCloudflareSignaling` 既定 false→true）をコード上で適用済**（次リリースで CF が既定経路になる。`false` 明示で Firebase へ rollback 可）。**CF インフラはライブで整備完了**（2026-06-29 実地確認: D1 `ferry_ledger` schema 適用済 / Worker secret 4 件 SESSION_HMAC_SECRET・SALT・FIREBASE_CLIENT_EMAIL・FIREBASE_PRIVATE_KEY 投入済 / ferry-relay Worker は PairDO+DeviceDO+RelayDO + auth/pair/pairs/sig/presence/inbox 全ルート込みで 2026-06-23 デプロイ済 / Bridge Static Assets 配信 200）。**残るゲートは Step 3.5/4.5 の「cold start 実測（東京 pin で p50/p95 を Firebase と比較）+ dual-path 2 台実機検証」**（いずれも 2 台実機が必要。Step 6 のコード撤去はこの検証 OK が前提）。手順は [`verify-cf-path.md`](verify-cf-path.md) 参照。
**前提**: v1.0.62 で Firebase Custom Token Auth + pairs SSoT 済（PR #10）。CF 側は relay Worker + Durable Objects(Hibernation) を本番運用中。**v1.0.64 出荷時点の既定は依然 Firebase 経路**（`AppSettings.UseCloudflareSignaling=false`）= 現行ユーザーは Firebase で接続している。
**判断根拠**: 10 エージェント Workflow 調査（棚卸し→CF一次資料→設計→4観点敵対批判）。要約は memory-bank `Ferry/design-proposals.md` の「CF 単独完結化の実現性調査」節。

> ⚠️ **Step 6（Firebase コード撤去）着手禁止ガード**: `FirebaseSignaling.cs` / `FirebaseAuthClient.cs` / `FirebasePresenceServiceFactory.cs` / `src/Ferry.Bridge/` / `database.rules.json` / `FirebaseDatabase.net` 参照 / `firebase-cleanup.yml` / `release.yml` の firebase-deploy job / Firebase 系 Secret の削除は、**Step 3.5/4.5 ゲート通過 → Step 5 既定切替 → dual-path 観測期間（数バージョン）** をすべて経るまで着手しない。v1.0.64 では Firebase が production 既定経路のため、今これらを消すと現行配布クライアント全員が起動直後に signaling 不能になる。

---

## 0. 方針（敵対的批判で確定した設計制約）

1. **dual-path で進める**: Firebase 経路を残したまま CF 経路をフラグ（`UseCloudflareSignaling`）で並存。既定は Firebase。CF を 2 台実機で検証してから既定切替。**Firebase 削除は最後・不可逆なので read-only 放置の観測可能プロトコルで**。
2. **signaling / presence は KV 不可・DO 必須**: KV は eventual 最大 60s 伝播で `WaitForOfferExternalIp(8s)`/`WaitForEndpoint(10s)` を構造的に超過し常時リレー転落。DO は single-thread で強整合・即時。
3. **realtime: signaling/presence は polling 維持、pairing 通知だけ WS**: signaling の律速は伝送速度でなく相手の STUN/ホールパンチ処理待ち（数秒）。WS push 化は cold start・wakeup 課金だけ乗って体感不変＝過剰。現状が 400ms polling なので CF でも long-poll/poll に素直に落とせる（移行リスク低）。**真 push の価値があるのは「QR スキャン直後に PC へペア成立を出す」inbox のみ** → そこだけ DeviceDO + WebSocket。
4. **cold start を移行可否の必須ゲートにする**: DO は pairId/deviceId 単位で分散し「毎回 cold」。東京 pin で「QR スキャン→PC にペア出現」「接続開始→確立」の p50/p95 を実測し Firebase 現状値と比較してから既定切替。
5. **D1 reads を線形増殖させない**: remote-unpair の 404 検出をポーリングで叩くと D1 read が台数線形で枯れ Firebase の DL 枠枯れを再現。unpair も inbox WS push で通知し、D1 は書込時の SSoT 記録 + 起動時/復帰時の低頻度確認のみ。
6. **認可は ECDSA→HMAC bearer の一段**: Firebase の「Worker が Custom Token mint → クライアントが Firebase ログイン → idToken」二段を、「Worker が ECDSA 検証して自前 HMAC bearer を発行 → クライアントが Bearer ヘッダで DO/D1 を叩く」一段に畳む。idToken の 1h 失効・SSE 再購読が消える。

---

## 1. CF リソース構成

| リソース | 役割 | キー |
|---|---|---|
| **PairDO**（pairId-keyed, SQLite-backed DO） | signaling: offers/answers/endpoints/probes/createdAt。alarm で TTL cleanup | `idFromName(SHA256(pairId + SALT))`（relay と同手法で生 pairId 横入り防止） |
| **DeviceDO**（deviceId-keyed, SQLite-backed DO） | presence(lastSeen/displayName/version) + pairing inbox(WS push + 未読キュー) | `idFromName(SHA256(deviceId + SALT))` |
| **RelayDO**（既存） | WebSocket リレー本体（変更なし） | 既存どおり |
| **D1**（`ferry_ledger`） | sessions / pairing_nonces / pairs（永続 SSoT は pairs のみ） | テーブル PK |
| **KV**（`DEVICE_KEY_BINDING`, 既存） | deviceId↔pubKeySpki first-write-wins binding（流用） | `device-pubkey:{deviceId}` |
| **Workers Static Assets** | Bridge QR ページ（`src/Ferry.Bridge/`）。Firebase Hosting 置換 | — |

> SQLite-backed DO は Workers **Free** でも動く（KV-backed のみ Paid 必須）。Ferry 規模は日次無料枠に収まる。$5 Workers Paid は RealTimeTranslator 相乗りの sunk cost。

---

## 2. 認可モデル（一段）

### 2.1 トークン取得（既存 `/auth/token` を拡張）
クライアント（PC）は起動時、既存どおり ECDSA P-256 IEEE P1363 raw 署名で `/auth/token` を叩く。Worker は ECDSA 検証 + KV first-write-wins binding（現行ロジック維持）を行い、**追加で `cfToken`（自前 HMAC bearer, HS256, TTL 1h）を応答に載せる**。

```
POST /auth/token  { deviceId, pubKeySpki, ts, sig }
→ 200 { customToken, cfToken, expiresIn }   # customToken は Firebase dual-path 用に当面併存
```

- `cfToken` = HS256 JWT。payload `{ sub: deviceId, iat, exp }`、署名鍵 `env.SESSION_HMAC_SECRET`。
- AOT: クライアントは JWT を decode しない（uid は自明、exp はレスポンス値）。plain string として保持し Bearer に乗せるだけ。
- refresh: 50min 経過で再 `/auth/token`（ECDSA 再署名）。Firebase idToken のような SSE 再購読は不要（poll/WS は次回リクエストで新 token を使うだけ）。

### 2.2 データ操作の認可
全 `/signaling/*`・`/presence/*`・`/pairs/*`・`/inbox` は `Authorization: Bearer <cfToken>` 必須。Worker が `verifySessionToken` で deviceId を取り出し:

- **signaling/pairs**: `pairId = {a}_{b}` の当事者（`deviceId == a || deviceId == b`）のみ R/W。
- **sender キー強制**: offer/answer/endpoint の書込は「自分の deviceId キー」のみ（`offers/{deviceId}` に書く。他人キーへの書込は 403）。読みは相手キー。→ #D-003 per-sender なりすまし防止を rules でなくコードで担保。
- **presence**: `POST /presence/{deviceId}` は `deviceId == token.sub` のみ。`GET /presence/{peerId}/lastSeen` は任意の認証済みが可（オンライン検知の正当用途、現行 rules の受容リスクと同じ）。
- **inbox WS**: upgrade 時に Bearer 検証、`deviceId == token.sub` の DeviceDO に接続。

### 2.3 ペアリングの認可（nonce 所有）
Bridge は PC のような長期鍵を持たない。**nonce 所有が認可の源**（現 `/pair/token` と同思想）。Bridge は `/pair/create` を 4 nonce 付きで叩き、Worker が D1 の `pairing_nonces` で両 sid の nonce 値一致を server 検証してから pairing push する。Bridge にトークンは発行しない（1 リクエスト完結）。

---

## 3. エンドポイント仕様（C# メソッド ↔ CF）

ベース URL: `https://relay.ferry.nephilim.jp`。signaling は pairId をクエリで渡し Worker が PairDO へ委譲。

### 3.1 signaling（PairDO・HTTP poll）
| C# (FirebaseSignaling) | CF エンドポイント | 備考 |
|---|---|---|
| `SendSdpOfferAsync(pairId, sender, sdp)` | `POST /sig/{pairId}/offer` `{sdp, createdAt}` | sender=token.sub。createdAt も更新（cleanup 用） |
| `TryReadOfferOnceAsync` | `GET /sig/{pairId}/offer?from={peer}&minCreatedAt=` | 200 `{data, createdAt}` / 404。**着信検知は接続ノック（offer/probe-offer POST 時に Worker がペア相手の inbox WS へ type=knock を push）が主経路**で、client のポーリングは低頻度の安全網（15s / WS 切断中 3s）。旧 `WaitForOfferAsync`（400ms 常時ポーリング）は CF 使用量削減で撤去 |
| `TryReadOfferCreatedAtAsync` | 上と同じ（createdAt のみ利用） | role 調停 deferral 用 |
| `SendSdpAnswerAsync` | `POST /sig/{pairId}/answer` `{sdp}` | answerer=token.sub |
| `WaitForAnswerAsync` | `GET /sig/{pairId}/answer?from={peer}` | 200 `{data}` / 404 |
| `SendEndpointAsync` | `POST /sig/{pairId}/endpoint` `{endpoint}` | payload は server 側で `{sender}|{endpoint}` 化（From 二重防護維持） |
| `WaitForEndpointAsync` | `GET /sig/{pairId}/endpoint?from={peer}` | From 一致を server 検証して返す |
| `SendProbeOfferAsync` | `POST /sig/{pairId}/probe-offer/{nonce}` `{sdp}` | per-nonce |
| `ReadProbeOffersAsync` | `GET /sig/{pairId}/probe-offers` | `[{nonce, sdp}]` |
| `SendProbeAnswerAsync` | `POST /sig/{pairId}/probe-answer/{nonce}` `{sdp}` | |
| `WaitForProbeAnswerAsync` | `GET /sig/{pairId}/probe-answer/{nonce}` | 200 `{sdp}` / 404 |
| `CleanupProbeAsync` | `DELETE /sig/{pairId}/probe/{nonce}` | offer/answer 両方 |
| `CleanupSignalingDataAsync` | `DELETE /sig/{pairId}` | leaf 一括（offers/answers/endpoints/createdAt） |

PairDO storage キー: `offer:{sender}`→`{data,createdAt}` / `answer:{sender}`→`{data}` / `endpoint:{sender}`→`{data}` / `probeOffer:{nonce}` / `probeAnswer:{nonce}` / `createdAt`。alarm で `now - createdAt > 1h` の pair データを一掃（lazy cleanup を主、alarm は補助。短間隔で全 DO を起こさない方針 — 公式警告）。

### 3.2 presence（DeviceDO・poll）
| C# | CF | 備考 |
|---|---|---|
| `UpdatePresenceAsync(deviceId, name)` | `POST /presence/{deviceId}` `{displayName, version}` | lastSeen は server now。authz uid==deviceId |
| `GetPresenceLastSeenAsync(peerId)` | `GET /presence/{peerId}/last-seen` | ETag/If-None-Match で 304 対応（帯域節約維持）。`{lastSeen}` |
| `GetPresenceAsync(deviceId)` | `GET /presence/{deviceId}` | フル（displayName 同期用） |
| `RemovePresenceAsync` | `DELETE /presence/{deviceId}` | 終了時 |

### 3.3 pairing（D1 + DeviceDO）
| C# | CF | 備考 |
|---|---|---|
| `RegisterSessionAsync(deviceId, name, pk)` | `POST /pair/session` `{displayName, publicKey, pairingNonce}` | D1 sessions + pairing_nonces upsert。authz uid==deviceId |
| `CheckSessionAsync(sid)` | `GET /pair/session/{sid}` | `{displayName, publicKey}` / 404 |
| Bridge `performPairing`（QR/Bridge 経路） | `POST /pair/create` `{sidA,nameA,pkA,sidB,nameB,pkB,nonceA,nonceB}` | bearer なし。両 nonce 値所有を D1 で server 検証 → 両 sid の DeviceDO inbox に pairing push |
| `SubmitPairingAsync`（PC コード貼付ペアリング、Step 7 で CF 対応） | `POST /pair/link` `{sidB,nameA,nameB,pkA,pkB}` | bearer 必須（sidA は claims.deviceId）。相手 sidB は「セッションがアクティブ」のみ要求（nonce 値所有は不要） |
| `StartWatchingPairing()` 購読 | `GET /inbox?deviceId={id}` (WS) | DeviceDO が pairing イベントを push。接続時に未読 flush(connect-60s gate) |
| `RevokePairingTokensAsync(sid)` | `DELETE /pair/session/{sid}` | sessions + pairing_nonces 削除 |

### 3.4 pairs SSoT（D1）
| C# | CF | 備考 |
|---|---|---|
| `PutPairAsync(pairId, rec)` | `PUT /pairs/{pairId}` `{nameA, nameB, createdAt}` | authz 当事者。D1 upsert |
| `GetPairAsync` / `GetPairWithStatusAsync` | `GET /pairs/{pairId}` | 200 `{...}` / 404。PairSyncService の 404/401/5xx 区別をそのまま HTTP ステータスで |
| `DeletePairAsync` | `DELETE /pairs/{pairId}` | unpair。相手へは inbox WS で unpair push も送る（D1 ポーリング線形増殖の回避・§0-5） |

---

## 4. クライアント書き換え（C#）

- **新規** `Infrastructure/CloudflareSignaling.cs`: `IPresenceService` 実装 + FirebaseSignaling と同じ public API（ConnectionService から差し替え可能に signature を揃える）。plain `HttpClient`（Bearer 注入）でポーリング。base64/From 検証/backoff など現行ロジックは流用。
- **新規** `Infrastructure/CloudflareInboxClient.cs`: `ClientWebSocket` で `/inbox` 購読。**自前再接続**（指数バックオフ + 未読 flush + 重複冪等化 + connect-60s replay gate）。`PairingDetected` イベントを発火。
- **新規** `Infrastructure/CfTokenProvider.cs`: 既存 `DeviceIdentity` の ECDSA 鍵で `/auth/token` を叩き `cfToken` を取得・50min refresh・Bearer 提供。
- **フラグ** `AppSettings.UseCloudflareSignaling`（既定 false）。`App.axaml.cs` の手動 DI で `FirebaseSignaling` か `CloudflareSignaling` を選択。
- **AOT**: `FirebaseDatabase.net` 依存は CF 経路では不使用。`System.Text.Json` SourceGen に CF DTO（OfferDto/AnswerDto/PresenceDto/PairDto/InboxEventDto）を追加。
- ConnectionService / ConnectionViewModel は `IConnectionService`/`IPresenceService` 抽象に依存させ、実装差し替えで両経路を吸収（God クラス分割の B1-7 とも整合）。

> 既存 `FirebaseSignaling.cs` / `FirebaseAuthClient.cs` / `FirebaseDatabase.net` / `database.rules.json` / `firebase.json` の撤去は **最終段**（§5 Step 6）。dual-path 期間は温存。

---

## 5. 段階移行（不可逆点は Step 7 のみ）

| Step | 作業 | 可逆性 |
|---|---|---|
| 1 ✅ | **サーバ基盤追加**: PairDO + signaling ルート + auth cfToken（`ccb9374`）。Firebase 不変 | 完全可逆（デプロイ戻すだけ） |
| 2 ✅ | DeviceDO(presence+inbox) + D1 台帳 + pairing/pairs ルート（`1392ba6`）。Firebase 並存 | 完全可逆 |
| 3 ✅ | クライアント dual-path（`UseCloudflareSignaling` フラグ・`37cb81f`）。既定 Firebase | 完全可逆（フラグ） |
| 4 ✅ | **Bridge 二系統**: CF 版 Bridge（`infra/cloudflare/relay/public/` を Workers Static Assets で配信、同一オリジン `/pair/create` 直叩き・Firebase SDK 不使用）を追加。旧 Firebase Bridge（`src/Ferry.Bridge/`）は温存。QR は `UseCloudflareSignaling` で `CfBridgePageUrl` に切替 | 完全可逆 |
| 3.5 / 4.5 | **cold start 実測ゲート**（東京 pin、Firebase と p50/p95 比較）+ dual-path 2 台実機 + D1/SESSION_HMAC_SECRET プロビジョン + `wrangler deploy` | — |
| 5 🔶 | 既定を CF に切替（`UseCloudflareSignaling` 既定 false→true・コード適用済）。Firebase 経路はコードに数バージョン残置。実機検証 OK を確認してから出荷 | フラグ戻しで可逆 |
| 6 | Firebase コード撤去（FirebaseSignaling/AuthClient/SDK/rules/firebase.json）+ cleanup を DO alarm に置換 | git revert で可逆 |
| 7 | **Firebase プロジェクト無効化**（RTDB/Auth/Hosting）。**read-only 化 → N ヶ月放置 → アクセスログが閾値以下で削除**（Velopack R2 は配信統計を持たないため「全台更新確認」は観測不能 → 観測可能な放置プロトコルに差し替え） | **不可逆**（事前に pairs 台帳エクスポート） |

**dual-write 期間の pairs 台帳整合（Step 3-5）**: CF 既定切替前は Firebase を真とし、CF へは shadow write のみ。切替時に Firebase pairs を D1 へ一括移植（deviceId↔pairId↔PairSecret の 3 点セット保全）してから CF を真にする。両書き期間に「どちらが真か」を曖昧にしない。

---

## 6. テスト

- **vitest（relay）**: session token mint/verify、signaling authz（当事者外 403・sender キー強制）、PairDO storage ラウンドトリップ、nonce server 検証、alarm cleanup。
- **C#（xUnit）**: CloudflareSignaling のポーリング/backoff/From 検証、InboxClient の再接続/replay gate、CfTokenProvider の refresh、`SafePath` 等の既存回帰維持。
- **2 台実機（別 NAT）**: dual-path で「接続完了 経路:」「暗号セッション確立」ログ、QR ペア成立、unpair 伝播、1h+ 継続。
