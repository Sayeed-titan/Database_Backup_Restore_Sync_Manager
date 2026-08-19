using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PgBackupManager.Core.Services;

namespace PgBackupManager.UI.Views;

// Custom-styled replacement for MessageBox.Show — the app is a chromeless,
// custom-titlebar window (see MainWindow.xaml), so a native MessageBox pops up
// looking like a completely different app. Matches the same card/button
// pattern already used by ProfileEditorWindow.
public partial class ConfirmDialog : Window
{
    public bool Result { get; private set; }

    // Set by ShowVersionMismatch() only, when no already-installed version
    // matches the target server but PgToolsDownloader knows how to fetch one.
    private int? _downloadMajorVersion;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) { Result = false; Close(); }
    private void CancelBtn_Click(object sender, RoutedEventArgs e) { Result = false; Close(); }
    private void ConfirmBtn_Click(object sender, RoutedEventArgs e) { Result = true; Close(); }

    // Opens the download dialog as a nested modal; on success this dialog
    // closes with Result=true too, same as picking an install to switch to,
    // so the caller's "click Start again" messaging applies either way.
    private void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadMajorVersion is not int major) return;

        var dlg = new PgToolsDownloadDialog(major) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.InstalledBinDir != null)
        {
            var store = new SettingsStore();
            var settings = store.Load();
            settings.PgBinDirOverride = dlg.InstalledBinDir;
            store.Save(settings);
            Result = true;
            Close();
        }
    }

    /// <summary>Yes/No style confirmation. Returns true only if the confirm button was clicked.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText = "Yes, proceed", bool danger = false)
    {
        var dlg = Build(owner, title, message, danger);
        dlg.ConfirmBtnText.Text = confirmText.ToUpperInvariant();
        dlg.ShowDialog();
        return dlg.Result;
    }

    /// <summary>Single acknowledgement ("OK" only) alert — no Cancel button.</summary>
    public static void Alert(Window? owner, string title, string message, bool danger = true)
    {
        var dlg = Build(owner, title, message, danger);
        dlg.CancelBtn.Visibility = Visibility.Collapsed;
        dlg.ConfirmBtnText.Text = "OK";
        dlg.ShowDialog();
    }

    /// <summary>
    /// Version-mismatch alert with a built-in one-click fix: pick a different
    /// detected PostgreSQL install and save it as the Bin Folder Override right
    /// from the dialog, instead of cancelling, going to Settings, and coming
    /// back. Returns true if the user switched — Settings are already saved to
    /// disk by the time this returns, so the caller just needs to tell them to
    /// retry (not attempt to auto-continue mid-dialog).
    /// </summary>
    public static bool ShowVersionMismatch(Window? owner, string toolKind, string detectedVersion, int serverMajor, string? currentBinDir)
    {
        var dlg = Build(owner,
            "Client tools are newer than the target server",
            $"{toolKind} {detectedVersion} is newer than the target server (PostgreSQL {serverMajor}.x).\n\n" +
            "Newer client tools can send session settings the server doesn't recognize " +
            "(e.g. \"transaction_timeout\", added in PostgreSQL 17) and the run fails immediately.\n\n" +
            "Pick a matching install below, or fix it later in Settings.",
            danger: true);

        var installs = PgToolsLocator.DetectAllInstalls(currentBinDir);
        var matchingInstall = installs.FirstOrDefault(i => i.MajorVersion == serverMajor);
        dlg.InstallPicker.ItemsSource = installs;
        dlg.InstallPicker.SelectedItem = matchingInstall ?? installs.FirstOrDefault();
        dlg.InstallPickerPanel.Visibility = installs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        dlg.CancelBtn.Content = "CLOSE";
        dlg.ConfirmBtnText.Text = "SWITCH & SAVE";
        dlg.ConfirmBtn.IsEnabled = installs.Count > 0;

        // No already-installed version matches the server — offer to fetch
        // one directly instead of just pointing the user at Settings.
        if (matchingInstall is null && PgBackupManager.Core.Services.PgToolsDownloader.IsVersionSupported(serverMajor))
        {
            dlg._downloadMajorVersion = serverMajor;
            dlg.DownloadBtn.Content = $"DOWNLOAD PG {serverMajor}";
            dlg.DownloadBtn.Visibility = Visibility.Visible;
        }

        dlg.ShowDialog();

        if (dlg.Result && dlg.InstallPicker.SelectedItem is PgInstall chosen)
        {
            var store = new SettingsStore();
            var settings = store.Load();
            settings.PgBinDirOverride = chosen.BinDir;
            store.Save(settings);
            return true;
        }
        return false;
    }

    private static ConfirmDialog Build(Window? owner, string title, string message, bool danger)
    {
        var dlg = new ConfirmDialog { Owner = owner ?? Application.Current?.MainWindow };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;

        if (danger)
        {
            dlg.IconBadge.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
            dlg.IconPath.Stroke = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
            dlg.ConfirmBtn.Style = (Style)dlg.FindResource("DangerButton");
        }

        return dlg;
    }
}
