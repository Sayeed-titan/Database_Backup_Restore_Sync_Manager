using System;
using System.Linq;
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

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
