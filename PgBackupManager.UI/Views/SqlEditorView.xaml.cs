using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PgBackupManager.Core.Sql;
using PgBackupManager.Core.Providers;
using PgBackupManager.UI.Controls;
using PgBackupManager.UI.ViewModels;

namespace PgBackupManager.UI.Views;

public partial class SqlEditorView : UserControl
{
    private SqlTextEditor? _editor;
    private SqlEditorViewModel Vm => (SqlEditorViewModel)DataContext;

    public SqlEditorView()
    {
        InitializeComponent();
        Vm.ErrorAtLine += (_, line) => Dispatcher.BeginInvoke(() => JumpToLine(line));
        Vm.InsertRequested += (_, text) => Dispatcher.BeginInvoke(() =>
        {
            if (_editor == null) return;
            _editor.Document.Insert(_editor.CaretOffset, text);
            _editor.Focus();
        });
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        _editor = (SqlTextEditor)sender;
        _editor.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if ((e.Key == Key.Enter && ctrl && shift)) { RunStatement_Click(this, e); e.Handled = true; }
        else if ((e.Key == Key.Enter && ctrl) || e.Key == Key.F5) { Run_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.E && ctrl) { Explain_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.S && ctrl) { Vm.SaveCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.T && ctrl) { Vm.NewTabCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.O && ctrl) { Vm.OpenFileCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.W && ctrl) { Vm.CloseTabCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape && Vm.IsBusy) { Vm.CancelCommand.Execute(null); e.Handled = true; }
    }

    // Selection if there is one, otherwise the whole document.
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_editor == null || Vm.IsBusy) return;
        if (_editor.SelectionLength > 0)
        {
            var line = _editor.Document.GetLineByOffset(_editor.SelectionStart).LineNumber - 1;
            await Vm.RunAsync(_editor.SelectedText, line);
        }
        else await Vm.RunAsync(_editor.Text, 0);
    }

    private async void RunStatement_Click(object sender, RoutedEventArgs e)
    {
        if (_editor == null || Vm.IsBusy) return;
        var (text, line) = StatementAtCaret();
        if (text != null) await Vm.RunAsync(text, line - 1);
    }

    private async void Explain_Click(object sender, RoutedEventArgs e)
    {
        if (_editor == null || Vm.IsBusy) return;
        var (text, line) = _editor.SelectionLength > 0 ? (_editor.SelectedText, 1) : StatementAtCaret();
        if (text == null) return;
        await Vm.RunAsync(Vm.BuildExplain(text), line - 1);
    }

    private (string? Text, int Line) StatementAtCaret()
    {
        if (_editor == null || Vm.SelectedProfile == null) return (null, 0);
        var dialect = DbProviders.For(Vm.SelectedProfile).Dialect;
        var caretLine = _editor.TextArea.Caret.Line;
        var stmts = SqlScriptSplitter.SplitDetailed(_editor.Text, dialect).Where(s => s.Kind != StatementKind.Meta).ToList();
        var st = stmts.LastOrDefault(s => s.StartLine <= caretLine) ?? stmts.FirstOrDefault();
        if (st == null) return (null, 0);
        // Re-attach the terminator the splitter strips, where the engine needs/accepts it.
        var text = dialect == ScriptDialect.Oracle || dialect == ScriptDialect.SqlServer ? st.Text : st.Text + ";";
        return (text, st.StartLine);
    }

    private void JumpToLine(int line)
    {
        if (_editor == null || line < 1 || line > _editor.Document.LineCount) return;
        var l = _editor.Document.GetLineByNumber(line);
        _editor.Select(l.Offset, l.Length);
        _editor.ScrollToLine(line);
        _editor.Focus();
    }

    // Header as a TextBlock (keeps underscores — a plain string header would
    // turn "_x" into an access key) and an indexer binding (handles column
    // names containing spaces, dots, slashes...).
    private void ResultGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var name = e.PropertyName;
        if (e.Column is DataGridTextColumn tc)
        {
            tc.Binding = new Binding($"[{name.Replace("]", "\\]")}]") { Mode = BindingMode.OneWay };
            tc.ElementStyle = (Style)FindResource("NullCell");
            tc.MaxWidth = 520;
        }
        e.Column.Header = new TextBlock { Text = name.Replace("·", ".") };
        e.Column.SortMemberPath = name;
    }

    private void ResultChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedTab != null) Vm.SelectedTab.ShowMessages = false;
    }

    private void History_DoubleClick(object sender, MouseButtonEventArgs e) =>
        Vm.OpenHistoryCommand.Execute(HistoryList.SelectedItem);

    private void View_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop))
            if (File.Exists(f)) Vm.OpenPath(f);
        e.Handled = true;
    }

    // ---------------------------------------------------------- explorer

    private ObjectNodeVm? SelectedNode => ExplorerTree.SelectedItem as ObjectNodeVm;

    private void ExplorerTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedNode is { } n && n.Kind is not ("group" or "info" or "schema")) Vm.InsertNameCommand.Execute(n);
    }

    private void ExplorerMenu_Opened(object sender, RoutedEventArgs e)
    {
        var n = SelectedNode;
        foreach (var item in ExplorerMenu.Items.OfType<MenuItem>())
        {
            var h = item.Header?.ToString() ?? "";
            item.IsEnabled = n != null && n.Kind != "info" && h switch
            {
                "Select top 100" => n.IsTableLike,
                "Script source / DDL" => n.Object != null,
                "Export data to file…" => n.IsTableLike,
                "Import CSV into this table / schema…" => n.Kind is "table" or "schema",
                "Convert code to another engine…" => n.IsCode || n.Kind == "table",
                "Transfer to another database…" => n.Kind is "table" or "schema",
                _ => n.Kind != "group",
            };
        }
    }

    private void Menu_SelectTop(object sender, RoutedEventArgs e) => Vm.SelectTopCommand.Execute(SelectedNode);
    private void Menu_Script(object sender, RoutedEventArgs e) => Vm.ScriptObjectCommand.Execute(SelectedNode);
    private void Menu_Insert(object sender, RoutedEventArgs e) => Vm.InsertNameCommand.Execute(SelectedNode);
    private void Menu_Copy(object sender, RoutedEventArgs e) => Vm.CopyNameCommand.Execute(SelectedNode);
    private void Menu_Export(object sender, RoutedEventArgs e) => Vm.ExportObjectCommand.Execute(SelectedNode);
    private void Menu_Import(object sender, RoutedEventArgs e) => Vm.ImportCsvCommand.Execute(SelectedNode);
    private void Menu_Convert(object sender, RoutedEventArgs e) => Vm.ConvertObjectCommand.Execute(SelectedNode);
    private void Menu_Transfer(object sender, RoutedEventArgs e) => Vm.TransferObjectCommand.Execute(SelectedNode);
}
