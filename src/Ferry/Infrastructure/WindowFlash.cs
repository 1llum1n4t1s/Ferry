using System;
using System.Runtime.InteropServices;

namespace Ferry.Infrastructure;

/// <summary>
/// タスクバーのウィンドウを点滅させるユーティリティ。
/// </summary>
public static partial class WindowFlash
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;      // タイトルバー + タスクバー
    private const uint FLASHW_TIMERNOFG = 12; // フォアグラウンドになるまで点滅

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>ウィンドウを点滅させる。フォアグラウンドになるまで継続。</summary>
    public static void Flash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var fwi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };
        FlashWindowEx(ref fwi);
    }
}
