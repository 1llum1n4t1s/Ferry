using System;
using System.IO;

namespace Ferry.Util;

/// <summary>
/// 例外を UI 表示用のローカライズ済み文言へ変換するヘルパー（rere #U11 / #F-001）。
/// 生の <see cref="Exception.Message"/>（多くは .NET/OS 由来の英語・内部用語）を UI に出さず、
/// 原因種別だけをロケール文言で伝える。詳細（型名・スタックトレース）はログ側に残す前提。
/// </summary>
public static class ErrorText
{
    /// <summary>例外の種別から UI 表示用のローカライズ文言を返す。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => App.Text("Transfer.Error.Access"),
        IOException => App.Text("Transfer.Error.Io"),
        _ => App.Text("Transfer.Error.Generic"),
    };
}
