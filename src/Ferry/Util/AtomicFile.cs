using System;
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
        // rere PR#8 #F1: tmp 名を path+".tmp" 固定にすると、同一 path への並行 Write や
        // 前回クラッシュの残骸 tmp と衝突して取り違え・部分書き残骸が起こりうる。
        // 衝突しない GUID 付き一時名を使い、Move 失敗時も finally で残骸を掃除する。
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            TryCleanup(tmp);
        }
    }

    /// <summary>一時ファイルへ書いてからリネームで置換する（非同期）。</summary>
    public static async Task WriteAsync(string path, byte[] data)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            TryCleanup(tmp);
        }
    }

    /// <summary>Move 成功後は tmp が消えているので no-op、Move 失敗時のみ残骸を掃除する（best-effort）。</summary>
    private static void TryCleanup(string tmp)
    {
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch { /* 掃除失敗は無視（次回は別の GUID 名なので衝突しない） */ }
    }
}
