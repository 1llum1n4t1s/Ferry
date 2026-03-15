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
    public void FirebaseDatabaseUrlのデフォルトが空文字列であること()
    {
        var settings = new AppSettings();
        Assert.Equal(string.Empty, settings.FirebaseDatabaseUrl);
    }

    [Fact]
    public void BridgePageUrlのデフォルトが空文字列であること()
    {
        var settings = new AppSettings();
        Assert.Equal(string.Empty, settings.BridgePageUrl);
    }

    [Fact]
    public void ブール設定のデフォルトがfalseであること()
    {
        var settings = new AppSettings();
        Assert.False(settings.RunAtStartup);
        Assert.False(settings.StartMinimized);
        Assert.False(settings.MinimizeToTray);
    }
}
