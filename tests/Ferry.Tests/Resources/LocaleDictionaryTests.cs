using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ferry.Tests.Resources;

/// <summary>
/// ロケール辞書（<c>src/Ferry/Resources/Locales/*.axaml</c>）の整合性テスト。
///
/// rere レビュー #C-29: 既存の <c>PairedPeerTests</c> は <c>App.Text</c> がキー未解決時に返す
/// フォールバック文字列（<c>$"Text.{key}"</c>）を期待値にしている。ユニットテストには
/// Avalonia の Application が存在せず <c>Application.Current == null</c> なので、あれは
/// 「enum → キー名」のマッピングしか検証しておらず、**そのキーが辞書に実在するか**は
/// 一切見ていない。つまりキー名を打ち間違えても、辞書側からキーを消しても、テストは緑のまま
/// UI に生の <c>Text.Route.Lan.Badge</c> が表示される。
///
/// ここでは Avalonia を起動せず、.axaml をテキストとして読んで次を固定する:
///   1. 全ロケールのキー集合が en_US と一致する（欠損＝英語フォールバック、余剰＝死蔵）
///   2. コード・AXAML から参照される Text.* キーがすべて en_US に存在する
///   3. 死蔵キー（どこからも参照されないキー）が存在しない
///
/// 2 と 3 は「動的に組み立てられるキー」を除外して判定する（下の DynamicKeyPrefixes 参照）。
/// </summary>
public class LocaleDictionaryTests
{
    private static readonly Regex KeyRegex = new(@"x:Key=""(Text\.[A-Za-z0-9_.]+)""", RegexOptions.Compiled);
    private static readonly Regex DynamicResourceRegex = new(@"DynamicResource\s+(Text\.[A-Za-z0-9_.]+)", RegexOptions.Compiled);
    private static readonly Regex AppTextRegex = new(@"\bText\(""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    /// <summary>
    /// 実行時にキー名を組み立てて <c>App.Text(variable)</c> へ渡す箇所があるプレフィックス。
    /// 静的検索では参照を検出できないので、死蔵判定から除外する。
    ///   - Status.Phase.* : ConnectionService が StatusMessageChanged で文字列キーを流す
    ///   - Peer.Section.* : BuildPeerProjection が label(...) デリゲート経由で引く
    ///   - Transfer.Error.*: ErrorText.RefreshCache がまとめて引く
    /// </summary>
    private static readonly string[] DynamicKeyPrefixes =
    [
        "Text.Status.Phase.",
        "Text.Peer.Section.",
        "Text.Transfer.Error.",
    ];

    private static string RepoRoot()
    {
        // テストは tests/Ferry.Tests/bin/<cfg>/<tfm>/ から実行される
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ferry.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string LocalesDir() => Path.Combine(RepoRoot(), "src", "Ferry", "Resources", "Locales");

    private static HashSet<string> KeysOf(string axamlPath) =>
        KeyRegex.Matches(File.ReadAllText(axamlPath)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    public static TheoryData<string> LocaleFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(LocalesDir(), "*.axaml").OrderBy(f => f))
        {
            if (Path.GetFileNameWithoutExtension(f) == "en_US") continue;
            data.Add(Path.GetFileName(f));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(LocaleFiles))]
    public void 各ロケールのキー集合がen_USと完全一致すること(string localeFileName)
    {
        var en = KeysOf(Path.Combine(LocalesDir(), "en_US.axaml"));
        var loc = KeysOf(Path.Combine(LocalesDir(), localeFileName));

        var missing = en.Except(loc).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = loc.Except(en).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, $"{localeFileName} に en_US のキーが不足: {string.Join(", ", missing)}");
        Assert.True(extra.Count == 0, $"{localeFileName} に en_US に無いキーが余剰: {string.Join(", ", extra)}");
    }

    /// <summary>コード・AXAML が参照する Text.* キーを収集する（Locales 自身は除外）。</summary>
    private static HashSet<string> ReferencedKeys()
    {
        var root = Path.Combine(RepoRoot(), "src", "Ferry");
        var refs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            var norm = path.Replace('\\', '/');
            if (norm.Contains("/Locales/") || norm.Contains("/obj/") || norm.Contains("/bin/")) continue;
            var ext = Path.GetExtension(path);
            if (ext is not (".cs" or ".axaml")) continue;

            var text = File.ReadAllText(path);
            foreach (Match m in DynamicResourceRegex.Matches(text))
                refs.Add(m.Groups[1].Value);
            // App.Text("Xxx") は "Text." プレフィックス無しで呼ぶ契約
            foreach (Match m in AppTextRegex.Matches(text))
                refs.Add("Text." + m.Groups[1].Value);
        }
        return refs;
    }

    [Fact]
    public void 参照されている全キーがen_USに定義されていること()
    {
        var en = KeysOf(Path.Combine(LocalesDir(), "en_US.axaml"));
        var undefined = ReferencedKeys()
            .Where(k => !en.Contains(k))
            // 動的キーはプレフィックスだけが定数なので、収集側には現れない
            .Where(k => !DynamicKeyPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undefined.Count == 0,
            $"参照されているのに en_US.axaml に無いキー: {string.Join(", ", undefined)}");
    }

    [Fact]
    public void 参照されないキーが辞書に残っていないこと()
    {
        var en = KeysOf(Path.Combine(LocalesDir(), "en_US.axaml"));
        var referenced = ReferencedKeys();

        var dead = en
            .Where(k => !referenced.Contains(k))
            .Where(k => !DynamicKeyPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            dead.Count == 0,
            $"どこからも参照されていない死蔵キー: {string.Join(", ", dead)}\n" +
            "（実行時にキーを組み立てる新しい経路を足した場合は DynamicKeyPrefixes に追加すること）");
    }
}
