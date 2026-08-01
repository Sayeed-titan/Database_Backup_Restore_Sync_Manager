using System;
using System.Threading.Tasks;
using System.Windows;
using PgBackupManager.UI.Services;

namespace PgBackupManager.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var v = UpdateService.CurrentVersion;
        VersionBadge.Text = $"v{v.Major}.{v.Minor}";

        // Fire-and-forget, silent unless it actually finds something newer —
        // never delay startup on a network call, and never let one fail loudly.
        _ = CheckForUpdatesOnStartupAsync();
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

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
