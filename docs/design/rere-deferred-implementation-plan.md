# rere レビュー deferred 項目 実装計画書

このドキュメントは `/rere`（10 人分隊レビュー, 2026-06-18）で検出され、**仕様変更・設計判断・2 台実機検証**を伴うため即時実装を見送った指摘について、**着手可能な粒度の実装計画**を記録する。挙動不変で適用済みの修正（B1-003 / B1-001 / B1-004/005 / C2-001/002 / A2-001 / F-001/002 / B1-007/008 / B2-001/002）は本書の対象外（コミット済み）。

> ⚠️ **共通の前提**: ここに挙げる項目は、ユニットテスト + ビルド + 敵対的レビューだけでは正しさを保証できない（接続確立の race・NAT 越え・暗号ハンドシェイクは **別回線の 2 台実機**でしか最終検証できない）。各項目の「検証」節の実機手順を踏むまで「完了」としない。

---

## P0-SEC: E2E 暗号配線（#D-001b / A1-001/002/003/004 + A2-002 を一括解消）

### 実装状況（2026-06-18 更新: Phase 1-3 のクライアント側コードを実装・単体テスト済み）

**実装・単体テスト済み（ビルド 0 err / テスト 324、暗号系 +24）**:
- **Phase 1（PairSecret 確立）**: `DeviceIdentity`（長期 ECDH 鍵を `%APPDATA%\Ferry\identity.key` に永続）/ QR に `&pk=` 付与 / `RegisterSession`・`SubmitPairing`・`CheckSession` と `PairingData`/`SessionData` に pk 配線 / Bridge(`bridge.js`) の pk 中継 / `OnPairingDetected` で ECDH→`PairSecret` を導出し `PairedPeer.PairSecret` に永続。テスト: `DeviceIdentityTests`（ECDH 対称性・鍵永続・base64url）。
- **Phase 2/3（HMAC ゲート + AES-GCM 封筒）**: `SecureChannel`（自己ネゴシエーション状態機械: 両端対応→HMAC 相互認証→封筒 / 片側非対応→平文フォールバック / HMAC 不一致→切断 / attach レースの Init バッファ / 順不同救済）/ `TransferProtocol` に `SecureHello(0x30)`/`SecureConfirm(0x31)` / `ConnectionService` に結線（接続前に `CreateSecureChannel`、成立点で `StartSecureHandshake`、`SendAsync` ゲート+封筒、`OnDataReceived` 復号、ロックで状態機械直列化）/ `AppSettings.EnableSecureChannel`（既定 false）。テスト: `SecureChannelTests`。
- **フラグ OFF（既定）はバイト等価**: `EnableSecureChannel=false` or `PairSecret` 無しなら `_secureChannel=null` で送受信は現状パス。よって本変更は挙動不変（仕様変更なし）。
- **前方互換**: `database.rules.json` の sessions/pairings に `PublicKey`/`PkA`/`PkB` の validate を追記（未デプロイ）。

**残（2 台実機 / デプロイが必要・未完）**:
- **実機疎通検証**: 別回線 2 台で、コード貼付ペアリング（pk は session 経由で運ぶため **Bridge 未デプロイでも可**）→ 両 PC で `settings.json` の `EnableSecureChannel=true` → 接続 → ログ「暗号セッション確立（HMAC 相互認証成功）」確認 → 転送成功。攻撃者役のリレー席奪取が HMAC 不一致で弾かれること。旧ペア（PairSecret 無し）が平文で繋がること。
- **Bridge デプロイ**: QR ペアリングで pk を運ぶには `src/Ferry.Bridge` の Firebase Hosting 再デプロイが必要（コード貼付経路は不要）。
- **Phase 4（Firebase rules + Anonymous Auth）**: 暗号チャネル自体は rules 無しで機能する（直交）。A2-002/A2-001 の攻撃元を断つ追加ハードニングとして別途。`database.rules.json` は `DO_NOT_DEPLOY` のまま。
- **設定 UI**: ✅ `EnableSecureChannel` トグルを設定画面（ファイル転送セクション）に追加済み（`SettingsViewModel`/`SettingsView.axaml`/`en_US`/`ja_JP`。他16言語は en_US フォールバック）。既定 OFF。
- **Bridge デプロイ**: コード（`bridge.js` の pk 中継）は適用済み・後方互換。ローカル `firebase` は未認証（`firebase login` が対話式で要手動）。**次の `/vava` リリースの CI（`release.yml` の firebase-deploy job, 公式 Action が `--only hosting` 相当）で自動デプロイされる**ため手動は不要。手動で先行する場合は `firebase login` → `cd src/Ferry.Bridge && firebase deploy --only hosting`（**必ず `--only hosting`**。`firebase.json` に database 設定もあり、無印 deploy は DO_NOT_DEPLOY ルールを撒いてアプリ全停止する）。
- ⚠️ 本 Phase が実機検証で完了するまで CLAUDE.md §既知の制限の「平文」記述は削除しない（既定動作は今も平文）。

### Phase 4（Firebase rules + Auth）が「今すぐ実施」できない理由（重要）

`database.rules.json` は `auth.uid == deviceId`（sessions/presence）・`SidA/SidB == auth.uid`（pairings）・`$pairId.contains(auth.uid)`（signaling）を要求する。これを満たすには:
1. **`auth.uid` を deviceId に一致させる必要がある** → Anonymous Auth はランダム uid なので不一致（全 write が `permission_denied`）。一致には **Custom Token Auth（= #D-001a：Workers のトークン発行エンドポイント + Firebase サービスアカウント秘密鍵という新シークレット）** が前提。#D-001a はゆろ君の明示 GO 待ちで保留中・不可逆。
2. **pairings は Bridge（スマホ=第三者）が 2 PC 代理で書く**モデルなので `newData.SidX == auth.uid` と構造的に矛盾（Codex P2 blocker）。per-device inbox への restructure + Bridge auth 設計が要る。
3. **rules deploy は不可逆**で、Auth 配線と同一リリースで揃えないと部分適用で**全ペアリング・転送・presence が停止**する（rules ファイル冒頭 `DO_NOT_DEPLOY` と CI コメントが明記する戦略）。

→ **実際の盗聴/MITM 保護は Phase 1-3 の暗号層が既に担う**ため、Phase 4 は防御の多層化（Firebase アクセス制御）であって機密性の必須要件ではない。多日・不可逆・新シークレットを伴うので、`#D-001a` の GO とサービスアカウント鍵の用意がそろってから着手する。

### 現状
- 暗号コア `PairCrypto` / `SecureSession` / `PairingHandshake` は実装・テスト済みだが **live コードから未呼出（inert）**。転送は平文。
- `signaling/{pairId}/...` は匿名 R/W（`database.rules.json` は記述済みだが Anonymous Auth 未実装でデプロイ不可）。
- リレー（`infra/cloudflare/relay/src/index.ts`）は `live.length >= 2` のみで入室判定し、pairId を知る第三者が席を奪える（A1-002）。受信側 `TransferService.HandleFileMeta` の `receivePeerId`（`TransferService.cs:742`）は transport 接続のみを信頼（A1-003）。

### 目的
接続確立後に**ペア相互認証（HMAC）**を必須化し、認証成功までデータ転送をゲートする。これにより A1-002（席奪取）/A1-003（なりすまし受信）/A1-004（probe 詐称）を一括で塞ぐ。さらに AES-GCM 封筒で**リレー経由の中継盗聴**を防ぐ。

### 実装手順（フェーズ分割）
**Phase 0（済）**: 暗号コア（`PairCrypto` ECDH P-256 + HKDF、`SecureSession` AES-GCM + HMAC + anti-replay、`PairingHandshake`）。

**Phase 1 — ペアシークレット確立（QR 公開鍵交換）**
1. `PairCrypto` に長期 ECDH 鍵ペアの生成・永続を追加（`%APPDATA%\Ferry` に秘密鍵、公開鍵は QR に載せる）。AOT: 鍵の JSON 化は SourceGen Context を追加。
2. QR ペイロード拡張: 現在 `?sid={deviceId}&name={name}`（`ConnectionViewModel.cs:152`）に `&pk={base64url(publicKey)}` を追加。
3. Bridge（`src/Ferry.Bridge/bridge.js` + `index.html`）: スキャンした両 PC の `pk` を `pairings/{pairingId}` に中継書き込み（現状 sid/name のみ）。**← Firebase Hosting への再デプロイが必要**。
4. ペアリング完了時（`ConnectionService.OnPairingCompleted` 付近）に、自分の秘密鍵 × 相手の公開鍵で ECDH → `PairSecret` を導出し、`PairedPeer`（`Models/PairedPeer.cs`）に保存（`PeerRegistryJsonContext` 更新）。

**Phase 2 — セッション HMAC ゲート**
5. 各 transport（TCP/UDP/Relay）接続確立直後に `PairingHandshake`（チャレンジ-レスポンス HMAC, メッセージ種別 0x30/0x31 を `TransferProtocol` に追加）を実行。`ConnectionService.AttachTransportEvents`（`ConnectionService.cs:1392` 付近）で `DataReceived` を**認証成功までバッファ/ドロップ**するゲートを挟む。
6. HMAC 不一致なら即切断 + `Logger.Error`。`PairSecret` 未保有のペア（旧データ）は後方互換のため平文フォールバック（Phase 3 のフラグで制御）。

**Phase 3 — AES-GCM 封筒 + フラグ**
7. `AppSettings` に `EnableSecureChannel`（既定 `false`）を追加。ON かつ両側が `PairSecret` 保有時のみ、`SecureSession` で各メッセージを封筒化（`TransferProtocol` の送受に封筒 encode/decode を挟む）。OFF または片側未対応なら平文（現状維持）。
8. ネゴシエーション: Phase 1 ハンドシェイクで相手が暗号対応か判定し、両対応時のみ暗号化（非対応相手とは平文）。

**Phase 4 — Firebase rules デプロイ**
9. Anonymous Auth（`auth.uid = deviceId`）を .NET + Bridge 両方に配線 → `database.rules.json` をデプロイ（`signaling/{pairId}` を `$pairId.contains(auth.uid)` で制限）。**Auth 配線とルールデプロイは同一リリースで揃える**（片方だけだと `permission_denied` で全停止）。これで A2-002 と A2-001 の攻撃元を断つ。

### 影響範囲
`PairCrypto` / `PairingHandshake` / `SecureSession`（live 化）、`ConnectionService`（ハンドシェイク・ゲート）、3 transport、`TransferProtocol`（0x30/0x31 + 封筒）、`ConnectionViewModel`（QR pk）、`PairedPeer` + `PeerRegistryJsonContext`、`src/Ferry.Bridge/*`（再デプロイ）、`database.rules.json`（デプロイ）。

### リスク
- HMAC ゲートが正規ピアを誤って弾くと**全接続が壊れる** → 既定 OFF + 平文フォールバックで段階導入必須。
- Bridge デプロイのタイミングずれで pk 未着 → ペアシークレット欠落 → 暗号無効（平文に落ちるので致命ではないが要監視）。

### 検証（実機必須）
別回線の 2 台で: ①ペアリング → `PairSecret` が両 `peers.json` に保存される ②`EnableSecureChannel=true` で接続 → HMAC ハンドシェイクログ → 転送成功 ③攻撃者役で pairId を使いリレー席を奪取 → HMAC 不一致で弾かれることを確認 ④旧クライアント（pk なし）と平文で繋がること。
memory `ferry-d001-mitm-crypto-first` の方針（明示 GO + 段階実機）に従う。**本計画完了まで CLAUDE.md §既知の制限の「平文」記述は削除しない**。

---

## D-005: 同時接続 role の決定論化（deferral TOCTOU の根治）

### 現状
`ConnectionService.cs:594-646` の deviceId 序列 deferral は、相手の fresh offer を **1 回 peek した瞬間**に見えれば譲歩する TOCTOU を内包。両者がほぼ同時に接続すると両者 offerer → 15s 空振りフォールバック → 体感 15-30s 遅延、最悪再衝突（CLAUDE.md §既知の制限 1 が自認）。

### 実装手順
1. peek/鮮度ヒューリスティクスを廃止し、**決定論的役割固定**: 両者が常に offer を per-sender ノード（`offers/{deviceId}`）に書いた上で、`CompareOrdinal(_deviceId, peerId) < 0` の側を**常に offerer**、他方を**常に answerer** とする。
2. answerer 側は自分の offer を書かず、相手の offer ノードをポーリングして answer を返すだけにする（per-sender ノード化済みなので相互上書きは構造的に起きない）。
3. `WaitForListenerConnectedAsync` の 15s 空振りフォールバック経路を削除/縮退。

### 影響範囲
`ConnectionService` の role 調停（594-646）+ `StartListeningForConnection` / `WaitForListenerConnectedAsync` 連鎖。`RoleDeferFreshnessMs` / `RoleDeferListenTimeout` 定数は不要化。

### リスク（高）
接続確立パスの中核変更。バグると**両者 answerer デッドロック**や接続不成立で、ユーザーが繋げなくなる。フラグでの段階導入が難しい（役割固定は二者で一貫している必要がある）。

### 検証（実機必須）
2 台で**同時接続ボタン押下**を多数回繰り返し、両者 offerer→15s フォールバックが消えること、cross-NAT で確立すること、片側のみ接続でも成立することを確認。

---

## D-006: 差分レジューム（部分受信ビットマップ駆動）

### 現状
`ResumeTransferAsync`（`TransferService.cs:337`）は `startChunk=0` 全再送、受信側は承認時にファイル再作成、`ResumeResponse` は false 固定。受信側は順不同 Seek 書込 + `ReceivedChunkSet` ビットマップを持つが活用していない。

### ⚠️ 設計上の障害（要解決）
**現状は切断時に部分ファイルを削除する**（`TransferService.OnConnectionLost` の「受信中の部分ファイルを削除」, rere #D-005/#F-014）。差分レジュームは「部分ファイル + ビットマップが切断後も生き残る」前提なので、この cleanup と**正面衝突**する。よって D-006 は単なるプロトコル変更でなく、**受信側の部分ファイル寿命の再設計**を伴う。

### 実装手順
1. 受信状態（`ReceiveState`）の `ReceivedChunkSet` を**ディスク永続**（部分ファイルと並んで `.ferrypart` メタに保存）するか、少なくとも同一プロセス内の一過性切断では `_receiveStates` を破棄せず保持する。
2. `OnConnectionLost` の部分ファイル削除を「レジューム可能なら保持、TTL 超過 or 明示キャンセルで削除」に変更。
3. `ResumeRequest`（0x20）→ 受信側が `ResumeResponse`（0x21）に **未受信 chunkIndex 集合（ビットマップ）** を載せて返す。
4. 送信側 `SendChunksAsync` は `startChunk` でなく**欠落 chunk のみ**送信（既存の skip ロジックを流用）。
5. 受信側は承認時にファイル再作成をやめ、既存部分ファイルに追記。最終 SHA-256 検証は全 chunk 揃ってから（現状維持）。

### 影響範囲
`TransferService`（OnConnectionLost / HandleFileMeta / ResumeTransferAsync / Handle...Resume）、`FileChunker`（ResumeResponse に bitmap、既に `ParseResumeResponseV2` の足場あり）、`TransferProtocol`。

### リスク（中）
部分ファイル破損のリスクがあるが、**最終 SHA-256 検証が安全網**（不一致なら転送失敗 = 全再送に落ちるだけで、無言の破損ファイルは生まれない）。プロセス再起動跨ぎはビットマップ永続が要る（しなければ従来どおり全再送）。

### 検証（実機推奨）
数 GB 転送を 50%/99% で切断 → 再接続 → 欠落分のみ再送されること（ログのチャンク数）、SHA 検証成功、部分ファイルが正しく追記されることを確認。

---

## D-001: 複数ペア同時接続（arch-judgment / プロダクト方針）

### 現状
接続・転送スタック全体が「同時 1 ペア」を硬く仮定（`_transport` 単数 `ConnectionService.cs:81`、`ConnectedPeer` 単数、新接続が既存 `_transport` を Dispose、受信は `ConnectedPeer` で逆引き `TransferService.cs:742`）。A と転送中に B を選び送信すると進行中 A 転送が黙って切断されうる。

### ⚠️ プロダクト方針判断が先
「1 対 1 か、複数同時か」は機能方針の判断。Ferry が個人向け 1:1 ツールであり続けるなら**現状の単純化が正しい**（YAGNI 回避）。複数ペア対応は新機能であり、PO 判断が前提。

### 実装方向（採用する場合）
- `ConnectionService` を `peerId → ConnectionSession`（`ITransport` / `_signaling` / CTS をセッション単位に保持）の集約に分解。
- transport インスタンスが自分の peerId を保持し、`DataReceived` に peerId を付帯 → `TransferService` の受信ルーティングを `ConnectedPeer` 逆引きから transport 直結へ。
- `_connectGate` の直列化を per-peer 化。
- インクリメンタル経路: まず `ConnectedPeer` → `ConnectedPeers`（集合）化 + 受信ルーティング変更から。

### リスク（高・multi-day）
全接続/転送層の書き換え。回帰面が極めて大きく、2 台×複数ペアの実機検証が要る。

### 暫定の止血（方針判断前でも可）
VM 層で「進行中転送があるピアからの接続切替」を抑止/警告するゲートを置く（プロトコル不変）。これは挙動を「黙って切断」から「警告」に変えるだけなので比較的安全。**← 別途実装可否を要確認**。

---

## D-002: シグナリング基盤（Firebase RTDB polling → Cloudflare Durable Object）

### 現状
RTDB はネイティブ stream を持つのに offer/answer/endpoint/offer-v2 を **400ms polling**（`FirebaseSignaling.cs:170-241` ほか 4 箇所）。「イベント駆動を標榜しつつ polling」の乖離、UDP ホールパンチの非対称タイミングが polling 周期に従属。Spark 無料枠の読み取りも逼迫しうる。

### 実装方向（multi-day・インフラ方針）
- シグナリングを Cloudflare Durable Object（既にリレーで運用中）へ寄せ、WebSocket で offer/answer/endpoint を push。RTDB は presence/pairings に限定。
- rere #D-003 の per-sender ノード設計と MITM 検証（From==peer）を push 経路にも適用。
- 中間ステップ: まず RTDB の `AsObservable()`/stream に寄せて polling をイベント駆動化（基盤は Firebase のまま乖離だけ縮める）+ 時間定数の安全マージンを 2s→5s に広げ polling ジッタ耐性を上げる。

### リスク（高）
シグナリング基盤の差し替えは接続確立の全経路に影響。2 台実機 + Cloudflare インフラ構築。

---

## D-004: UDP 信頼性レイヤー → QUIC（System.Net.Quic）

### 現状
`UdpHolePunchTransport`（`:38-51`）は TCP 相当を手書き。輻輳制御なし、RTO 固定 300ms（RTT 推定なし）、WindowSize 128（≒150KB 固定窓）。高 RTT/高損失で過剰再送 → 自己輻輳、高 BDP 回線で窓過小。

### 実装方向（要設計判断）
- ホールパンチで開けた穴の上に `System.Net.Quic`（MsQuic）を載せ、信頼性・輻輳・フロー制御を委譲。リレー経路（WebSocket）と P2P 経路（QUIC）の信頼性モデルが近づく。
- **Native AOT で MsQuic がトリミング安全か**、ホールパンチ済みソケットを QUIC に引き渡せるかの事前検証が必須。

### 暫定改善（QUIC 移行前でも可・低リスク）
固定 RTO を簡易 SRTT+RTTVAR の RTT 推定に置換、window を BDP 連動に。輻輳制御の最小版（AIMD）を再送ループに追加。**← 自前レイヤー内に閉じるので別途実装可否を要確認**。

### リスク（中〜高）
QUIC 移行は AOT 制約 + 実機。暫定改善は `UdpHolePunchTransport` 内に閉じるが、再送挙動の変更は高損失回線での実機検証が要る。

---

## D-003: リレー DO backpressure（プラットフォーム制約で実装見送り）

### 結論
Cloudflare Workers の WebSocket は **`bufferedAmount` を実装していない**（`@cloudflare/workers-types` に doc コメントのみで実プロパティ宣言なし = workerd#988）。よって DO 側でドレイン未了量を観測する backpressure は**プラットフォーム制約で実装不可**。

### 既存の緩和（維持）
クライアント側アプリ層フロー制御 `FileFlowAck`（窓 512=32MB）が中継バッファを ~32MB に抑え、DO 128MB に 3-4 倍マージン。`Route==Unknown` も安全側でフロー制御 ON。

### 本セッションで追加済み
`peer.send` の失敗を握り潰さずログ（F-001）→ ドロップが起きたら `wrangler tail` で観測可能になった。

### 将来の根本対策（要 protocol 変更）
リレー経由に WebSocket の明示 pause フレーム（クライアント↔リレー双方の protocol 拡張）を入れる。本番リレーの protocol 変更は実機検証が要るため別 PR。

---

## F-003: Firebase Spark 枠（download 10GB/月）枯渇監視

### 結論（GitHub Actions cron でなく GCP ネイティブアラートが正解）
GitHub Actions の cron で GCP Monitoring を polling するのは脆い（SA 鍵管理 + JWT 署名 + テスト不能）。正解は **GCP Cloud Monitoring のアラートポリシー**（1 度設定すれば常時監視、cron 不要）。

### 設定手順（GCP Console、ゆろ君が 1 度実施）
1. GCP Console → 対象 Firebase プロジェクト → Monitoring → Alerting → Create Policy。
2. Metric: `firebasedatabase.googleapis.com/network/sent_bytes_count`（RTDB egress）を選択、集計を 30 日 sum。
3. 閾値: 7GB（70%）で warning、9GB（90%）で critical。
4. 通知チャンネル: メール（`yuro.7878@gmail.com`）or Slack。
5. 併せて GCP Billing → Budgets & alerts で予算アラートも設定（Spark でも Monitoring 経由で egress を見られる）。

### 発動時のアクション
枠接近 = presence 帯域節約策（可視性ゲート/ETag/LastSeen 限定）が破れたサイン → 次手は presence を Cloudflare（Workers Paid + KV/DO）へ逃がす（本書 D-002 と連動）。

---

## A1-004: probe offer の SSRF（crypto 依存）

`HandleProbeOfferAsync`（`ConnectionService.cs:505-551`）は `From==peerId`（署名なし自己申告）のみで宛先 IP を offer 由来で TCP 接続試行。pairId 既知の第三者が From 詐称で被害ホストを内部ポートスキャンの片棒にできる（実害は接続プローブ止まり）。**根治は P0-SEC の HMAC ゲート（From を HMAC で裏取り）**に統合。probe を private/local レンジに制限する暫定案は cross-NAT 正規 probe を壊すため不可。

---

## 実装順序の推奨

1. **P0-SEC（暗号配線）** — 唯一の P0。Phase 1→4 を順に、各 Phase ごとに 2 台実機。最重要。
2. **D-006 差分レジューム** — SHA 安全網があり中リスク。部分ファイル寿命の再設計とセットで。
3. **D-005 役割決定論化** — 接続中核で高リスク。十分な同時接続実機を確保してから。
4. **D-004 暫定（RTT 推定）/ D-002 中間（stream 化）** — 各レイヤー内に閉じる改善から段階的に。
5. **D-001 複数ペア** — プロダクト方針判断が先。採用なら暫定の VM ゲート（止血）→ セッション集約（根治）。
6. **F-003** — GCP アラート設定（コード不要、Console 作業）。
