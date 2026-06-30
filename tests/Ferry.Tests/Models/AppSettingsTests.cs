using Ferry.Models;

namespace Ferry.Tests.Models;

/// <summary>
/// AppSettings のデフォルト値と DeviceId 生成を検証する。
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void DeviceIdがGUID形式の32文字16進数であること()
    {
        var settings = new AppSettings();
        // "N" フォーマット: ハイフンなし32文字
        Assert.Equal(32, settings.DeviceId.Length);
        Assert.True(Guid.TryParseExact(settings.DeviceId, "N", out _),
            $"DeviceId '{settings.DeviceId}' は GUID の N フォーマットではない");
    }

    [Fact]
    public void DeviceIdがインスタンスごとに異なること()
    {
        var a = new AppSettings();
        var b = new AppSettings();
        Assert.NotEqual(a.DeviceId, b.DeviceId);
    }

    [Fact]
    public void DisplayNameのデフォルトがマシン名であること()
    {
        var settings = new AppSettings();
        Assert.Equal(Environment.MachineName, settings.DisplayName);
    }

    [Fact]
    public void SaveDirectoryのデフォルトがDownloadsであること()
    {
        var settings = new AppSettings();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        Assert.Equal(expected, settings.SaveDirectory);
    }

    [Fact]
    public void AppConstantsの接続先URLが非空で期待スキームであること()
    {
        // rere #D-004: 接続先 URL は settings から AppConstants(const) へ移行。空文字化や URL タイポの回帰を固定化。
        // CF 単独完結 Step 6: Firebase 系定数は撤去し、CF 経路の定数のみ検証する。
        Assert.StartsWith("https://", Ferry.AppConstants.CfApiBaseUrl);
        Assert.StartsWith("https://", Ferry.AppConstants.CfBridgePageUrl);
        Assert.StartsWith("wss://", Ferry.AppConstants.RelayUrl);
    }

    [Fact]
    public void 旧settingsの撤去済みキーを含むJSONも例外なくロードできること()
    {
        // rere #D-004 / CF 単独完結 Step 6: AppSettings から削除したキー（FirebaseDatabaseUrl / BridgePageUrl /
        // dual-path の UseCloudflareSignaling / MigratedToCloudflareDefault）が旧 settings.json に残っていても、
        // 未知キーとして無視され DeviceId 等は正しく読めること（次回 SaveAsync で自然に消える）。
        // 実際のロード経路（SettingsService が AppSettingsJsonContext で読む）を通して検証する。
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ferry_test_{System.Guid.NewGuid():N}.json");
        System.IO.File.WriteAllText(path, "{\"DeviceId\":\"abc123\",\"DisplayName\":\"OldPC\",\"FirebaseDatabaseUrl\":\"https://old.example.com\",\"BridgePageUrl\":\"https://old.web.app\",\"UseCloudflareSignaling\":false,\"MigratedToCloudflareDefault\":true}");
        try
        {
            using var svc = new Ferry.Services.SettingsService(path);
            Assert.Equal("abc123", svc.Settings.DeviceId);
            Assert.Equal("OldPC", svc.Settings.DisplayName);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ブール設定のデフォルトがfalseであること()
    {
        var settings = new AppSettings();
        // N-2: 旧 RunAtStartup は AutoStartWithWindows と統合済みのため検証から除外
        Assert.False(settings.StartMinimized);
        Assert.False(settings.MinimizeToTray);
        Assert.False(settings.AutoStartWithWindows);
    }

    [Fact]
    public void 位置未設定のNaNウィンドウ座標を含む新規設定がserializeできること()
    {
        // WindowX/WindowY は「位置未設定」を double.NaN で表す。既定の System.Text.Json は NaN を
        // 書けず ArgumentException になるため、AppSettingsJsonContext で AllowNamedFloatingPointLiterals
        // を有効化している。初回起動 (= NaN のまま) の同期 Save が落ちて settings.json が永続化されない
        // 回帰を固定化する。
        var s = new AppSettings(); // WindowX/Y = double.NaN
        Assert.True(double.IsNaN(s.WindowX) && double.IsNaN(s.WindowY));

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            s, Ferry.Infrastructure.AppSettingsJsonContext.Default.AppSettings);
        var round = System.Text.Json.JsonSerializer.Deserialize(
            json, Ferry.Infrastructure.AppSettingsJsonContext.Default.AppSettings);

        // NaN センチネルが round-trip し、IsNaN 判定 (位置復元のゲート) と整合すること
        Assert.NotNull(round);
        Assert.True(double.IsNaN(round!.WindowX) && double.IsNaN(round.WindowY));
    }

    [Fact]
    public void 新規インストールは初回Saveでsettingsjsonが生成されDeviceIdが確定すること()
    {
        // settings.json が存在しない初回起動: 初回 Save でファイルが生成され DeviceId が永続化される。
        // WindowX/Y の NaN を書けず初回 Save が落ちて永続化されない回帰（AllowNamedFloatingPointLiterals）を固定化する。
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ferry_test_{System.Guid.NewGuid():N}.json");
        try
        {
            using var svc = new Ferry.Services.SettingsService(path);
            Assert.True(System.IO.File.Exists(path)); // 初回 Save で生成される
            Assert.Equal(32, svc.Settings.DeviceId.Length);

            // 再ロードしても同じ DeviceId が読める（永続化が成立している）
            using var reloaded = new Ferry.Services.SettingsService(path);
            Assert.Equal(svc.Settings.DeviceId, reloaded.Settings.DeviceId);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }
}
