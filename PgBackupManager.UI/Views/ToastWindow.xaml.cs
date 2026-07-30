using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PgBackupManager.UI.Views;

// Snackbar-style completion toast. Deliberately an independent top-level
// window with no Owner — that's what lets it stay visible on screen even
// while MainWindow is minimized, which is the entire point of it existing
// instead of an in-window banner.
public partial class ToastWindow : Window
{
    private static readonly List<ToastWindow> _active = new();
    private DispatcherTimer? _dismissTimer;

    public ToastWindow()
    {
        InitializeComponent();
    }

    public static void Show(string title, string message, bool success, int durationSeconds)
    {
        var win = new ToastWindow();
        win.TitleText.Text = title;
        win.MessageText.Text = message;

        var accent = (Brush)Application.Current.Resources[success ? "StatusGreen" : "StatusRed"];
        win.IconBadge.Background = new SolidColorBrush(success
            ? Color.FromRgb(0xDC, 0xFC, 0xE7)
            : Color.FromRgb(0xFE, 0xE2, 0xE2));
        win.IconPath.Stroke = accent;
        win.ProgressFill.Background = accent;
        win.IconPath.Data = Geometry.Parse(success
            ? "M12 2 a10 10 0 1 0 0.001 0 Z M7 12 l3 3 l7 -7"
            : "M12 2 a10 10 0 1 0 0.001 0 Z M8 8 L16 16 M16 8 L8 16");

        // Stack above whatever toasts are already on screen instead of
        // overlapping them — measure before Show() so it lands in the right
        // spot on first paint rather than jumping after layout.
        win.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var area = SystemParameters.WorkArea;
        double stacked = 0;
        foreach (var t in _active) stacked += (t.ActualHeight > 0 ? t.ActualHeight : 90) + 10;
        win.Left = area.Right - win.Width - 16;
        win.Top = area.Bottom - Math.Max(win.DesiredSize.Height, 80) - 16 - stacked;

        _active.Add(win);
        win.Opacity = 0;
        win.Show();
        win.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

        var seconds = Math.Max(2, durationSeconds);
        var scale = (ScaleTransform)win.ProgressFill.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(seconds)));

        win._dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        win._dismissTimer.Tick += (_, _) => win.Dismiss();
        win._dismissTimer.Start();
    }

    private void Dismiss()
    {
        _dismissTimer?.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Dismiss();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _active.Remove(this);
    }
}
