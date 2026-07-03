using System;
using System.IO;
using System.Net.Sockets;

namespace Ferry.Util;

/// <summary>
/// 例外を UI 表示用のローカライズ済み文言へ変換するヘルパー（rere #U11 / #F-001）。
/// 生の <see cref="Exception.Message"/>（多くは .NET/OS 由来の英語・内部用語）を UI に出さず、
/// 原因種別だけをロケール文言で伝える。詳細（型名・スタックトレース）はログ側に残す前提。
/// </summary>
public static class ErrorText
{
    // CodeRabbit #3516884778: Describe は TransferService の受信処理（Task.Run 上のバックグラウンド
    // スレッド）から呼ばれる。App.Text() は Avalonia の TryGetResource/MergedDictionaries を参照し、
    // ロケール切替（UI スレッドでの MergedDictionaries 変更）と競合するとスレッド安全性の懸念がある。
    // 文言は UI スレッドである App.SetLocale からのみ更新し、Describe は任意スレッドから安全に読める
    // キャッシュだけを参照する（string 参照の読み書きはアトミックなのでロック不要。切替直後の一瞬だけ
    // 旧ロケールの文言になりうるが実害はない）。
    private static string _accessText = "Text.Transfer.Error.Access";
    private static string _ioText = "Text.Transfer.Error.Io";
    private static string _genericText = "Text.Transfer.Error.Generic";

    /// <summary>ロケール確定/切替時に UI スレッドから呼び、キャッシュ文言を更新する。</summary>
    public static void RefreshCache()
    {
        _accessText = App.Text("Transfer.Error.Access");
        _ioText = App.Text("Transfer.Error.Io");
        _genericText = App.Text("Transfer.Error.Generic");
    }

    /// <summary>例外の種別から UI 表示用のローカライズ文言を返す（任意スレッドから呼び出し可）。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => _accessText,
        // NetworkStream の切断は IOException が SocketException を内包する形で飛んでくる。
        // ディスク由来の IOException（容量不足・ロック等）と混同してディスクエラー文言を
        // 誤表示しないよう、ネットワーク起因は Generic 文言に振り分ける（Codex #3516870401）。
        IOException { InnerException: SocketException } => _genericText,
        IOException => _ioText,
        _ => _genericText,
    };
}
