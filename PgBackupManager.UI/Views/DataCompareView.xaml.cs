using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class DataCompareView : UserControl
{
    public DataCompareView() => InitializeComponent();

    private void Script_Click(object sender, RoutedEventArgs e) =>
        ((DataCompareViewModel)DataContext).GenerateScript(DiffGrid.SelectedItems.OfType<DataDiffRowVm>().ToList());

    // A single click only shows that row's values; 2+ selected rows narrow Apply.
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selected = DiffGrid.SelectedItems.OfType<DataDiffRowVm>().ToList();
        await ((DataCompareViewModel)DataContext).ApplyAsync(selected, Window.GetWindow(this)!);
    }
}
