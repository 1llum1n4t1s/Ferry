using Ferry.Util;

namespace Ferry.Tests.Util;

/// <summary>
/// AppPaths のパス解決をテストする。実行ランナー（Windows）で確認できる不変条件を固定する:
/// ログ出力先は必ず「絶対パス」で末尾が Ferry/logs 系であること（GetFolderPath 空文字 →
/// 相対パス化 → 書込失敗 という退行を防ぐ）。mac 固有の ~/Library/Logs 分岐は実機確認に委ねる。
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void GetLogDirectory_絶対パスを返す()
    {
        var dir = AppPaths.GetLogDirectory();
        Assert.False(string.IsNullOrEmpty(dir));
        Assert.True(Path.IsPathRooted(dir), $"ログ出力先が絶対パスでない: {dir}");
    }

    [Fact]
    public void GetLogDirectory_Ferryフォルダ配下を指す()
    {
        var dir = AppPaths.GetLogDirectory();
        Assert.Contains("Ferry", dir);
    }
}
