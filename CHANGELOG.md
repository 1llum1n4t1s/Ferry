# 変更履歴

Ferry の利用者から見える変更点をまとめます。書式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に準拠し、バージョンは [セマンティック バージョニング](https://semver.org/lang/ja/) に従います。

配布物は Cloudflare R2（<https://ferry.kagayoi.com>）から配信され、アプリ内の自動更新で最新版へ移行します。内部リファクタ・テスト・CI・開発環境まわりの変更は原則として記載していません。

## 1.0.78 - 2026-08-29

### セキュリティ

- リレー接続で有効な認証トークンを必須化し、ペア当事者の確認、月次・セッションごとの転送量・接続時間・メッセージ数上限、同時接続上限、緊急停止を追加しました。認証情報を送らない旧版は移行用の小さい互換枠に制限されます
- 相手から通知された TCP / UDP 接続先を検証し、ループバック、クラウドメタデータ、マルチキャストなど PC 間接続ではない宛先を拒否するようにしました
- 受信を承認するまでディスクへファイルやフォルダを作らず、送信元別と全体の承認待ち数に上限を設けました

### 変更

- P2P の TCP / UDP 直接接続を優先する設計は維持し、Cloudflare リレーを使用量が強制的に制限される最終手段にしました
- プレゼンス確認と着信通知を正式ペアに限定し、着信 WebSocket と各 API に入力サイズ・レート・同時接続制限を追加しました

## 1.0.77 - 2026-08-23

### 修正

- 複数の宛先を登録しているとき、1 台の削除・切断・選択解除によって別の相手の接続監視や転送まで停止する問題を修正しました
- 壊れたファイルメタデータを受信したとき、送信側へ即座に拒否を返さずタイムアウトまで待たせる問題を修正しました
- 接続のキャンセルやリレー接続の失敗後に内部の待機・通信資源が残り、再接続できなくなることがある問題を修正しました
- Windows 起動時のファイアウォール確認・追加処理が長時間応答しないことがある問題を修正しました

## 1.0.76 - 2026-08-11

### セキュリティ

- WebSocket リレーの入室受付に形式検証と IP 単位のレート制限を追加しました。認証情報を伴わない入室要求で中継サーバーを大量に起動させる攻撃を防ぎます

### 変更

- アプリアイコンを刷新しました

## 1.0.75 - 2026-08-08

### セキュリティ

- QR ペアリング成立処理の並列リクエスト耐性を強化しました。従来は同じ QR の nonce に対する同時リクエストが両方通ってしまう理論上の隙があり、単回使用の保証がサーバー側で完全ではありませんでした
- 暗号ハンドシェイクが未確立の状態で、切断処理と送信処理が競合すると通信が平文で送出されうる隙を塞ぎました（ペア鍵を持つ接続では該当時に送信を中止するよう変更）

### 修正

- サーバー側の一時的な切断（メンテナンスや再起動）が続いた際、着信通知の再接続処理が待機なしで即座に繰り返され、サーバーへの負荷が上がる問題を修正しました
- 着信検知の低頻度ポーリングが、通知経路の切断が長時間続いた場合にサーバーへ余分な負荷をかけていた問題を修正しました

## 1.0.74 - 2026-08-06

### セキュリティ

- ペア鍵を保有する接続で、通信経路上の第三者がフレームを 1 通注入する、またはハンドシェイクを妨害するだけで端末間暗号が無効化され平文通信へ降格しうる問題を修正しました。ハンドシェイクが成立しない場合は平文へ降格せず接続を切断します（fail-closed 化）。公開鍵交換前の古いペアはこれまでどおり平文で接続できます
- QR ペアリングで使う nonce をサーバー側で単回使用にしました。従来はペア成立後もクライアント側の取り消し処理任せで、相手 PC がオフラインだと nonce が有効期限（1 時間）いっぱい再利用可能なまま残り、撮影・流出した QR コードから再度ペアを張れる状態でした
- リレーの依存パッケージ undici の脆弱性 5 件（重要度 high 1 件 / moderate 4 件）を解消しました

### 変更

- ウィンドウのアクリル背景の設定を簡略化しました

## 1.0.73 - 2026-07-28

### 修正

- 接続の確立中にファイル送信を実行すると、確立途中の接続が破棄されて「相手から応答がありません」で必ず失敗する不具合を修正しました。確立中の接続がある場合は破棄せず完了を待って相乗りします

### 変更

- README を利用者向け（ダウンロードと使い方）に整理し、技術スタック・構成・ビルド手順を `CONTRIBUTING.md` と `docs/architecture.md` へ分離しました

## 1.0.72 - 2026-07-28

### 修正

- v1.0.70 以降、シグナリングのレート制限枠が実際の消費量に対して不足しており、送信側が自分自身を 429 で締め出して送信が必ず失敗する不具合を修正しました（シグナリング専用の枠を新設）
- デバッグ起動時に開発者ツールが二重に接続され、アプリが起動できなくなる問題を修正しました
- 左右ペインの仕切りの見た目の隙間を 10px から 3px へ縮小しました

## 1.0.71 - 2026-07-27

### 追加

- 右横書き（RTL）のロケール（ヘブライ語）を選んだとき、ウィンドウのレイアウト方向が切り替わるようになりました

### 変更

- 転送の失敗理由・接続エラー・コード貼付ペアリングの結果を 18 言語のロケール辞書経由に変更しました。従来はこれらの文言だけ日本語のまま表示されていました
- 高帯域・高遅延の経路で転送速度が頭打ちになる問題を解消しました（TCP の受信ウィンドウ自動調整に委ねるよう変更）

### 修正

- `settings.json` が消失したときに、副本から端末 ID を復元するようにしました。従来は通知もなく新しい ID が採番され、登録済みのペアがすべて失われていました
- 相手から届く拒否理由の文字列をサニタイズするようにしました（制御文字の除去と長さ制限）

## 1.0.70 - 2026-07-26

### 変更

- 転送履歴を仮想化リストへ移行し、履歴が増えても描画負荷が上がらないようにしました
- ペインの影表現を見直し、転送リストを含むペインが毎フレーム再合成される問題を解消しました
- ファイル名のスクロール表示を全体で 1 つのタイマーに集約し、非表示のときは動作を停止するようにしました
- 全 18 言語のキー集合を統一し、経路バッジなどに直書きされていた日本語をロケール辞書経由に変更しました。言語を切り替えると表示が追従します

### セキュリティ

- ペア成立時に配られる公開鍵を、サーバーが保持する権威データから引き直すようにしました
- UDP 経路に送信元の検証を追加し、接続確立後のエンドポイント差し替えと偽装パケットを拒否するようにしました
- 暗号ハンドシェイク失敗時の切断を該当ペアだけに限定しました。従来は 1 ペアの失敗で他ペアの転送まで巻き添えで切断されていました
- `identity.key` と `peers.json` のパーミッションを所有者のみ読み書き可（0600）に制限しました（macOS / Linux）

### 修正

- 接続要求の鮮度判定をサーバー時刻基準に変更し、端末間の時計ズレによる接続失敗を解消しました
- 認証トークンが期限切れで拒否されたとき、即座に取り直すようにしました
- WebSocket に無通信タイムアウトを設定し、経路が黙って切れた状態を検出できるようにしました。従来は受信中に経路が失われると画面が「転送中」のまま固まることがありました

## 1.0.69 - 2026-07-26

### 変更

- 配信ドメインを `nephilim.jp` から `kagayoi.com` へ移行しました
- Windows のタスクバーでアイコンが正しくグループ化されるようにしました

### 修正

- 認証リクエストが共有の HTTP クライアントを使っておらず、最大 100 秒応答しないことがある問題を修正しました。接続確立の待ち時間が空振りし、リレー経路への転落や「相手から応答がありません」として現れていました
- リレー経路の受信バッファが不足し、すべてのチャンクが低速な処理経路に落ちていた問題を修正しました
- 切断処理が通信路に伝わらず、再接続が「ペアが埋まっています」で弾かれる問題を修正しました
- 受信時にチャンクごとに書き込みバッファが破棄されていた問題を修正し、書き込み回数を削減しました
- ペアリング時のサーバー側の書き込みが不可分でなく、「QR は表示されるのにペアリングだけ通らない」中間状態が残ることがある問題を修正しました

## 1.0.68 - 2026-07-03

### 追加

- IPv6 での TCP 直接接続に対応しました。IPv4 が CGNAT 配下でも、双方が IPv6 で到達できればリレーを経由せず直結できます

### 修正

- IPv6 が無効な環境でのフォールバックが働かない問題を修正しました
- 複数ファイルを送信したときの同時接続の競合と、ネットワークエラーの誤分類を修正しました
- TCP のソケットリーク（接続側・待ち受け側の両方）を修正しました
- 転送をキャンセルした行が上書きされてしまう問題を修正しました
- 確認ダイアログに未翻訳の文言が残っていた問題を修正しました

### 削除

- マルチストリーム転送の試験実装を撤去しました

## 1.0.67 - 2026-07-02

### 変更

- 着信の検知を常時ポーリングから「接続ノック」（サーバーからの WebSocket push）へ移行しました。検知はミリ秒オーダーになり従来のポーリングより高速化し、待機中のサーバーリクエストは 1 ペアあたり 1 日 20 万件規模から 90% 以上削減されています

## 1.0.66 - 2026-07-01

### 追加

- 宛先リストに検索フィルタ、並び替えの切替、オンライン / オフラインの区切り、ピン留めを追加しました
- PC 同士のコード貼付ペアリング（QR コードを介さない直接ペアリング）を復活させました

### 削除

- Firebase を完全に撤去し、シグナリング・プレゼンス・ペアリングを Cloudflare へ一本化しました

> ⚠️ **破壊的変更**: v1.0.64 以前のバイナリは以後シグナリングできません。v1.0.65 以降を使っていれば自動更新で移行されます。長期間起動していなかった端末で接続できない場合は、最新版を再インストールしてください。

## 1.0.65 - 2026-06-29

### 変更

- シグナリングの既定を Cloudflare へ切り替え、既存の端末は一度きりの自動移行で追従するようにしました
- 並列転送数を 1〜10 の範囲で設定できるようにしました（既定は 1）

### 修正

- ウィンドウ位置の保存に失敗することがある問題を修正しました

## 1.0.64 以前

詳細な変更履歴はこのファイルの整備前のため、リポジトリのコミット履歴を参照してください。主なマイルストーンは次のとおりです。

| バージョン | 時期 | 内容 |
|---|---|---|
| 1.0.64 | 2026-06-23 | 複数ペアの同時接続に対応 |
| 1.0.52 | 2026-06-12 | Windows 版の配布物へのコード署名を開始 |
| 1.0.48 | 2026-06-07 | 端末間暗号を常時有効化、帯域制限・並列転送・保存先バーを追加 |
| 1.0.47 | 2026-06-07 | 宛先別の転送履歴、送信操作（再送 / 一時停止 / キャンセル）、多重起動防止を追加 |
| 1.0.46 | 2026-06-06 | リレー経路にフロー制御を導入し、大容量ファイルが約 55 秒で切断される問題を解消 |
| 1.0.43 | 2026-05-31 | バックエンドを Cloudflare Workers へ移行 |
| 1.0.38 | 2026-05-29 | ファイル受信の承認プロトコルを導入 |
| 1.0.0 | 2026-03-14 | 初回リリース |

## [1.0.63] — Git 記録日: 2026-06-23

- Cloudflare を使うペアリング・在席確認・接続情報の交換を追加し、クライアントから従来経路と切り替えられるよう対応。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/32c66d4092574ca80ceab62770eefb22e4638bc2) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/545951b1a5f7e2ab7c6012b7752c0f046b1ed8fe...32c66d4092574ca80ceab62770eefb22e4638bc2)。

## [1.0.62] — Git 記録日: 2026-06-20

- Firebase のカスタムトークン認証を導入し、ペアリング情報の管理を統一。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/545951b1a5f7e2ab7c6012b7752c0f046b1ed8fe) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/2922de8f24a4ae257ecd7c277ceadc589443a370...545951b1a5f7e2ab7c6012b7752c0f046b1ed8fe)。

## [1.0.61] — Git 記録日: 2026-06-19

- UDP 接続を双方向の疎通確認後に確立し、一方向しか通信できない状態を接続済みとする問題を修正。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/2922de8f24a4ae257ecd7c277ceadc589443a370) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/2b1b955b7406ba0634dc44a511f52b1cf960d3d2...2922de8f24a4ae257ecd7c277ceadc589443a370)。

## [1.0.60] — Git 記録日: 2026-06-18

- 任意に有効化できるエンドツーエンド暗号化を追加。既定では無効。
- 転送再開・キャンセルの競合と受信バッファの容量制限を改善。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/2b1b955b7406ba0634dc44a511f52b1cf960d3d2) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/06aa2b1d97182d107c9fa5f4d364c902eae0cc15...2b1b955b7406ba0634dc44a511f52b1cf960d3d2)。

## [1.0.59] — Git 記録日: 2026-06-17

- 転送速度の平均表示・転送行UI統一・オフライン無応答時のリトライ抑止

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/06aa2b1d97182d107c9fa5f4d364c902eae0cc15) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/27f863d11976276034a75d11ac1bce646b7d1d2f...06aa2b1d97182d107c9fa5f4d364c902eae0cc15)。

## [1.0.58] — Git 記録日: 2026-06-17

- キャプションボタンへのピンク波及修正とトレイ復帰時の最前面化

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/27f863d11976276034a75d11ac1bce646b7d1d2f) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/39fa9ba8654e61032ad162da99f634f61970d9a7...27f863d11976276034a75d11ac1bce646b7d1d2f)。

## [1.0.57] — Git 記録日: 2026-06-17

- 接続の無応答ハング修正とリレー待ち短縮

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/39fa9ba8654e61032ad162da99f634f61970d9a7) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/ea8fd50050df28cc5f7c4a108315d63ebdcacc1a...39fa9ba8654e61032ad162da99f634f61970d9a7)。

## [1.0.56] — Git 記録日: 2026-06-17

- VelopackUpdateDialog.Avalonia を 1.0.10 へ更新
- mac/ライトテーマ UI 修正 + rere/PR レビュー指摘の是正 (#8)

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/ea8fd50050df28cc5f7c4a108315d63ebdcacc1a) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/c4b5bafe6c2ef886424b9fcef045c47e7b499b23...ea8fd50050df28cc5f7c4a108315d63ebdcacc1a)。

## [1.0.55] — Git 記録日: 2026-06-14

- 配布用のバージョン情報と対応する README の表記を更新。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/c4b5bafe6c2ef886424b9fcef045c47e7b499b23) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/d1b068838b34b172d51632f582877fa88c167c9b...c4b5bafe6c2ef886424b9fcef045c47e7b499b23)。

## [1.0.54] — Git 記録日: 2026-06-14

- マルチプラットフォーム対応の強化と接続/受信まわりの不具合修正 (#7)
- csproj の Version 重複を削除し Directory.Build.props 単一管理に
- macOS 公証を app-specific password 方式に変更
- macOS notarytool 失敗の診断ログ追加 + matrix fail-fast: false

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/d1b068838b34b172d51632f582877fa88c167c9b) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/fb599fed74a1d6fc5150787e6338ed7680f4f24c...d1b068838b34b172d51632f582877fa88c167c9b)。

## [1.0.53] — Git 記録日: 2026-06-13

- ランディングページの macOS/Linux DLリンクを実ファイル名に修正
- vpk pack に --icon を追加 (Setup.exe にアイコンが入っていなかった)
- R2 レスポンスが byte[] になるケースに対応 (UTF-8 デコード + keep set 形式検証 + CDN キャッシュバイパス)

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/fb599fed74a1d6fc5150787e6338ed7680f4f24c) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/152cd165ff2685188182620f76486c597fcfdb58...fb599fed74a1d6fc5150787e6338ed7680f4f24c)。

## [1.0.52] — Git 記録日: 2026-06-12

- Windows 配布物のコード署名とローカルリリース方式を導入し、自動更新ダイアログと配布ツールを更新。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/152cd165ff2685188182620f76486c597fcfdb58) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/75af72bed98181d19019cce756d000436851b848...152cd165ff2685188182620f76486c597fcfdb58)。

## [1.0.51] — Git 記録日: 2026-06-11

- 接続情報の取得回数と転送時の不要な割り当てを減らし、受信ファイルのハッシュ判定と状態通知を改善。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/75af72bed98181d19019cce756d000436851b848) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/d183b6424aaf4efbbf186441c37cdaae46b9ae51...75af72bed98181d19019cce756d000436851b848)。

## [1.0.50] — Git 記録日: 2026-06-11

- review: ロケール 7 ファイルの新キーを各言語に翻訳 + Answer 側 UDP のキャンセル伝播を対称化
- キャンセル時の偽 State=Connected 残留と transport リークを修正
- Lhamiel 互換のランディングページを追加（ferry.nephilim.jp）

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/d183b6424aaf4efbbf186441c37cdaae46b9ae51) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/12a30136f94f3460d5dd71b3bf1231d58d25b4b9...d183b6424aaf4efbbf186441c37cdaae46b9ae51)。

## [1.0.49] — Git 記録日: 2026-06-08

- 保存先 UI を設定画面に戻し、テーマ ComboBox / X ボタン挙動を修正

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/12a30136f94f3460d5dd71b3bf1231d58d25b4b9) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/7b8ff70095bcdae0e2e7f16a4097bc7f4b9dbf19...12a30136f94f3460d5dd71b3bf1231d58d25b4b9)。

## [1.0.48] — Git 記録日: 2026-06-07

- 帯域制限 / 並列転送 / 保存先バー下部移設 / ピンク統一ボタン

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/7b8ff70095bcdae0e2e7f16a4097bc7f4b9dbf19) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/7adf1e2d0957af57b01c12f2fbad6a5f42fd2a95...7b8ff70095bcdae0e2e7f16a4097bc7f4b9dbf19)。

## [1.0.47] — Git 記録日: 2026-06-07

- 承認待ち・一時停止・接続断からの再試行と、キャンセル時の状態管理を修正。受信フォルダーを開く操作を保存先バーへ統一。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/7adf1e2d0957af57b01c12f2fbad6a5f42fd2a95) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/703092553516ffee2462998b38739683a665691d...7adf1e2d0957af57b01c12f2fbad6a5f42fd2a95)。

## [1.0.46] — Git 記録日: 2026-06-06

- リレー経路のフロー制御追加 + UI テーマ刷新

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/703092553516ffee2462998b38739683a665691d) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/a0694958cb822519cd2aad972d9ec0bd8cdf95a9...703092553516ffee2462998b38739683a665691d)。

## [1.0.45] — Git 記録日: 2026-06-06

- ウィンドウ終了挙動と接続/転送の不具合修正

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/a0694958cb822519cd2aad972d9ec0bd8cdf95a9) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/05fe28e91e1a016f7d145c01b5fed0f7dbd9b39e...a0694958cb822519cd2aad972d9ec0bd8cdf95a9)。

## [1.0.44] — Git 記録日: 2026-06-01

- VelopackUpdateDialog.Avalonia を 1.0.5 に更新

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/05fe28e91e1a016f7d145c01b5fed0f7dbd9b39e) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/678f54a2a433f531c276ecb834dd7e0f6a7b9072...05fe28e91e1a016f7d145c01b5fed0f7dbd9b39e)。

## [1.0.43] — Git 記録日: 2026-05-31

- pairing watch を成立確定まで維持 + 削除ピアの着信監視停止
- 起動時自動ペアリングの多重実行防止 + サイドバー幅復元のクランプ
- タブ切替時に直前ピアの着信監視を維持する
- サイドバーをタブグループ化しペアリング追加を右ペインへ統合
- generate_icon.ps1 を UTF-8 BOM 付きで保存

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/678f54a2a433f531c276ecb834dd7e0f6a7b9072) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/ee853d85dceea95fa0d7cb4a900af872fe66fc80...678f54a2a433f531c276ecb834dd7e0f6a7b9072)。

## [1.0.42] — Git 記録日: 2026-05-30

- アプリアイコン差し替え

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/ee853d85dceea95fa0d7cb4a900af872fe66fc80) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/7fafc3a4247a8a9db0b65cba5c353897848b43c6...ee853d85dceea95fa0d7cb4a900af872fe66fc80)。

## [1.0.41] — Git 記録日: 2026-05-29

- 設定画面のスクロール見切れ修正

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/7fafc3a4247a8a9db0b65cba5c353897848b43c6) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/a6a262543c6fb232b9291de2586fa34d5acf90b7...7fafc3a4247a8a9db0b65cba5c353897848b43c6)。

## [1.0.40] — Git 記録日: 2026-05-29

- VM ライフサイクル一括整理 + 設定 UI クリーンアップ
- Lhamiel 風バージョン UI に拡張 (チェック中ボタン無効化 + スキップ取消)

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/a6a262543c6fb232b9291de2586fa34d5acf90b7) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/87f57f3aadadfa1d1af4246652891ddf681bafdf...a6a262543c6fb232b9291de2586fa34d5acf90b7)。

## [1.0.39] — Git 記録日: 2026-05-29

- ドキュメント更新
- VelopackUpdateDialog.Avalonia 1.0.4 + 依存更新でビルドエラー解消

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/87f57f3aadadfa1d1af4246652891ddf681bafdf) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/06a06b488599821accd5a09bf62ddbcca64ab160...87f57f3aadadfa1d1af4246652891ddf681bafdf)。

## [1.0.38] — Git 記録日: 2026-05-29

- 転送前の承認手順を導入し、接続・転送の操作性を改善。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/06a06b488599821accd5a09bf62ddbcca64ab160) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/f99df65e9b54fb4a75c798b76a37a552011a2abf...06a06b488599821accd5a09bf62ddbcca64ab160)。

## [1.0.37] — Git 記録日: 2026-05-27

- 依存ライブラリ更新

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/f99df65e9b54fb4a75c798b76a37a552011a2abf) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/d876896fbe2f92f61104ae6b51b4c5254b34d845...f99df65e9b54fb4a75c798b76a37a552011a2abf)。

## [1.0.36] — Git 記録日: 2026-05-27

- ペアリング検知漏れ修正 + UI 不具合 3 件修正

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/d876896fbe2f92f61104ae6b51b4c5254b34d845) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/4417c13547132e4a44224c8c7736472f953ac3d8...d876896fbe2f92f61104ae6b51b4c5254b34d845)。

## [1.0.35] — Git 記録日: 2026-05-27

- WebSocket リレーを Cloudflare Workers + Durable Objects に移行

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/4417c13547132e4a44224c8c7736472f953ac3d8) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/9f821f008c12f851d5b252809820a6ee9bb9c7d7...4417c13547132e4a44224c8c7736472f953ac3d8)。

## [1.0.34] — Git 記録日: 2026-05-25

- カメラなし PC ペアリング対応 + Firebase deploy 自動化 + フロー整理

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/9f821f008c12f851d5b252809820a6ee9bb9c7d7) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/d28f94dd7af2995ce3c35172df41d795e806b943...9f821f008c12f851d5b252809820a6ee9bb9c7d7)。

## [1.0.33] — Git 記録日: 2026-05-25

- 接続経路バッジ表示 + Bridge URL 貼り付け経路追加

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/d28f94dd7af2995ce3c35172df41d795e806b943) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/3395271c111813acc18c9fe13dd7a492316bfafc...d28f94dd7af2995ce3c35172df41d795e806b943)。

## [1.0.32] — Git 記録日: 2026-05-24

- 自動更新ダイアログを導入し、接続・転送処理の安全性とメモリ管理を改善。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/3395271c111813acc18c9fe13dd7a492316bfafc) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/2318601039f087eb3eeb9bfeb9c7c07bd520f882...3395271c111813acc18c9fe13dd7a492316bfafc)。

## [1.0.31] — Git 記録日: 2026-05-21

- 受信先のパス検証、転送チャンクの識別と位置指定書き込み、キャンセル・再開処理を強化。
- 設定の保存を保護し、版情報を一元化。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/2318601039f087eb3eeb9bfeb9c7c07bd520f882) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/72826732769740491c80b35256e9d5f741fde0ec...2318601039f087eb3eeb9bfeb9c7c07bd520f882)。

## [1.0.30] — Git 記録日: 2026-03-20

- 画面を Avalonia のネイティブ UI へ移行し、外観・アクセント色を調整。WebView の残留参照とプラットフォーム別のビルドを修正。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/72826732769740491c80b35256e9d5f741fde0ec) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/ab3cfe9057e191fadc22fcd11d1ee440197c88ae...72826732769740491c80b35256e9d5f741fde0ec)。

## [1.0.28] — Git 記録日: 2026-03-19

- チャット・ファイル転送・UI の大幅改善
- 🚀 チャット機能大幅強化: 33機能を一括実装
- 🐛 メッセージ送信エラーログを詳細化（接続失敗時に原因を記録）
- チャットUI・暗号化履歴・設定改善（WebView移行準備）

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/ab3cfe9057e191fadc22fcd11d1ee440197c88ae) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/36872072a1df63867a634ece4e28d30dec7ef96c...ab3cfe9057e191fadc22fcd11d1ee440197c88ae)。

## [1.0.26] — Git 記録日: 2026-03-18

- UI大幅改善（転送履歴・受信通知・設定保存・ステータス表示）

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/36872072a1df63867a634ece4e28d30dec7ef96c) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/68f629bf8ed750ee3f487f648d2b78a81e13a126...36872072a1df63867a634ece4e28d30dec7ef96c)。

## [1.0.24] — Git 記録日: 2026-03-18

- UX 大幅改善（接続ステータス詳細化・転送通知・トレイメニュー・名前同期）

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/68f629bf8ed750ee3f487f648d2b78a81e13a126) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/422c48533387015c5fb847aac832a3f9d4fbba4f...68f629bf8ed750ee3f487f648d2b78a81e13a126)。

## [1.0.22] — Git 記録日: 2026-03-17

- 暗い背景レイヤーを復元（IsHitTestVisible=False付き）

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/422c48533387015c5fb847aac832a3f9d4fbba4f) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/965409a80206f71aa3878cc64410032f3819ed13...422c48533387015c5fb847aac832a3f9d4fbba4f)。

## [1.0.20] — Git 記録日: 2026-03-17

- 不要な背景レイヤーを削除

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/965409a80206f71aa3878cc64410032f3819ed13) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/3ee6e9cd14208a5ed5935616687bf08e1045a551...965409a80206f71aa3878cc64410032f3819ed13)。

## [1.0.18] — Git 記録日: 2026-03-17

- ウィンドウドラッグ移動できない問題を修正

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/3ee6e9cd14208a5ed5935616687bf08e1045a551) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/c2e0b32a1c90f458e34ff17bb7812ae0e45d112b...3ee6e9cd14208a5ed5935616687bf08e1045a551)。

## [1.0.16] — Git 記録日: 2026-03-17

- リリースビルドの白画面問題を修正

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/c2e0b32a1c90f458e34ff17bb7812ae0e45d112b) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/b16b13e3b03f2d7378f05f2e7b12cf54d95d0061...c2e0b32a1c90f458e34ff17bb7812ae0e45d112b)。

## [1.0.14] — Git 記録日: 2026-03-17

- v1.0.14 にバージョン更新
- Ferry-releases のリリースをインストーラーと自動更新用に分離
- プライベートリポへのリリース作成を廃止

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/b16b13e3b03f2d7378f05f2e7b12cf54d95d0061) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/371b16efdab8c673a3f8898987e7db2c73e6b471...b16b13e3b03f2d7378f05f2e7b12cf54d95d0061)。

## [1.0.12] — Git 記録日: 2026-03-17

- Ferry-releases にインストーラーも配布

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/371b16efdab8c673a3f8898987e7db2c73e6b471) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/59b86c2fe66931dc229136846bdc1d8db406e71a...371b16efdab8c673a3f8898987e7db2c73e6b471)。

## [1.0.10] — Git 記録日: 2026-03-17

- v1.0.10 にバージョン更新
- Ferry リポジトリから Velopack リリースを廃止、Ferry-releases に一本化
- 更新チェックを public リポジトリ Ferry-releases 経由に変更

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/59b86c2fe66931dc229136846bdc1d8db406e71a) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/7449f3803ea8d0ff4f125fa0ea63e8ab91b4cdcc...59b86c2fe66931dc229136846bdc1d8db406e71a)。

## [1.0.8] — Git 記録日: 2026-03-17

- 自動更新画面を閉じたときの資源解放を修正し、更新失敗の情報保持とファイル選択処理を整理。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/7449f3803ea8d0ff4f125fa0ea63e8ab91b4cdcc) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/7154d457dec743b01cfce034366dd1c15cb819e6...7449f3803ea8d0ff4f125fa0ea63e8ab91b4cdcc)。

## [1.0.6] — Git 記録日: 2026-03-17

- 多言語対応・Tahoe UI・サイドバーメニュー・トレイ最小化改善

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/7154d457dec743b01cfce034366dd1c15cb819e6) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/163e9d282bcf9bd0deaba2d4960740c1beb053a1...7154d457dec743b01cfce034366dd1c15cb819e6)。

## [1.0.4] — Git 記録日: 2026-03-17

- フォルダー転送に対応し、トレイアイコンと最小化時の動作を修正。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/163e9d282bcf9bd0deaba2d4960740c1beb053a1) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/493342f1692bb7e65d1e15ec2efcca5bd0fcc68d...163e9d282bcf9bd0deaba2d4960740c1beb053a1)。

## [1.0.2] — Git 記録日: 2026-03-17

- 接続を TCP 直接通信と WebSocket リレーへ変更し、リレー URL の設定・転送画面・ペアリングリンク共有を追加。

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/493342f1692bb7e65d1e15ec2efcca5bd0fcc68d) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/f2a579756ca68947fefae7cd2d57027ec90f041a...493342f1692bb7e65d1e15ec2efcca5bd0fcc68d)。

## [1.0.0] — Git 記録日: 2026-03-14

- UI/UX 改善・バグ修正・Bridge ページ刷新
- Firebase 本実装・Bridge ページ刷新・接続経路表示・ゴミセッション自動削除

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/f2a579756ca68947fefae7cd2d57027ec90f041a) / [変更差分](https://github.com/1llum1n4t1s/Ferry/compare/accd25a54c467c05b46a7416c5a4334641e2b4e8...f2a579756ca68947fefae7cd2d57027ec90f041a)。

## [0.1.0] — Git 記録日: 2026-03-14

- オンデマンド接続・転送レジューム機能を実装

出典: [版の記録](https://github.com/1llum1n4t1s/Ferry/commit/accd25a54c467c05b46a7416c5a4334641e2b4e8)。
