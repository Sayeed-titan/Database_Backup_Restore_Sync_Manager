using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class RestoreView : UserControl
{
    public RestoreView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && DataContext is RestoreViewModel vm)
                vm.ReloadProfiles();
        };
    }
}
