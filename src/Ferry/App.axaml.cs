using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Ferry.Infrastructure;
using Ferry.Services;
using Ferry.ViewModels;
using Ferry.Views;

namespace Ferry;

/// <summary>ロケール選択肢の表示用レコード。</summary>
public record LocaleItem(string Key, string DisplayName);

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private ResourceDictionary? _activeLocale;
    // rere #B2-001: 全ロケールのフォールバック先として常時マージしておく en_US 辞書。
    // 選択ロケールに欠損キーがあっても DynamicResource が空白にならず英語で表示される
    private ResourceDictionary? _baseLocale;
    private ISettingsService? _settingsService;

    /// <summary>TransferService のインスタンス（MainWindow からのアクセス用）。</summary>
    public ITransferService? TransferService { get; private set; }

    /// <summary>
    /// PairSyncService のインスタンス（MainWindow の visibility ハンドラから <see cref="PairSyncService.SetActive"/> を
    /// 呼ぶための公開ハンドル）。CodeRabbit 指摘で SetActive 配線が抜けていたのを補う。
    /// </summary>
    public PairSyncService? PairSyncService { get; private set; }

    /// <summary>PendingPairDelete 定期処理タスクの cancellation。終了時にキャンセルする。</summary>
    private System.Threading.CancellationTokenSource? _pendingDeleteCts;

    /// <summary>自動更新の配信元 URL（Cloudflare R2 ferry-updates 経由）。</summary>
    private const string UpdateBaseUrl = "https://ferry.nephilim.jp";

    // rere #D-004: Firebase DB / Bridge / Relay の各 URL は Ferry.AppConstants に一本化し、
    // settings.json からの書き換えを廃止した（改ざん面の対称化。UpdateBaseUrl と同方針）。

    /// <summary>サポートされているロケール一覧。</summary>
    public static readonly string[] SupportedLocales =
    [
        "en_US", "ja_JP", "zh_CN", "zh_TW", "de_DE", "fr_FR", "es_ES",
        "it_IT", "pt_BR", "ru_RU", "uk_UA", "id_ID", "fil_PH", "ta_IN", "ko_KR",
        "la_VA", "sa_IN", "he_IL"
    ];

    /// <summary>ロケール表示名（ネイティブ言語名）。</summary>
    public static readonly Dictionary<string, string> LocaleDisplayNames = new()
    {
        ["en_US"] = "English",
        ["ja_JP"] = "日本語",
        ["zh_CN"] = "简体中文",
        ["zh_TW"] = "繁體中文",
        ["de_DE"] = "Deutsch",
        ["fr_FR"] = "Français",
        ["es_ES"] = "Español",
        ["it_IT"] = "Italiano",
        ["pt_BR"] = "Português (Brasil)",
        ["ru_RU"] = "Русский",
        ["uk_UA"] = "Українська",
        ["id_ID"] = "Bahasa Indonesia",
        ["fil_PH"] = "Tagalog",
        ["ta_IN"] = "தமிழ்",
        ["ko_KR"] = "한국어",
        ["la_VA"] = "Latina",
        ["sa_IN"] = "संस्कृतम्",
        ["he_IL"] = "עברית עתיקה"
    };

    /// <summary>ロケール選択肢一覧。</summary>
    public static readonly LocaleItem[] LocaleOptions = SupportedLocales
        .Select(l => new LocaleItem(l, LocaleDisplayNames.GetValueOrDefault(l, l)))
        .ToArray();

    /// <summary>
    /// アプリの明示終了（トレイ「終了」/ Cmd+Q / OS シャットダウン）が進行中かどうか。
    /// macOS は赤信号ボタンの Close ではウィンドウを Hide するだけで終了しない設計のため、
    /// 「本当に終了したい」経路だけこのフラグを立て、MainWindow.OnClosing がそれを見て
    /// Hide ではなく実際の close を許可する（これが無いと mac でアプリを終了できない）。
    /// </summary>
    public static bool IsExplicitShutdown { get; private set; }

    public override void Initialize()
    {
        // macOS のメニューバーに表示するアプリ名を Avalonia の Application.Name 経由で固定する。
        // 旧実装は未設定で、bundle 化されていないビルドや一部経路で macOS が "Avalonia Application"
        // (Avalonia デフォルト) と表示してしまっていた。CFBundleName + Application.Name の両方に
        // "Ferry" を入れることで .app 起動 / dotnet run のどちらでもメニューバーが Ferry になる。
        Name = "Ferry";
        AvaloniaXamlLoader.Load(this);
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection used by Avalonia data validation plugins and ViewLocator")]
#pragma warning disable IL2046 // Avalonia の基底メソッドに属性が付与されていないため抑制
    public override void OnFrameworkInitializationCompleted()
#pragma warning restore IL2046
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // トレイ常駐アプリ: ウィンドウの Close では終了せず、トレイ「終了」(desktop.Shutdown) と
            // Velopack 再起動のみを終了経路にする。既定 OnLastWindowClose のままだと X ボタンで
            // 最終ウィンドウが閉じる → desktop.Exit → ConnectionService.Dispose で転送中 transport が
            // 切れてファイル転送が落ちる。X ボタンの挙動は MainWindow.OnClosing で MinimizeToTray に応じて
            // 明示制御する (ON=トレイ格納で転送継続 / OFF=desktop.Shutdown で終了)。
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // Cmd+Q / OS シャットダウン等のフレームワーク起点の終了要求でも明示終了フラグを立てる。
            // これで macOS の MainWindow.OnClosing が Hide ではなく実際の close を許可する
            // （トレイ「終了」は自前で desktop.Shutdown を呼ぶ経路でフラグを立てる）。
            desktop.ShutdownRequested += (_, _) => IsExplicitShutdown = true;

            // Avalonia 12 から DataAnnotationsValidationPlugin がデフォルト除外されたため、
            // 旧 11.x で必要だった DisableAvaloniaDataAnnotationValidation() は不要になった。

            // Windows ファイアウォールルールの確認・追加（初回のみ UAC ダイアログ表示）。
            // N-15: 非同期版に切替。Task.Run でスレッドプールを占有する必要なし、async I/O で軽く回す
            _ = FirewallHelper.EnsureFirewallRuleAsync();

            // サービス組み立て（コンストラクタで同期的に settings.json を読み込み、DeviceId を永続化）
            _settingsService = new SettingsService();
            var settingsService = (SettingsService)_settingsService;
            var settings = settingsService.Settings;
            // 接続先 URL は AppConstants に固定（settings からは撤去）。保存先のみ空なら既定値を補う。
            var needsSave = false;
            if (string.IsNullOrEmpty(settings.SaveDirectory))
            {
                settings.SaveDirectory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                needsSave = true;
            }
            if (needsSave)
            {
                // M-7: ContinueWith のレガシーパターンを async/await + try/catch に統一
                _ = SaveInitialSettingsAsync(settingsService);

                static async Task SaveInitialSettingsAsync(SettingsService svc)
                {
                    try { await svc.SaveAsync(); }
                    catch (Exception ex)
                    {
                        Util.Logger.Log($"初期設定の保存に失敗: {ex.Message}", Util.LogLevel.Warning);
                    }
                }
            }
            // 自動起動が有効なら、登録済みエントリを現在の実行ファイルパスへ冪等に更新する。
            // Velopack 更新等で実行パスが変わっても次回起動時に追従する（Win=レジストリ /
            // mac=LaunchAgent / Linux=.desktop を SetAutoStart 内で OS 別に再書込）。
            if (settings.AutoStartWithWindows)
                settingsService.SetAutoStart(true);

            // rere #D-001(b): 長期 ECDH 鍵（%APPDATA%\Ferry\identity.key）。QR の公開鍵交換と PairSecret 導出に使う。
            var deviceIdentity = Infrastructure.DeviceIdentity.CreateDefault();
            // #D-001a Phase B: Firebase Custom Token Auth クライアント。バックグラウンドで /auth/token に
            // 署名チャレンジ → idToken 取得 → 50min ごとに refresh。FirebaseSignaling のすべての REST に注入される。
            var firebaseAuthClient = new Infrastructure.FirebaseAuthClient(deviceIdentity, settings.DeviceId);
            // peerRegistry を IdentityLost ハンドラより前で宣言（lambda capture 解析のため）
            var peerRegistry = new PeerRegistryService();
            firebaseAuthClient.IdentityLost += (_, _) =>
            {
                Util.Logger.Log("identity.key 紛失イベントを検知 → clean slate UI を表示", Util.LogLevel.Warning);
                // UI スレッドで IdentityLostDialog を表示し、[やり直す] なら DeviceId + identity.key + peers.json を一括リセット。
                _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        var dialog = new Views.IdentityLostDialog();
                        var owner = GetMainWindow();
                        if (owner != null && owner.IsVisible)
                        {
                            await dialog.ShowDialog(owner);
                        }
                        else
                        {
                            // owner 不在時の dialog.Show() は非ブロックなので、Closed まで TCS で待つ。
                            // これが無いと ResetConfirmed が初期値 false のまま即座に評価され、
                            // [やり直す] が選ばれても clean slate が実行されない (CodeRabbit 指摘)。
                            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                            dialog.Closed += (_, _) => tcs.TrySetResult(true);
                            dialog.Show();
                            await tcs.Task;
                        }
                        if (!dialog.ResetConfirmed)
                        {
                            Util.Logger.Log("identity 紛失: ユーザーが [後で] を選択（オフラインモードで継続）");
                            return;
                        }
                        // clean slate 実行:
                        // 1. 新しい DeviceId を生成 + settings.json を上書き保存
                        settings.DeviceId = System.Guid.NewGuid().ToString("N");
                        await settingsService.SaveAsync();
                        // 2. identity.key を破棄して新規生成
                        var keyPath = System.IO.Path.Combine(
                            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                            "Ferry", "identity.key");
                        Infrastructure.DeviceIdentity.RegenerateAndSave(keyPath).Dispose();  // 新鍵をディスクに保存
                        // 3. peers.json を空にする（既存ペアは PairSecret も含めて全消去）
                        foreach (var p in peerRegistry.GetPairedPeers().ToList())
                            await peerRegistry.RemovePeerAsync(p.PeerId);
                        Util.Logger.Log("identity 紛失リカバリー完了 - アプリを終了します");
                        // 4. in-memory の deviceIdentity / firebaseAuthClient は古い鍵を保持しているため、
                        //    続行すると次回 /auth/token でまた DEVICE_PUBKEY_MISMATCH → ダイアログ無限ループに陥る。
                        //    確認ダイアログを表示してからプロセスを終了し、次回起動で新鍵で再構築させる。
                        await Views.IdentityLostDialog.ShowRestartRequiredAsync(GetMainWindow());
                        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                            desktop.TryShutdown(0);
                    }
                    catch (Exception ex)
                    {
                        Util.Logger.Log($"identity 紛失リカバリー UI でエラー: {ex.Message}", Util.LogLevel.Error);
                    }
                });
            };
            // 初回 SignIn を fire-and-forget で開始。完了前の Firebase 操作は GetIdTokenAsync で例外になり、
            // 既存リトライ経路で吸収される（実装の段階性を保つ）。
            // Codex P2 fix: 初回失敗時 (起動時 offline 等) は EnsureRefreshLoopStarted で refresh ループを
            // 立ち上げてバックグラウンド再試行に委ねる（旧実装は失敗時に何もせず永遠 unauthenticated 状態が続いた）。
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await firebaseAuthClient.SignInAsync();
                }
                catch (Infrastructure.IdentityLostException)
                {
                    // IdentityLost イベント発火済み → clean slate UI が処理する
                }
                catch (Exception ex)
                {
                    Util.Logger.Log($"Firebase Auth 初回 SignIn 失敗（refresh ループに委譲して短期バックオフで再試行）: {ex.Message}", Util.LogLevel.Warning);
                    firebaseAuthClient.EnsureRefreshLoopStarted(startWithBackoff: true);
                }
            });
            // peerRegistry / settingsService は暗号チャネルの PairSecret 引き当て・フラグ参照に使うため先に生成して注入する。
            // peerRegistry は上の IdentityLost ハンドラより前で宣言済み。
            // #D-001a Phase B §6.3: Firebase pairs DELETE が失敗したときの再試行キュー（pending-pair-deletes.json）。
            // Codex P2 fix (第4弾): ConnectionService に注入して pairs/{pairId} 書込成功時に queued delete を取り消す。
            var pendingPairDeletes = new PendingPairDeleteQueue();
            var connectionService = new ConnectionService(AppConstants.FirebaseDatabaseUrl, settings.DeviceId, settings.DisplayName, deviceIdentity, peerRegistry, settingsService, firebaseAuthClient, pendingPairDeletes)
            {
                RelayUrl = AppConstants.RelayUrl,
            };
            var transferService = new TransferService(connectionService, settingsService);
            TransferService = transferService;
            var qrCodeService = new QrCodeGenerator();

            // ロケールを設定から復元（未設定ならシステムロケールを自動検出）
            var locale = string.IsNullOrEmpty(settings.Locale) ? DetectDefaultLocale() : settings.Locale;
            SetLocale(locale);

            // テーマを設定から復元
            RequestedThemeVariant = settings.ThemeMode switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default, // "System" = OS 追従
            };

            // rere #B1-001: presence は Infrastructure を VM が直接 new せず、ファクトリ経由で生成する。
            // #D-001a Phase B: presence も auth 付き REST が必須（rules 厳格化で auth.uid==$deviceId 強制）。
            var presenceFactory = new FirebasePresenceServiceFactory(AppConstants.FirebaseDatabaseUrl, firebaseAuthClient);
            // #D-001a Phase B §6.2: pairs/{pairId} SSoT のローカル同期サービス（起動時即 + 5min + 1h）。
            // 専用の FirebaseSignaling インスタンスを持たせる（ConnectionService の _signaling と独立、
            // ライフサイクル分離）。Visibility gate は MainWindow から SetActive を呼ぶ。
            var pairSyncSignaling = new Infrastructure.FirebaseSignaling(AppConstants.FirebaseDatabaseUrl, firebaseAuthClient);
            var pairSyncService = new PairSyncService(pairSyncSignaling, peerRegistry, settings.DeviceId);
            PairSyncService = pairSyncService;  // MainWindow の visibility ハンドラから SetActive を呼ぶための公開
            pairSyncService.Start();
            var connectionVm = new ConnectionViewModel(connectionService, qrCodeService, settingsService, peerRegistry, presenceFactory, pendingPairDeletes);
            // 起動時 + 10min ごとに未処理の DELETE retry を実行（オフライン中に削除した分の遅延反映 + 起動後に
            // enqueue された分の追従）。Codex P2 fix: 旧実装は起動時のみ呼出で、起動後に enqueue された分は
            // 次再起動まで処理されなかった。バックグラウンドタイマで定期処理する。
            _pendingDeleteCts = new System.Threading.CancellationTokenSource();
            var pendingDeleteCtsToken = _pendingDeleteCts.Token;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                async Task<bool> DeleteOne(string pairId)
                {
                    try
                    {
                        using var sig = new Infrastructure.FirebaseSignaling(AppConstants.FirebaseDatabaseUrl, firebaseAuthClient);
                        await sig.DeletePairAsync(pairId);
                        Util.Logger.Log($"pairs/{pairId} retry DELETE 成功");
                        return true;
                    }
                    catch { return false; }
                }
                // 起動直後 1 回
                try { await pendingPairDeletes.ProcessAsync(DeleteOne); }
                catch (Exception ex) { Util.Logger.Log($"PendingPairDelete 起動時処理エラー: {ex.Message}", Util.LogLevel.Warning); }
                // 以降 10min ごと（ペア削除 enqueue の遅延上限）
                while (!pendingDeleteCtsToken.IsCancellationRequested)
                {
                    try { await System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(10), pendingDeleteCtsToken); }
                    catch (OperationCanceledException) { return; }
                    if (pendingPairDeletes.Count == 0) continue;
                    try { await pendingPairDeletes.ProcessAsync(DeleteOne); }
                    catch (Exception ex) { Util.Logger.Log($"PendingPairDelete 定期処理エラー: {ex.Message}", Util.LogLevel.Warning); }
                }
            });
            // rere PR#8 #F4: プレゼンス監視は実 Firebase I/O を伴うため ctor ではなく本番起動時にここで開始する。
            connectionVm.StartPresenceMonitoring();
            var transferVm = new TransferViewModel(connectionService, transferService, connectionVm, settingsService);
            var settingsVm = new SettingsViewModel(settingsService, transferService);

            // N-6: SettingsViewModel から MVVM 違反を除去するため、テーマ切替 / 更新チェックは App 側で実行する
            settingsVm.ThemeChangeRequested += (_, themeIndex) =>
            {
                RequestedThemeVariant = themeIndex switch
                {
                    1 => ThemeVariant.Light,
                    2 => ThemeVariant.Dark,
                    _ => ThemeVariant.Default, // OS 追従
                };
            };
            settingsVm.UpdateCheckRequested += (_, _) => Check4Update(true);

            var mainVm = new MainWindowViewModel(connectionVm, transferVm, settingsVm);

            _mainWindow = new MainWindow
            {
                DataContext = mainVm,
            };
            _mainWindow.SetSettingsService(settingsService);
            desktop.MainWindow = _mainWindow;

            // VM / Service ライフサイクル一括管理: App が生成した IDisposable をアプリ終了時に依存順で破棄する。
            // desktop.Exit はトレイ「終了」(Shutdown) と最終ウィンドウ close (OnLastWindowClose) の双方で発火する。
            // 破棄順は 利用側 → 提供側: MainWindowViewModel (子 VM = service イベント購読 / presence 監視 /
            // QR Bitmap / SemaphoreSlim / CTS) → TransferService (connectionService イベント購読) →
            // ConnectionService (listener / transport / signaling)。
            // 注: ConnectionViewModel.Dispose の presence 削除は fire-and-forget のため終了時は best-effort
            // (サーバー側で LastSeen が 60 秒老化 → offline 判定)。
            desktop.Exit += (_, _) =>
            {
                DisposeQuietly(mainVm, nameof(MainWindowViewModel));
                DisposeQuietly(transferService, nameof(TransferService));
                DisposeQuietly(pairSyncService, nameof(PairSyncService));
                DisposeQuietly(pairSyncSignaling, nameof(Infrastructure.FirebaseSignaling));
                DisposeQuietly(connectionService, nameof(ConnectionService));
                // #D-001a Phase B: FirebaseAuthClient は FirebaseSignaling から AuthTokenAsyncFactory 経由で
                // 参照されている可能性があるため、ConnectionService 破棄後に破棄する。
                DisposeQuietly(firebaseAuthClient, nameof(Infrastructure.FirebaseAuthClient));
                // ConnectionService が参照を持つので、それを破棄した後に鍵を破棄する。
                DisposeQuietly(deviceIdentity, nameof(Infrastructure.DeviceIdentity));
            };

            // トレイアイコン設定（MinimizeToTray 有効時にウィンドウ復帰用）
            var trayIcon = new TrayIcon
            {
                ToolTipText = "Ferry",
                IsVisible = true,
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Ferry/icon/app.ico"))),
                Menu = CreateTrayMenu(),
            };
            trayIcon.Clicked += (_, _) => ShowMainWindow();
            TrayIcon.SetIcons(this, [trayIcon]);

            // 多重起動防止: 2 つ目の起動が来たら（Windows）既存ウィンドウを前面化する
            SingleInstanceGuard.StartActivationListener(
                () => Avalonia.Threading.Dispatcher.UIThread.Post(ShowMainWindow));

            // 起動時：ペアリング済みピアがあれば最初のピアを宛先として選択
            // ピアがいなければペアリング追加タブ（右ペイン）を自動アクティブにし QR を表示
            if (connectionVm.PairedPeers.Count > 0)
            {
                connectionVm.SelectedPeer = connectionVm.PairedPeers[0];
            }
            else
            {
                // ピア未登録時はペアリング追加タブを自動表示（旧 AddMemberWindow ダイアログから移行）。
                // Loaded はトレイの隠/表示による再アタッチで複数回発火しうるため、初回のみ実行する
                // （多重発火すると StartSessionCommand が重複実行されてしまう）。
                var autoPairingStarted = false;
                _mainWindow.Loaded += (_, _) =>
                {
                    if (autoPairingStarted) return;
                    autoPairingStarted = true;
                    try
                    {
                        connectionVm.StartSessionCommand.Execute(null);
                        mainVm.IsAddPeerMode = true;
                    }
                    catch (Exception ex)
                    {
                        Util.Logger.Log($"起動時ペアリング追加タブ表示失敗: {ex.Message}", Util.LogLevel.Error);
                    }
                };
            }

            // 起動時の自動更新チェック（更新がある場合のみダイアログ表示）。
            // v1.0.38: 起動時 OFF オプションを撤去し一律有効化。トレイ常駐ユーザーへは
            // トレイ右クリックメニューの「アップデートを確認」から手動チェックも可能
            Check4Update(false);
        }

        base.OnFrameworkInitializationCompleted();
    }


    /// <summary>
    /// ロケールを切り替える。
    /// </summary>
    /// <param name="localeKey">ロケールキー（"ja_JP", "en_US" など）</param>
    public static void SetLocale(string localeKey)
    {
        if (Current is not App app ||
            app.Resources[localeKey] is not ResourceDictionary targetLocale ||
            targetLocale == app._activeLocale)
            return;

        // rere #B2-001: en_US をベース辞書として MergedDictionaries の先頭側に常時敷く。
        // Avalonia のリソース解決は後に追加した辞書が優先（後勝ち）なので、選択ロケールを
        // 後ろに Add すれば「選択ロケール優先・欠損キーは en_US」のフォールバックになる
        if (app._baseLocale == null && app.Resources["en_US"] is ResourceDictionary baseLocale)
        {
            app._baseLocale = baseLocale;
            app.Resources.MergedDictionaries.Insert(0, baseLocale);
        }

        if (app._activeLocale != null && !ReferenceEquals(app._activeLocale, app._baseLocale))
            app.Resources.MergedDictionaries.Remove(app._activeLocale);

        if (!ReferenceEquals(targetLocale, app._baseLocale))
            app.Resources.MergedDictionaries.Add(targetLocale);
        app._activeLocale = targetLocale;
    }

    /// <summary>
    /// リソースからローカライズ済みテキストを取得する。
    /// </summary>
    /// <param name="key">リソースキー（"Text." プレフィックスなし）</param>
    /// <param name="args">フォーマット引数</param>
    /// <returns>ローカライズ済み文字列</returns>
    public static string Text(string key, params object[] args)
    {
        // Avalonia 12 では Application.FindResource はキー未登録時に例外を投げる契約。
        // ロケール途中切替で欠損キーがあれば即落ちするのを避け、TryGetResource にフォールバック
        string? fmt = null;
        if (Current is { } app
            && app.TryGetResource($"Text.{key}", app.ActualThemeVariant, out var value)
            && value is string s)
        {
            fmt = s;
        }
        if (string.IsNullOrWhiteSpace(fmt))
            return $"Text.{key}";

        if (args == null || args.Length == 0)
            return fmt;

        return string.Format(fmt, args);
    }

    /// <summary>
    /// メインウィンドウを取得する（IClassicDesktopStyleApplicationLifetime 経由）。
    /// M-8: 3 箇所のフル修飾キャストパターンを集約。SingleViewLifetime 等の変更にも一元対応可能。
    /// </summary>
    public static Window? GetMainWindow() =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>アプリ終了時の一括破棄ヘルパー。1 つの破棄が例外を投げても残りの破棄を継続できるよう隔離する。</summary>
    private static void DisposeQuietly(IDisposable disposable, string name)
    {
        try { disposable.Dispose(); }
        catch (Exception ex) { Util.Logger.LogException($"{name} の Dispose で例外", ex); }
    }

    /// <summary>
    /// システムのカルチャからデフォルトロケールを検出する。
    /// </summary>
    public static string DetectDefaultLocale()
    {
        var culture = CultureInfo.CurrentUICulture;
        var name = culture.Name.Replace('-', '_');

        // 完全一致
        if (SupportedLocales.Contains(name))
            return name;

        // 言語部分のみで一致（例: "ja" → "ja_JP"）
        var lang = culture.TwoLetterISOLanguageName;
        var match = SupportedLocales.FirstOrDefault(l => l.StartsWith(lang + "_", StringComparison.OrdinalIgnoreCase));
        return match ?? "en_US";
    }

    /// <summary>メインウィンドウを表示・復帰し、最前面 + アクティブにする（トレイからの復帰 / 2 個目起動シグナル）。</summary>
    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.ShowInTaskbar = true;
        // トレイ格納時は WindowState=Minimized のまま Hide されている。Minimized のときだけ Normal に戻す
        // （Maximized 表示中に 2 個目が起動したケースで最大化状態を壊さないよう、無条件 Normal 化はしない）。
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
        // Windows のフォアグラウンド奪取制限の回避: シグナルを送った 2 個目プロセスが直前まで前面だったため、
        // Activate() だけでは前面に出ず「非アクティブで起動した」ように見える（実機で確認）。Topmost を一瞬
        // 立ててすぐ戻すと確実に最前面 + フォーカスを取れる（全 OS で無害。MainWindow は通常 Topmost=false）。
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
    }

    /// <summary>トレイアイコンの右クリックメニューを作成する。</summary>
    private NativeMenu CreateTrayMenu()
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem(Text("Tray.ShowWindow"));
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Add(showItem);

        // Komorebi と同じく手動でアップデート確認できる入口をトレイにも置く
        // （Ferry はサイドバーを開かないと Settings に行けないため、トレイから一発で叩ける方が便利）
        var checkUpdateItem = new NativeMenuItem(Text("Tray.CheckForUpdate"));
        checkUpdateItem.Click += (_, _) => Check4Update(true);
        menu.Add(checkUpdateItem);

        menu.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem(Text("Tray.Exit"));
        exitItem.Click += (_, _) =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // macOS で OnClosing が Hide に倒さず実際に終了できるよう、明示終了フラグを立ててから落とす。
                IsExplicitShutdown = true;
                desktop.Shutdown();
            }
        };
        menu.Add(exitItem);

        return menu;
    }

    // === 自動更新チェック（VelopackUpdateDialog.Avalonia 委譲、Komorebi 同等パターン） ===

    /// <summary>更新チェック中かどうかのアトミックフラグ（0=未実行, 1=実行中）。
    /// 起動時自動チェックとメニュー手動チェックの同時実行を防止する。</summary>
    private static int _isCheckingUpdate;

    /// <summary>
    /// 更新チェックが進行中かどうかを ViewModel / 他ロジックから観測するための public 読み取りプロパティ。
    /// 値は <see cref="_isCheckingUpdate"/> をロックフリーで読む（実行中: true / 未実行: false）。
    /// Lhamiel と同じ設計: SettingsViewModel が UpdateCheckStateChanged の購読直後に
    /// この値で初期同期するため、起動時自動チェック中に Settings 画面を開いてもボタンが正しく
    /// 無効化される。
    /// </summary>
    public static bool IsUpdateCheckInProgress =>
        System.Threading.Volatile.Read(ref _isCheckingUpdate) == 1;

    /// <summary>更新チェックの進行状態が変化したときに発火するイベント (true=開始 / false=終了)。</summary>
    /// <remarks>
    /// 起動時自動チェック / 24h 周期チェック / 手動チェック (設定画面のアップデート確認ボタン) の
    /// すべての経路から発火する。SettingsViewModel がこれを購読して IsCheckingUpdate を駆動し、
    /// ボタンの IsEnabled を制御する (並走実行抑止と視覚的フィードバック)。
    /// ハンドラはバックグラウンドスレッドから呼ばれる可能性があるため、UI 更新は購読側で
    /// Dispatcher.UIThread に marshal すること。
    /// </remarks>
    public static event Action<bool>? UpdateCheckStateChanged;

    /// <summary>更新チェック開始の試行。0→1 への遷移に成功した場合のみ true を返し、イベントを発火する。</summary>
    private static bool TryBeginUpdateCheck()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _isCheckingUpdate, 1, 0) != 0)
            return false;
        RaiseUpdateCheckStateChanged(true);
        return true;
    }

    /// <summary>更新チェック終了。1→0 への遷移に成功した場合のみイベントを発火する (多重呼出に対して冪等)。</summary>
    private static void EndUpdateCheck()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _isCheckingUpdate, 0, 1) == 1)
            RaiseUpdateCheckStateChanged(false);
    }

    /// <summary>イベント発火時のハンドラ例外を握りつぶしてフラグ管理を巻き戻さないためのラッパー。</summary>
    private static void RaiseUpdateCheckStateChanged(bool inProgress)
    {
        try { UpdateCheckStateChanged?.Invoke(inProgress); }
        catch (Exception ex) { Util.Logger.LogException("UpdateCheckStateChanged ハンドラで例外", ex); }
    }

    /// <summary>
    /// VelopackUpdateDialog.Avalonia ライブラリに更新チェックとダイアログ表示を委譲する。
    /// 自動チェック時はサイレント、手動チェック時は結果ダイアログを表示する。
    /// </summary>
    /// <param name="manually">
    /// true: 手動チェック（最新版/失敗でも結果ダイアログを表示、無視タグをスキップ）
    /// false: 自動チェック（更新がありかつ無視タグと一致しない場合のみダイアログ表示）
    /// </param>
    public void Check4Update(bool manually = false)
    {
        // 自動チェックは転送中なら丸ごとスキップする。Velopack は更新ありで DownloadAndApply →
        // アプリ再起動するため、転送中に走ると進行中のファイル転送を切断してしまう。
        // 手動チェック (manually=true) はユーザー意図なので許可。フラグ操作前に判定してフリッカを避ける。
        if (!manually && TransferService?.HasActiveTransfer == true)
            return;

        // 先勝ち: 既に更新チェック中なら何もしない (UI ボタンは UpdateCheckStateChanged 経由で
        // IsEnabled=false に落ちているので、ここに到達しても操作不可状態と整合する)
        if (!TryBeginUpdateCheck())
            return;

        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                // Velopack の UpdateManager を初期化（配信元: Cloudflare R2 カスタムドメイン経由の SimpleWebSource）
                var source = new Velopack.Sources.SimpleWebSource(UpdateBaseUrl);
                var mgr = new Velopack.UpdateManager(source);

                // ライブラリへ Ferry 流のローカライズ・アイコン・無視タグを注入する
                var settings = _settingsService?.Settings;
                // owner が非表示 (トレイ最小化で Hide 済み / 未 Open) のまま渡すと
                // "Cannot show window with non-visible owner" 例外になる。非表示なら null を渡す。
                var ownerWin = GetMainWindow(); // M-8 統一
                var owner = ownerWin?.IsVisible == true ? ownerWin : null;

                // Avalonia 12 では Application.FindResource が直接公開されないため TryGetResource を使う
                IBrush? accent = null;
                if (Current is { } app
                    && app.TryGetResource("TahoeAccentBrush", app.ActualThemeVariant, out var brushObj)
                    && brushObj is IBrush b)
                {
                    accent = b;
                }

                // VelopackUpdateDialog.Avalonia 1.0.4 の UpdateDialogOptions は Strings 差し替えのみ
                // 公開しており、Icons プロパティは未提供 (内蔵ベクタアイコンが固定)。
                // 旧 UpdateDialogIcons は撤去済み。将来ライブラリ側で IUpdateDialogIcons が公開され
                // 次第、Ferry 専用アイコンセットを Models 以下に追加して Icons = ... で接続する。
                var options = new VelopackUpdateDialog.UpdateDialogOptions
                {
                    Strings = Models.UpdateDialogStrings.Instance,
                    AccentBrush = accent,
                    IgnoredTagName = string.IsNullOrEmpty(settings?.IgnoreUpdateTag) ? null : settings!.IgnoreUpdateTag,
                    SuppressUpToDateOnAutoCheck = true,
                };

                // 「このバージョンを無視」を Settings へ永続化する
                options.VersionIgnored += tag => IgnoreUpdateVersion(tag);

                // チェック / ダウンロード中の例外をログへ流す（手動チェック時はライブラリ側でエラーダイアログも出る）
                options.ErrorOccurred += ex => Util.Logger.LogException("更新チェック失敗", ex);

                // 手動=true ならウィンドウ即表示でチェック進捗を可視化、自動=false ならチェック完了まで非表示
                await VelopackUpdateDialog.UpdateDialogWindow.ShowAsync(owner, mgr, options, manualCheck: manually);
            }
            catch (Exception e)
            {
                Util.Logger.LogException("更新チェック失敗", e);
            }
            finally
            {
                EndUpdateCheck();
            }
        });
    }

    /// <summary>指定バージョンの更新通知を無視するよう設定に記録する。</summary>
    public void IgnoreUpdateVersion(string tag)
    {
        var s = _settingsService?.Settings;
        if (s == null) return;
        s.IgnoreUpdateTag = tag;
        _ = _settingsService!.SaveAsync();
    }
}
