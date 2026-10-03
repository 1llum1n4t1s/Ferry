# 起動時ウィンドウの実画面検証

本体と同じ Avalonia ネイティブバックエンド、AXAML、MainWindow の表示・最小化・復帰を実行する。
通信サービスは起動せず、設定はメモリ内だけに保持する。利用中の Ferry の設定・プロセスは変更しない。

検出する失敗を先に定義する:

- 起動時最小化が Show 完了前に適用され、ネイティブウィンドウへ反映されない。
- 通常起動でも最小化される。
- macOS で最小化が Dock に入らず Hide される。
- Windows/Linux のトレイ最小化でウィンドウが残る。
- 保存された最大化状態が起動時最小化を上書きする。
- 保存されたウィンドウ座標が初回 Show の中央配置で上書きされる。
- 復帰時に起動時最小化が再実行される。
- Hide→Show で変更後のウィンドウサイズが保存値へ巻き戻る。
- 復帰後の設定画面のコントロールが非表示、またはレイアウトが空になる。

```powershell
dotnet run --project tests/Ferry.StartupSmoke/Ferry.StartupSmoke.csproj -c Release -- --output docs/verification/window-startup-20261001
```

終了コード 0 が成功。OS、Avalonia バージョン、各ケースの状態と結果を `result.json`、
各復帰後の Avalonia ビジュアルツリーの描画を PNG として保存する。
PNG は OS のウィンドウ合成結果ではないため、macOS の空白表示の解消を確認するには
macOS 上でもこの検証と配布アプリの実起動・Dock 復帰を確認する必要がある。

## レビュー指摘の再現シナリオ

`--regressions` で、上記に加えて実 UI と実サービスの次の失敗条件を検証する。

- 保存済み `he_IL` が初回ウィンドウと後発確認画面に適用されない、言語変更に追従しない。
- 言語変更後もスキップ中バージョンの説明が古い、設定入力の読み上げ名が空。
- 空・途中切断・null の設定 JSON で、正常な副本があるのに DeviceId を再生成する。
- 複数ピア接続で、受信キャンセルの Reject が primary へ誤配送される。
- 書込み後の帯域制限待ち中に取消すると、遅れて送るFlowAckもprimaryへ誤配送される。
- 保存ファイル作成待ち中の切断・Reject・取消で、承認側が状態を再登録し部分ファイルを残す。
- 承認前に到着した旧形式のチャンクが、承認時の状態交換で失われる。
- 同じフォルダーの到着済み2件を個別承認すると保存先が分かれる。
- 別 peer の承認待ちが、次回の同名フォルダー送信を前回の保存先へ混在させる。
- UDP の利用者キャンセル後も送信枠が残り、後続の正常送信が停止する。

```powershell
dotnet run --project tests/Ferry.StartupSmoke/Ferry.StartupSmoke.csproj -c Release -- --regressions --output docs/verification/gogo-20261003
```

設定・受信はダミーデータ専用の OS 一時ディレクトリを作り、終了時に削除する。
受信サービスは通信を捕捉するfixtureを使用し、UDPは隔離したloopbackソケットで通常のDATAだけを送る。
承認競合は既存の保存ゲートで順序を固定する。UDPは接続の前処理をfixtureで設定し、
ACK入口を呼ぶため、STUN・実際のNAT・受信ループ全体を検証するものではない。
API互換や外部サービスへ接続せず、実利用者の設定・ペア・プロセスを操作しない。
