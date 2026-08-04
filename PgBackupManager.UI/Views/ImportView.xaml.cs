using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class ImportView : UserControl
{
    public ImportView()
    {
        InitializeComponent();
    }

    private void MssqlPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ImportViewModel vm)
            vm.MssqlPassword = MssqlPasswordBox.Password;
    }
}
