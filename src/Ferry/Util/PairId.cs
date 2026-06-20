using System;

namespace Ferry.Util;

/// <summary>
/// 2 つの deviceId から pairId（<c>pairs/{pairId}</c> / <c>signaling/{pairId}</c> のキー）を
/// 導出する純関数ヘルパー。両端が同じ pairId を計算できるよう、Ordinal 比較で小さい側を前置する
/// 順序非依存の規約を 1 箇所に集約する。
///
/// この規約が複数箇所に分散すると、片方の比較規則を変えたときに pairId 不一致 → SSoT
/// (<c>pairs/{pairId}</c>) のキーずれで remote unpair 検出が静かに壊れるため、
/// <see cref="Ferry.Services.ConnectionService"/> と <see cref="Ferry.Services.PairSyncService"/> の
/// 双方からここを呼ぶ（単一定義点）。
/// </summary>
public static class PairId
{
    /// <summary>2 つの deviceId から順序非依存の pairId（<c>"{小}_{大}"</c>）を生成する。</summary>
    public static string Generate(string a, string b)
        => string.Compare(a, b, StringComparison.Ordinal) < 0 ? $"{a}_{b}" : $"{b}_{a}";
}
