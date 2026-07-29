using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
        if (DataContext is BackupViewModel vm)
            vm.LogLines.CollectionChanged += (_, __) => LogScroll.ScrollToEnd();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && DataContext is BackupViewModel vm2)
                vm2.ReloadProfiles();
        };
    }
}
