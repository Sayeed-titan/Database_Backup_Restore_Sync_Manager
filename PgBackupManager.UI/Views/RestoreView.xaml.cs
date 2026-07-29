using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class RestoreView : UserControl
{
    public RestoreView()
    {
        InitializeComponent();
        if (DataContext is RestoreViewModel vm)
            vm.LogLines.CollectionChanged += (_, __) => LogScroll.ScrollToEnd();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && DataContext is RestoreViewModel vm2)
                vm2.ReloadProfiles();
        };
    }
}
