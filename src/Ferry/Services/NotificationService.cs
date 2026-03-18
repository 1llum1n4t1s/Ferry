using System;
using System.Runtime.InteropServices;

namespace Ferry.Services;

/// <summary>通知関連のサービス（タスクバー点滅・受信音再生）。</summary>
public sealed partial class NotificationService : INotificationService
{
    private readonly ISettingsService _settingsService;

    /// <summary>SND_ASYNC: 非同期再生。SND_ALIAS: レジストリエイリアス指定。</summary>
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_ALIAS = 0x00010000;

    [LibraryImport("winmm.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(string pszSound, IntPtr hmod, uint fdwSound);

    public NotificationService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>メッセージ受信通知を発行する。</summary>
    public void NotifyMessageReceived(string peerId, string senderName, string preview)
    {
        // ピアがミュートされている場合はスキップ
        if (_settingsService.Settings.MutedPeerIds.Contains(peerId)) return;

        // サウンド再生
        if (_settingsService.Settings.EnableNotificationSound)
        {
            PlayNotificationSound();
        }
    }

    /// <summary>Windows 標準の通知音を P/Invoke で再生する。</summary>
    private static void PlayNotificationSound()
    {
        try
        {
            // "SystemAsterisk" は Windows の標準通知音エイリアス
            PlaySoundW("SystemAsterisk", IntPtr.Zero, SND_ASYNC | SND_ALIAS);
        }
        catch
        {
            // サウンド再生失敗は無視
        }
    }
}
