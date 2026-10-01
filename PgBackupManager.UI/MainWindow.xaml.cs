using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;

namespace PgBackupManager.UI;

public partial class MainWindow : Window
{
    private const string SunGlyph = "M12 7 A5 5 0 1 0 12 17 A5 5 0 0 0 12 7 M12 1 V3 M12 21 V23 M4.2 4.2 L5.6 5.6 M18.4 18.4 L19.8 19.8 M1 12 H3 M21 12 H23 M4.2 19.8 L5.6 18.4 M18.4 5.6 L19.8 4.2";
    private const string MoonGlyph = "M21 12.8 A9 9 0 1 1 11.2 3 A7 7 0 0 0 21 12.8 Z";

    public MainWindow()
    {
        InitializeComponent();

        var v = UpdateService.CurrentVersion;
        VersionBadge.Text = $"v{v.Major}.{v.Minor}";

        // Reopen on the page the user was last on.
        var last = new SettingsStore().Load().LastPage;
        var page = Nav.Items.OfType<TabItem>().FirstOrDefault(t => t.Name == last && t.IsEnabled)
                   ?? Nav.Items.OfType<TabItem>().First(t => t.Name == "Backup");
        page.IsSelected = true;

        Navigator.PageRequested += (_, name) => Dispatcher.Invoke(() =>
        {
            var tab = Nav.Items.OfType<TabItem>().FirstOrDefault(t => t.Name == name);
            if (tab != null) tab.IsSelected = true;
        });

        ThemeService.ThemeChanged += (_, _) => UpdateThemeGlyph();
        UpdateThemeGlyph();

        // Borderless window + WindowChrome: without this, a maximized window is
        // sized ~7px past every screen edge and over the taskbar, hiding the
        // min/max/close buttons. WM_GETMINMAXINFO pins it to the work area.
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        StateChanged += (_, _) => UpdateWindowStateVisuals();
        UpdateWindowStateVisuals();

        // Fire-and-forget, silent unless it actually finds something newer —
        // never delay startup on a network call, and never let one fail loudly.
        _ = CheckForUpdatesOnStartupAsync();
    }

    private void UpdateThemeGlyph() =>
        ThemeGlyph.Data = System.Windows.Media.Geometry.Parse(ThemeService.IsDark ? SunGlyph : MoonGlyph);

    private void ThemeBtn_Click(object sender, RoutedEventArgs e) =>
        ThemeService.ApplyAndSave(ThemeService.IsDark ? "Light" : "Dark", ThemeService.CurrentAccent);

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Nav || Nav.SelectedItem is not TabItem { Name: { Length: > 0 } name }) return;
        try
        {
            var store = new SettingsStore();
            var s = store.Load();
            if (s.LastPage == name) return;
            s.LastPage = name;
            store.Save(s);
        }
        catch { /* remembering the page is a nicety only */ }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await UpdateService.CheckAsync(silent: true);
        }
        catch { /* best-effort only — never disrupt startup */ }
    }

    private const string MaximizeGlyph = "M4 4 H20 V20 H4 Z";
    private const string RestoreGlyph = "M8 8 H20 V20 H8 Z M4 16 V4 H16";

    private void UpdateWindowStateVisuals()
    {
        var max = WindowState == WindowState.Maximized;
        RootBorder.BorderThickness = new Thickness(max ? 0 : 1);
        MaxGlyph.Data = Geometry.Parse(max ? RestoreGlyph : MaximizeGlyph);
        MaxBtn.ToolTip = max ? "Restore down" : "Maximize";
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        RECT work = info.rcWork, mon = info.rcMonitor;
        // Position is relative to the monitor; size is the work area (taskbar excluded).
        mmi.ptMaxPosition.X = work.Left - mon.Left;
        mmi.ptMaxPosition.Y = work.Top - mon.Top;
        mmi.ptMaxSize.X = work.Right - work.Left;
        mmi.ptMaxSize.Y = work.Bottom - work.Top;
        // Keep MinWidth/MinHeight in device pixels too.
        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.X = (int)(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.Y = (int)(MinHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
