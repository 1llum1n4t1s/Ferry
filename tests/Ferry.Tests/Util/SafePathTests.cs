using Ferry.Util;

namespace Ferry.Tests.Util;

/// <summary>
/// SafePath（受信パスの送信元 OS 非依存な安全化）の純関数を網羅的にテストする。
/// 特に Windows 送信 → mac/Linux 受信の混在で正規ファイルを誤拒否しない・トラバーサルを弾く、を固定する。
/// </summary>
public class SafePathTests
{
    // ==================== NormalizeSeparators ====================

    [Theory]
    [InlineData("sub\\file.txt", "sub/file.txt")]
    [InlineData("a\\b\\c", "a/b/c")]
    [InlineData("already/slash.txt", "already/slash.txt")]
    [InlineData("noseparator.txt", "noseparator.txt")]
    public void NormalizeSeparators_バックスラッシュをスラッシュに変換する(string input, string expected)
    {
        Assert.Equal(expected, SafePath.NormalizeSeparators(input));
    }

    [Fact]
    public void NormalizeSeparators_空文字はそのまま返す()
    {
        Assert.Equal("", SafePath.NormalizeSeparators(""));
    }

    // ==================== HasParentTraversal ====================

    [Theory]
    [InlineData("a/../b")]
    [InlineData("../escape")]
    [InlineData("a/b/..")]
    [InlineData("..")]
    public void HasParentTraversal_親参照のパス要素を含むとtrue(string path)
    {
        Assert.True(SafePath.HasParentTraversal(path));
    }

    [Theory]
    [InlineData("a/b/c")]
    [InlineData("photos/sub/file.jpg")]
    [InlineData("my..file.txt")]    // substring に ".." を含むが要素単位では traversal でない（誤拒否しない）
    [InlineData("..foo/bar")]       // ".." で始まるが別物のフォルダ名
    [InlineData("single.txt")]
    [InlineData("")]
    public void HasParentTraversal_正規パスはfalse(string path)
    {
        Assert.False(SafePath.HasParentTraversal(path));
    }

    // ==================== HasUnsafeRoot ====================

    [Theory]
    [InlineData("/evil.txt")]     // 先頭 "/"（絶対パス化け／フラット化の兆候）
    [InlineData("/")]
    [InlineData("./evil.txt")]    // 先頭 "." root
    [InlineData(".")]
    public void HasUnsafeRoot_先頭が空やドットrootはtrue(string path)
    {
        Assert.True(SafePath.HasUnsafeRoot(path));
    }

    [Theory]
    [InlineData("photos/sub/a.jpg")]
    [InlineData("folder/file.txt")]
    [InlineData("single.txt")]
    [InlineData("")]
    public void HasUnsafeRoot_正規rootはfalse(string path)
    {
        Assert.False(SafePath.HasUnsafeRoot(path));
    }

    // ==================== SafeFileName ====================

    [Theory]
    [InlineData("file.txt", "file.txt")]
    [InlineData("sub\\file.txt", "file.txt")]              // Windows 送信由来の '\' 区切りを剥がす（混在 OS の本丸）
    [InlineData("C:\\Users\\x\\report.txt", "report.txt")] // ドライブ付きフルパスでも basename
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("deep\\path/mixed\\name.txt", "name.txt")] // '\' と '/' 混在
    public void SafeFileName_ディレクトリ要素を剥がしてbasenameを返す(string input, string expected)
    {
        Assert.Equal(expected, SafePath.SafeFileName(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("sub\\..")]   // 末尾が ".." に解決される
    public void SafeFileName_空やドット参照はnull(string input)
    {
        Assert.Null(SafePath.SafeFileName(input));
    }

    // ==================== IsWithinDirectory ====================

    private static string BaseDir => Path.Combine(Path.GetTempPath(), "ferry_safepath_base");

    [Fact]
    public void IsWithinDirectory_配下のファイルはtrue()
    {
        Assert.True(SafePath.IsWithinDirectory(BaseDir, Path.Combine(BaseDir, "sub", "file.txt")));
    }

    [Fact]
    public void IsWithinDirectory_内部で打ち消されて配下に留まるパスはtrue()
    {
        // base/sub/../ok.txt → base/ok.txt（配下に留まる）
        Assert.True(SafePath.IsWithinDirectory(BaseDir, Path.Combine(BaseDir, "sub", "..", "ok.txt")));
    }

    [Fact]
    public void IsWithinDirectory_親へ脱出するパスはfalse()
    {
        Assert.False(SafePath.IsWithinDirectory(BaseDir, Path.Combine(BaseDir, "..", "evil.txt")));
    }

    [Fact]
    public void IsWithinDirectory_多段で親へ脱出するパスはfalse()
    {
        Assert.False(SafePath.IsWithinDirectory(BaseDir, Path.Combine(BaseDir, "sub", "..", "..", "evil")));
    }

    [Fact]
    public void IsWithinDirectory_絶対パス混入はfalse()
    {
        // RelativePath 経路で絶対パス/別ドライブが Path.Combine をすり抜けても、最終防御で弾く。
        var absolute = OperatingSystem.IsWindows()
            ? "C:\\Windows\\System32\\evil.txt"
            : "/etc/passwd";
        Assert.False(SafePath.IsWithinDirectory(BaseDir, absolute));
    }

    [Fact]
    public void IsWithinDirectory_兄弟プレフィックスディレクトリはfalse()
    {
        // base="..\ferry_safepath_base" に対し "..\ferry_safepath_base_evil\x" は
        // 文字列 StartsWith だと誤許可しうるが、GetRelativePath ベースでは脱出として弾く。
        Assert.False(SafePath.IsWithinDirectory(BaseDir, BaseDir + "_evil" + Path.DirectorySeparatorChar + "x"));
    }

    [Fact]
    public void IsWithinDirectory_NUL文字混入は例外を投げずfalse()
    {
        // 攻撃者制御のファイル名に NUL が混入すると Path.GetFullPath が ArgumentException を投げる。
        // 未捕捉だと受信ループ→接続切断（DoS）になるため、IsWithinDirectory は throw せず false に倒す。
        var withNul = Path.Combine(BaseDir, "ab\0cd.txt");
        var ex = Record.Exception(() => Assert.False(SafePath.IsWithinDirectory(BaseDir, withNul)));
        Assert.Null(ex); // 例外を投げないこと自体が回帰防止の主眼
    }

    // ==================== ContainsControlChar ====================

    [Theory]
    [InlineData("ab\0cd.txt")]   // NUL（Path.* を throw させる本丸）
    [InlineData("line\nbreak")]  // 改行
    [InlineData("tab\there")]    // タブ
    public void ContainsControlChar_制御文字を含むとtrue(string s)
    {
        Assert.True(SafePath.ContainsControlChar(s));
    }

    [Theory]
    [InlineData("normal.txt")]
    [InlineData("日本語ファイル.png")]
    [InlineData("sub/dir/file.bin")]
    [InlineData("")]
    [InlineData(null)]
    public void ContainsControlChar_通常文字列はfalse(string? s)
    {
        Assert.False(SafePath.ContainsControlChar(s));
    }

    [Fact]
    public void SafeFileName_NUL混入はnull()
    {
        // 制御文字を含むファイル名は不正として null（HandleFileMeta が FileReject を送る経路）。
        Assert.Null(SafePath.SafeFileName("ab\0cd.txt"));
        Assert.Null(SafePath.SafeFileName("sub\\bad\nname.txt"));
    }
}
