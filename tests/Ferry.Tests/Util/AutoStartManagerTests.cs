using Ferry.Util;

namespace Ferry.Tests.Util;

/// <summary>
/// AutoStartManager.IsProductionInstallPath（自動起動エントリを書き換えてよいかの判定）を固定する。
/// 開発ビルド（bin/Debug・bin/Release から直接起動）だけ false にし、本番配布形態
/// （Velopack / AppImage / .deb / .rpm / .app）は全て true にする。これを誤ると、
/// 開発ビルド起動時の self-heal がログイン自動起動を bin/Debug パスで上書きし、
/// 以降ログインのたびに開発ビルドが立ち上がって本番アプリの自動更新が届かなくなる回帰になる。
/// </summary>
public class AutoStartManagerTests
{
    [Theory]
    // --- 開発ビルド（書き換え禁止 = false） ---
    [InlineData(@"C:\Users\IMT\dev\Ferry\src\Ferry\bin\Debug\net10.0\Ferry.exe")]   // Windows Debug
    [InlineData(@"C:\Users\IMT\dev\Ferry\src\Ferry\bin\Release\net10.0\Ferry.exe")] // Windows Release
    [InlineData("/home/me/dev/Ferry/src/Ferry/bin/Debug/net10.0/Ferry")]            // Linux/mac Debug
    [InlineData("/home/me/dev/Ferry/src/Ferry/bin/Release/net10.0/Ferry")]          // Linux/mac Release
    [InlineData(@"C:\proj\Ferry\bin\debug\net10.0\Ferry.exe")]                      // 大文字小文字無視
    public void IsProductionInstallPath_開発ビルドは_false(string path)
    {
        Assert.False(AutoStartManager.IsProductionInstallPath(path));
    }

    [Theory]
    // --- 本番配布形態（書き換え可 = true） ---
    [InlineData(@"C:\Users\IMT\AppData\Local\Ferry\current\Ferry.exe")]   // Velopack Windows
    [InlineData(@"C:\Users\IMT\AppData\Local\Ferry\Ferry.exe")]           // Velopack シム
    [InlineData("/Applications/Ferry.app/Contents/MacOS/Ferry")]          // macOS .app
    [InlineData("/tmp/.mount_Ferry12345/AppRun")]                         // Linux AppImage 実行時の ProcessPath（マウント内）
    [InlineData("/home/me/Downloads/Ferry-1.0.60-linux-x64.AppImage")]    // Linux AppImage の $APPIMAGE 実体
    [InlineData("/opt/ferry/Ferry")]                                      // Linux .deb/.rpm
    [InlineData("/usr/lib/ferry/Ferry")]                                  // Linux パッケージ別配置
    public void IsProductionInstallPath_本番配布は_true(string path)
    {
        Assert.True(AutoStartManager.IsProductionInstallPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsProductionInstallPath_パス不明は安全側で_true(string? path)
    {
        // パス不明時は本番扱い（true）。登録経路に別途の空パスガードがあるため誤登録は起きない。
        Assert.True(AutoStartManager.IsProductionInstallPath(path));
    }

    [Fact]
    public void IsProductionInstallPath_binを含むだけのパスは誤検出しない()
    {
        // "bin" を含むが bin/Debug・bin/Release ではない正規の本番パスは true のまま。
        Assert.True(AutoStartManager.IsProductionInstallPath("/usr/bin/ferry/Ferry"));
        Assert.True(AutoStartManager.IsProductionInstallPath(@"C:\bin\Ferry\Ferry.exe"));
    }
}
