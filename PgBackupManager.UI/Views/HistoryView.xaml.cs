using System.Windows.Controls;
using System.Windows.Input;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        ((HistoryViewModel)DataContext).OpenLogCommand.Execute(null);
}
