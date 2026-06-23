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
