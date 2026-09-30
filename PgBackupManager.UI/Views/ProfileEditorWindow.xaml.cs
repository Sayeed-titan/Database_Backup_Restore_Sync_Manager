using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class ProfileEditorWindow : Window
{
    private readonly ProfileEditorViewModel _vm;
    public ConnectionProfile Profile => _vm.Source;

    public ProfileEditorWindow(ConnectionProfile profile, bool isEdit = true)
    {
        InitializeComponent();
        _vm = new ProfileEditorViewModel(profile);
        DataContext = _vm;

        TitleText.Text = isEdit ? "Edit Connection Profile" : "Add Connection Profile";
        PasswordInput.Password = SecretProtector.Unprotect(profile.EncryptedPasswordBase64);
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // Tests the values currently in the form (a throwaway copy — nothing is
    // saved), so a typo is caught before the profile lands in every dropdown.
    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        var copy = JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(_vm.Source))!;
        var probe = new ProfileEditorViewModel(copy) { };
        CopyInto(probe);
        probe.Apply();

        TestBtn.IsEnabled = false;
        TestResultText.Foreground = (System.Windows.Media.Brush)FindResource("Secondary");
        TestResultText.Text = "Testing...";
        var result = await ConnectionTester.TestAsync(copy, PasswordInput.Password);
        TestBtn.IsEnabled = true;
        TestResultText.Foreground = (System.Windows.Media.Brush)FindResource(result.Ok ? "StatusGreen" : "StatusRed");
        TestResultText.Text = result.Ok
            ? $"OK in {result.Elapsed.TotalMilliseconds:F0} ms — {result.ServerVersion}"
            : $"FAILED: {result.Message}";
    }

    private void CopyInto(ProfileEditorViewModel p)
    {
        p.Name = _vm.Name; p.Engine = _vm.Engine; p.Host = _vm.Host; p.Port = _vm.Port;
        p.Database = _vm.Database; p.Username = _vm.Username; p.DefaultSchema = _vm.DefaultSchema;
        p.ExtraOptions = _vm.ExtraOptions; p.SqlIntegratedSecurity = _vm.SqlIntegratedSecurity;
        p.OracleUseSid = _vm.OracleUseSid; p.Password = PasswordInput.Password;
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.Name))
        {
            MessageBox.Show(this, "Display name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _vm.Password = PasswordInput.Password;
        _vm.Apply();
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
