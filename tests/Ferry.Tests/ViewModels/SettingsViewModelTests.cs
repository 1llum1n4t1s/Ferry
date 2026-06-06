using Ferry.ViewModels;
using Xunit;

namespace Ferry.Tests.ViewModels;

/// <summary>
/// SettingsViewModel の純粋ロジック (IsObsoleteIgnoreTag) のテスト。
/// VM 本体は UI スレッド依存のため、ここでは static ヘルパーのみ検証する。
/// </summary>
public class SettingsViewModelTests
{
    [Theory]
    // skip 対象 < インストール済み → 陳腐化 (自動クリア対象)
    [InlineData("1.0.20", "1.0.46", true)]
    [InlineData("1.0.9", "1.0.46", true)]   // 9 <= 46 を数値比較で正しく判定 (文字列比較ではない)
    [InlineData("1.0.0", "2.0.0", true)]
    // skip 対象 == インストール済み → 既にそのバージョン上なので陳腐化扱い
    [InlineData("1.0.46", "1.0.46", true)]
    // skip 対象 > インストール済み → まだ来てない未来バージョンの skip なので保持
    [InlineData("1.0.47", "1.0.46", false)]
    [InlineData("2.0.0", "1.0.46", false)]
    // 先頭 'v' 許容
    [InlineData("v1.0.20", "1.0.46", true)]
    [InlineData("1.0.20", "v1.0.46", true)]
    // パース不能タグは保持 (誤クリア防止)
    [InlineData("", "1.0.46", false)]
    [InlineData("latest", "1.0.46", false)]
    [InlineData("1.0.x", "1.0.46", false)]
    public void IsObsoleteIgnoreTag_バージョン比較で陳腐化を判定すること(string tag, string current, bool expected)
    {
        Assert.Equal(expected, SettingsViewModel.IsObsoleteIgnoreTag(tag, current));
    }
}
