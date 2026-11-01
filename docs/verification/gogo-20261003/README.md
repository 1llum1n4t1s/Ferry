# gogo 検証・修正記録（2026-10-03）

RELAY-01/03・CORE-04/05 の現在の結論は、末尾の 2026-11-01 追補を参照する。以下の本文は 2026-10-03 時点の経緯を保持している。

対象は直前のrere一次調査13候補。全件freshとして現在の実コード・呼出し元・既存契約から反証した。
9件を修正、1件を限定修正、3件は問題成立だが処方未判定。rereの独立V1/V2検証の完了を意味しない。
開始時のmac起動修正・テスト整理・既存生成物を保持し、今回unit testは追加していない。
変更は未ステージ。version、Git履歴、配信、本番データを変更していない。

## 指摘別の結論

| ID | 判定 | 変更・反証結果 |
| --- | --- | --- |
| RELAY-01 | 未判定（問題成立／処方保留） | KV get→putに排他がない。強整合正本と既存鍵移行が必要。isolate内lockや再読込では解決しない。 |
| RELAY-02 | 採用 | URLSearchParams後の二重decodeを2箇所削除。%を含む3表示名×2入口で完全一致。 |
| RELAY-03 | 未判定（問題成立／処方保留） | nonce確定後に通知が片側だけ失敗する。既知peerの同期では未登録peerを復元できない。永続配送予定が必要。 |
| CORE-01 | 採用 | 受信取消Rejectと、索引回収後のFlowAckに受信元peerを明示。primaryへの誤配送を再現条件で防止。 |
| CORE-02 | 差し替え採用 | 作成中もpendingに残し、短い登録ゲートで状態交換と終了を調停。I/Oは外。旧stateによる新state除去を参照一致で防ぐ。 |
| CORE-03 | 採用 | UDPの送信終了時回収をfinallyへ移動。利用者取消後も128枠が戻り、同transportの次送信が成功。 |
| CORE-04 | 未判定（問題成立／処方保留） | ACKはTransferIdを持たず送信結果へ反映されない。旧版互換と受信検証待ち期限の契約が必要。 |
| CORE-05 | 最小採用 | 同peer/rootの到着済みpending/receiveが残る間は保存先を保持。別peerの待機による次回保存先混在も防止。未着項目・別バッチ識別は残る。 |
| UI-01 | 採用 | サルベージ失敗・退避失敗からも正常なID副本を試す。null JSONも破損扱い。原本IDを副本より優先。 |
| UI-02 | 差し替え採用 | :is(Window)の共通StyleとDynamicResourceに置換。初回・後発確認画面・既存画面の方向変更を検証。対応RTLはhe_ILのみ。 |
| UI-03 | 最小採用 | 言語変更操作でスキップ中バージョンの説明を再通知。実画面TextBlockが更新。 |
| UI-04 | 採用 | 26操作へ既存翻訳キーのAutomationProperties.Nameを付与。18言語のキー欠落0。設定11操作のAutomationPeerを2言語で実測。 |
| OPS-01 | 採用 | runbook・詳細設計・構成図の廃止web/deploy-landing参照を現行vps-webの配信経路へ統一。 |

## 同じ原因の検索範囲

- RELAY-02: bridge.jsのdecode検索。必要2箇所、残る二重decode0。
- CORE-01: TransferServiceの返送経路。必要2経路（取消Reject・FlowAck）。メタ拒否など明示peerを持つ既存経路は不要。
- CORE-02: Approve、ConnectionLost、Reject受信、Cancel、Reject操作、chunk、検証、検証起動、cleanupの9経路を必要と判断。送信側の承認辞書は別状態で不要。
- CORE-03: UDP送信の終了処理1箇所。TCP/WSには当該再送packet/window状態がなく、2transportへの横展開は不要。
- CORE-05: 承認・完了・拒否・取消・切断・書込失敗を共通のpeer/root解放へ接続。未着バッチ寿命は未判定。
- UI-01: Settings.Loadの欠損副本復元を再利用。null JSON1経路を追加。peers/pending-deleteの2保存系はID生成主体でなく不要。
- UI-02: 共通Window Style1箇所で派生画面も対象。既存のWindow列挙方式を削除。
- UI-03: 既存locale変更操作1箇所のみ必要。PairedPeer/TransferItemの2通知系は既に追従し不要。
- UI-04: Settings/MainWindow/TransferView/AddMemberの26操作が必要。文字列または単一TextBlockをContentに持つ操作は既存Name経路で十分。
- OPS-01: 現行文書3箇所が必要。2026-05の移行依頼書2記述は日付付きの過去資料で変更不要。
- RELAY-03: create/linkの2成立経路に配送保証が必要。DELETE通知は既知peerの404同期で復旧できるため同処方不要。

## 検証

Windows 10.0.26300、.NET 10.0.12、Avalonia 12.1.3、Node v24.18.0で実行した。
対象Release DLL SHA256: `64ED96642D4B3B44AF91DD468D77040DA69475AA4669DC71FD64AB426701C0E8`。

| 検証 | 結果 |
| --- | --- |
| dotnet build src/Ferry/Ferry.csproj --no-restore | 成功、警告0・エラー0 |
| dotnet test tests/Ferry.Tests/Ferry.Tests.csproj --no-restore | 455成功・失敗0・skip0 |
| relay: pnpm exec tsc --noEmit / pnpm test | 型チェック成功、15ファイル158成功 |
| 実ウィンドウ＋隔離した実サービスsmoke | [result.json](result.json)の18シナリオ成功 |
| Bridge入口pure-canary | [bridge-result.json](bridge-result.json)の6ケース成功 |
| git diff --check | 成功 |

再現:

```powershell
dotnet run --project tests/Ferry.StartupSmoke/Ferry.StartupSmoke.csproj -c Release -- --regressions --output docs/verification/gogo-20261003
node docs/verification/gogo-20261003/bridge-canary.cjs
```

Bridgeの修正前は、100%と日本語の%入力がURIError/QR解析失敗、%2Fが/へ改名された。
修正後は両入口で名前・sidを保持。DOMイベントはno-op、fetchなしで実Bridgeソースを実行した。
UI検証の初回はテンプレート内部入力まで数えた検証コードの不備で2件失敗した。
用途対象11個という期待値は維持し、テンプレート内部部品を除外した後に成功した。

新しい文脈のrere検証担当は前回の枠上限で未実施。今回はgogoの反証と、元担当による差分再確認を実施した。
macOSのOS合成結果・Dock復帰・VoiceOverは未実測。PNGはAvaloniaビジュアルツリーの描画でOSスクリーンショットではない。
設定以外15操作のアクセシビリティは静的確認。サービス検証は通信fixtureと実ファイルI/Oを使い、本番relay/NAT/暗号通信全体のE2Eではない。
UDPは実送信＋ACK入口のfixtureで、外部STUN/受信ループは対象外。

今回の最終smokeで作成した一時ディレクトリ11個はresult.jsonに絶対パスを記録し、全削除を確認した。
検証プロセスは終了済み。利用中のFerryプロセスには操作していない。前回からの生成物は保持した。

## 残る方式変更の具体案

裸のgogoはローカル修正・検証まで。以下は永続方式またはプロトコルの契約変更を含むため、実装していない。
本番移行・schema適用・deployは、ローカル実装の許可とは別工程とする。

1. **認証鍵の登録正本**: 既存D1にdeviceId主キーの鍵束縛表を設け、条件付きINSERT後の正本照合に成功した要求だけへtokenを発行する。KVへの旧初回書込みを停止し、既存束縛を移行・照合してから新登録を開く。lazy importはnegative cacheの移行窓があり採用しない。停止時の応答・移行完全性の確定とダミー移行検証が必要。
2. **ペアリング通知outbox**: pairs/nonceの同じD1 batchへ宛先別配送予定を記録し、DeviceDOのinbox保存成功で消費する。本人presence heartbeat/inbox接続で未配送を再試行。初回createdAtは固定し、unpair後のstale配送を無効化する。commit後中断・保存後応答消失・重複配送・unpair競合をダミーで検証する。
3. **受信結果の確認**: ACKの既存34byte prefixを保ちTransferIdを末尾へ追加し、FileApprove末尾で対応を交渉する。対応peerは検証結果まで送信完了を確定しない。旧peerの表示・待ち期限、最大1TBの検証時間を先に確定する。既存60秒を単純流用しない。
4. **フォルダーバッチ識別**: FileMetaにoptionalなFolderBatchIdを加え、同じ送信操作のメタに共通IDを渡す。保存先は(peer, batch, root)で対応させ、完了・取消・切断で寿命を管理する。旧メタは今回の到着済みpending単位の互換経路を維持。11件以上の送信窓と同root別バッチを検証する。

KVの同時書込み・negative cacheはCloudflare公式資料を2026-10-03に確認した:
[write](https://developers.cloudflare.com/kv/api/write-key-value-pairs/)、[read](https://developers.cloudflare.com/kv/api/read-key-value-pairs/)。

## 2026-11-01：4 残件の再確認

利用者から 4 ID の修正・検証を明示依頼され、現在のコードで反証してから実装した。以下をこれらの ID の現在の結論とする。基準は `a243794b78287eb909e8cd2cc00221fbd4487f6b`。版は 1.0.81 を維持し、変更は未コミット、本番操作は行っていない。

| ID | 採否・状態 | 解消内容と保全条件 |
| --- | --- | --- |
| RELAY-01 | 採用・ローカル検証済み | 認証鍵を既存 D1 の不可変 PK に登録し、並行登録の勝者を正本で照合する。KV 自動 seed はせず、未 import / 障害は 503、確定した不一致だけ 401。承認 snapshot の SHA・件数・全行・競合を確認して明示 ready にする移行 CLI を用意した |
| RELAY-03 | 採用・ローカル検証済み | nonce / pairs / 宛先別 outbox を同一 D1 batch に保存。永続受理後だけ消費し、片側失敗は元の payload・1 時間 TTL で再送。解除・再成立の revision を DeviceDO の保存 / flush でも照合する。本人検索は複合 index を利用する |
| CORE-04 | 採用・検証済み | 承認時の試行 token echo で交渉し、peer / TransferId / token / 成功 hash の一致後だけ送信完了。旧 17 / 34 byte prefix を維持する。結果不明は自動再送せず重複保存を防ぎ、取消・切断・Resume の終端と遅延 ACK を処理する |
| CORE-05 | 採用・検証済み | 送信操作の FolderBatchId で保存先を分離し、未着後続を終了通知まで共有する。active / pending 保持、30 分の孤立回収、件数上限、旧メタ互換を確認。実 UI で並列枠 10 を越す 11 ファイル、別バッチ、旧形式の計 13 ファイルを検証した |

追加で、エラー詳細更新時の `StateText` 通知漏れを実画面で再現し、通知 1 属性の修正後に同じ経路で解消を確認した。結果不明の説明は 18 言語へ同期した。

- 最終 build は警告 / エラー 0、.NET は 469 / 469 成功、relay は型検査成功・168 / 168 成功。Win x64 Native AOT 発行も成功（既存 Velopack ShellLink の Invalid IL 診断は継続）。
- [実画面の最終結果](../backlog-20261101-final/result.json) は 23 / 23 成功、一時領域 15 / 15 回収。今回の PNG 15 枚と、表示修正前の最小証拠を保存した。
- [Windows](../backlog-20261101-final/relay-windows-evidence.json) / [Linux ARM64](../backlog-20261101-final/relay-linux-evidence.json) で実 workerd・D1・WebSocket を通し、同一 ID の別鍵並行登録、transaction rollback、片側通知失敗からの再送、解除 / 再成立 / 古世代排除を確認。現 source 40 ファイルの SHA-256 が両検証コピーと一致した。
- 移行 CLI の実 `--local` 回帰は import 成功 → 競合拒否 → activate 成功。remote の SELECT 経路は実使用 Wrangler 4.156.0 の実装と独立照合した。コード・検証結果の修正後独立レビューは core / relay とも通過。

本番の承認 snapshot 取得・remote import・schema 適用・deploy は別工程であり未実行。[運用手順](../../operations/runbook.md#d1-スキーマの適用と-rollback) と [移行手順](../../../infra/cloudflare/relay/README.md) に従う。RELAY-03 の保証は元通知の 1 時間 TTL 内の再送であり、既存クライアントの watch 時刻制限や永続 pairId 重複除外を越える復元、旧バイナリとの実機対向、実 NAT 経路を新たに保証するものではない。
