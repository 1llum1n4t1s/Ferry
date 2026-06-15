using System.IO;
using System.Threading.Tasks;

namespace Ferry.Util;

/// <summary>
/// 設定・ペア情報など重要ファイルを「一時ファイルに書いてからリネームで置換」する
/// アトミック保存ヘルパー。書き込み中断（クラッシュ/電源断）による本体ファイル破損を防ぐ。
/// rere #B2-001: SettingsService（同期 WriteAtomic / 非同期 SaveAsync）と PeerRegistryService に
/// 分散していた同一ロジック（tmp→Move）を 1 箇所へ集約し、置換手順の取り違えを防ぐ。
/// 前提: tmp と本体は同一ディレクトリ＝同一ボリューム上にあり、File.Move(overwrite) が
/// アトミックに置換できる（クロスボリュームではアトミック性が保証されない）。
/// </summary>
public static class AtomicFile
{
    /// <summary>一時ファイルへ書いてからリネームで置換する（同期）。</summary>
    public static void Write(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>一時ファイルへ書いてからリネームで置換する（非同期）。</summary>
    public static async Task WriteAsync(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}
