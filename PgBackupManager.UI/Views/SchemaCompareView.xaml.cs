using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class SchemaCompareView : UserControl
{
    public SchemaCompareView() => InitializeComponent();

    // One selected row = "script only this"? No — a single click is just for viewing
    // its definitions, so only a multi-row selection narrows the script.
    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var selected = DiffGrid.SelectedItems.OfType<SchemaDiffRow>().ToList();
        ((SchemaCompareViewModel)DataContext).GenerateScript(selected.Count > 1 ? selected : new());
    }
}
