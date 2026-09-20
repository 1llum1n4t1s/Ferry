using System;

namespace Ferry.Util;

/// <summary>コード貼付ペアリングで共有する短命コードの生成・解析。</summary>
public static class PairingCode
{
    private const char Separator = '.';

    /// <summary>永続 deviceId と現在の単回使用 nonce を連結する。どちらかが不正なら表示しない。</summary>
    public static string Create(string deviceId, string nonce)
    {
        if (!TryNormalizeHexGuid(deviceId, out var normalizedDeviceId)
            || !TryNormalizeHexGuid(nonce, out var normalizedNonce))
            return string.Empty;

        return $"{normalizedDeviceId}{Separator}{normalizedNonce}";
    }

    /// <summary>表示コードを deviceId と nonce に分解する。旧 deviceId 単体は受理しない。</summary>
    public static bool TryParse(string? code, out string deviceId, out string nonce)
    {
        deviceId = string.Empty;
        nonce = string.Empty;
        if (string.IsNullOrWhiteSpace(code)) return false;

        var parts = code.Trim().Split(Separator);
        if (parts.Length != 2
            || !TryNormalizeHexGuid(parts[0], out deviceId)
            || !TryNormalizeHexGuid(parts[1], out nonce))
        {
            deviceId = string.Empty;
            nonce = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryNormalizeHexGuid(string value, out string normalized)
    {
        if (Guid.TryParseExact(value, "N", out var parsed))
        {
            normalized = parsed.ToString("N");
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}
