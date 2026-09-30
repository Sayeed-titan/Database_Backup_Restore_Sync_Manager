using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class TransferView : UserControl
{
    public TransferView()
    {
        InitializeComponent();
        if (DataContext is TransferViewModel vm)
            vm.LogLines.CollectionChanged += (_, _) =>
            {
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            };
    }
}
