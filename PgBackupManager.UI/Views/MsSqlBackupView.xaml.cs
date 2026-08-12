using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class MsSqlBackupView : UserControl
{
    public MsSqlBackupView()
    {
        InitializeComponent();
        if (DataContext is MsSqlBackupViewModel vm)
            vm.LogLines.CollectionChanged += (_, __) => LogScroll.ScrollToEnd();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && DataContext is MsSqlBackupViewModel vm2)
                vm2.ReloadProfiles();
        };
    }
}
