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

    // rere レビュー #C-09: サービス層が UI へ出す定型文言も同じキャッシュ方式で持つ。
    //
    // 旧実装は TransferService / ConnectionService が ErrorMessage や戻り値へ日本語リテラルを
    // 直代入していた。TransferItem.StateText は `ErrorMessage ?? App.Text("State.Error")` なので、
    // 日本語リテラルが常にローカライズ済みフォールバックを上書きし、18 言語を出荷していても
    // 「一番読まれる欄（失敗理由）だけ日本語」という状態になっていた。
    // これらは任意スレッド（受信 Task.Run 上）から読まれるので、Describe と同じく
    // UI スレッドの RefreshCache でだけ更新するキャッシュにする。
    private static string _disconnected = "Text.Transfer.Error.Disconnected";
    private static string _rejectedByPeer = "Text.Transfer.Error.RejectedByPeer";
    private static string _sizeExceeded = "Text.Transfer.Error.SizeExceeded";
    private static string _hashMismatch = "Text.Transfer.Error.HashMismatch";
    private static string _cancelled = "Text.Transfer.Error.Cancelled";
    private static string _receiveRejected = "Text.Transfer.Error.ReceiveRejected";

    /// <summary>接続が切断された。</summary>
    public static string Disconnected => _disconnected;
    /// <summary>相手が受信を拒否した。</summary>
    public static string RejectedByPeer => _rejectedByPeer;
    /// <summary>受信データが申告サイズを超過した。</summary>
    public static string SizeExceeded => _sizeExceeded;
    /// <summary>SHA-256 の整合性検証に失敗した。</summary>
    public static string HashMismatch => _hashMismatch;
    /// <summary>ユーザー操作でキャンセルされた。</summary>
    public static string Cancelled => _cancelled;
    /// <summary>自分が受信を拒否した。</summary>
    public static string ReceiveRejected => _receiveRejected;

    /// <summary>ロケール確定/切替時に UI スレッドから呼び、キャッシュ文言を更新する。</summary>
    public static void RefreshCache()
    {
        _accessText = App.Text("Transfer.Error.Access");
        _ioText = App.Text("Transfer.Error.Io");
        _genericText = App.Text("Transfer.Error.Generic");

        _disconnected = App.Text("Transfer.Error.Disconnected");
        _rejectedByPeer = App.Text("Transfer.Error.RejectedByPeer");
        _sizeExceeded = App.Text("Transfer.Error.SizeExceeded");
        _hashMismatch = App.Text("Transfer.Error.HashMismatch");
        _cancelled = App.Text("Transfer.Error.Cancelled");
        _receiveRejected = App.Text("Transfer.Error.ReceiveRejected");
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
