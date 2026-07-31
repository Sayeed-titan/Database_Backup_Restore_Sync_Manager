using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Views;

namespace PgBackupManager.UI.Services;

// Fires the completion snackbar + taskbar flash for backup/restore, gated by
// the user's Settings toggles. Both work while MainWindow is minimized:
// ToastWindow is an independent top-level window, and FlashWindowEx operates
// on the HWND directly regardless of window state.
public static class NotificationService
{
    public static void NotifyCompletion(string title, string message, bool success)
    {
        var settings = new SettingsStore().Load();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (settings.NotifyOnCompletion)
                ToastWindow.Show(title, message, success, settings.NotificationDurationSeconds);

            if (settings.FlashTaskbarOnCompletion)
                FlashTaskbar();
        });
    }

    public static void FlashTaskbar()
    {
        var window = Application.Current?.MainWindow;
        if (window is null) return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = handle,
            dwFlags = FlashwTray | FlashwTimernofg,
            uCount = 0,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    // Flash the taskbar button until the user brings the window to the
    // foreground themselves — not a fixed count, since we don't know how long
    // they'll be away from the app.
    private const uint FlashwTray = 0x00000002;
    private const uint FlashwTimernofg = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
