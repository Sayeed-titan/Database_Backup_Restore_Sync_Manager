using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && DataContext is BackupViewModel vm)
                vm.ReloadProfiles();
        };
    }
}
