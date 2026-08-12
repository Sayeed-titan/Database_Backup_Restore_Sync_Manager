using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class MsSqlRestoreView : UserControl
{
    public MsSqlRestoreView()
    {
        InitializeComponent();
        if (DataContext is MsSqlRestoreViewModel vm)
            vm.LogLines.CollectionChanged += (_, __) => LogScroll.ScrollToEnd();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && DataContext is MsSqlRestoreViewModel vm2)
                vm2.ReloadProfiles();
        };
    }
}
