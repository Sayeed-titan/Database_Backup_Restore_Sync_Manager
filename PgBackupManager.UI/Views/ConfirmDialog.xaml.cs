using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PgBackupManager.UI.Views;

// Custom-styled replacement for MessageBox.Show — the app is a chromeless,
// custom-titlebar window (see MainWindow.xaml), so a native MessageBox pops up
// looking like a completely different app. Matches the same card/button
// pattern already used by ProfileEditorWindow.
public partial class ConfirmDialog : Window
{
    public bool Result { get; private set; }

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
