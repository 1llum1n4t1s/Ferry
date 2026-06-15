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
        Assert.StartsWith("https://", Ferry.AppConstants.FirebaseDatabaseUrl);
        Assert.StartsWith("https://", Ferry.AppConstants.BridgePageUrl);
        Assert.StartsWith("wss://", Ferry.AppConstants.RelayUrl);
    }

    [Fact]
    public void 旧settingsの撤去済みURLキーを含むJSONも例外なくロードできること()
    {
        // rere #D-004: AppSettings から削除した FirebaseDatabaseUrl / BridgePageUrl が旧 settings.json に
        // 残っていても、未知キーとして無視され DeviceId 等は正しく読めること（次回 SaveAsync で自然に消える）。
        // 実際のロード経路（SettingsService が AppSettingsJsonContext で読む）を通して検証する。
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ferry_test_{System.Guid.NewGuid():N}.json");
        System.IO.File.WriteAllText(path, "{\"DeviceId\":\"abc123\",\"DisplayName\":\"OldPC\",\"FirebaseDatabaseUrl\":\"https://old.example.com\",\"BridgePageUrl\":\"https://old.web.app\"}");
        try
        {
            var svc = new Ferry.Services.SettingsService(path);
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
}
