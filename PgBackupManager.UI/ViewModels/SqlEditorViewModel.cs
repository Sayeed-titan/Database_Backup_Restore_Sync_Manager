using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Services;
using PgBackupManager.Core.Sql;
using PgBackupManager.UI.Controls;
using PgBackupManager.UI.Services;

namespace PgBackupManager.UI.ViewModels;

public partial class ResultSetVm : ObservableObject
{
    public string Header { get; init; } = "";
    public DataTable Table { get; init; } = new();
    public DataView View => Table.DefaultView;
    public string Sql { get; init; } = "";
    public bool Truncated { get; init; }
    public string Info => $"{Table.Rows.Count:N0} row(s){(Truncated ? " — more available (limit reached; use Export for everything)" : "")}";
}

public partial class QueryTabVm : ObservableObject
{
    private static int _counter = 1;
    [ObservableProperty] private string _title = $"Query {_counter++}";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private ResultSetVm? _selectedResult;
    [ObservableProperty] private bool _showMessages;
    public ObservableCollection<ResultSetVm> Results { get; } = new();
    public ObservableCollection<string> Messages { get; } = new();
    public string Header => (IsDirty ? "● " : "") + Title;
    partial void OnCodeChanged(string value) => IsDirty = true;
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Header));
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(Header));
}

public partial class ObjectNodeVm : ObservableObject
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";       // schema, group, table, view, function, procedure, package, sequence, trigger, type, column, info
    public string Detail { get; init; } = "";
    public string Schema { get; init; } = "";
    public DbObjectInfo? Object { get; init; }
    public ObservableCollection<ObjectNodeVm> Children { get; } = new();
    public Func<ObjectNodeVm, Task>? Loader { get; init; }
    private bool _loaded;
    [ObservableProperty] private bool _isExpanded;
    public string Glyph => Kind switch
    {
        "schema" => "M3 7 L12 3 L21 7 L12 11 Z M3 12 L12 16 L21 12 M3 17 L12 21 L21 17",
        "group" => "M3 6 H10 L12 8 H21 V19 H3 Z",
        "table" => "M3 4 H21 V20 H3 Z M3 10 H21 M3 15 H21 M9 10 V20",
        "view" => "M1 12 C4 6 20 6 23 12 C20 18 4 18 1 12 Z M12 9 A3 3 0 1 0 12 15 A3 3 0 0 0 12 9",
        "column" => "M6 4 V20 M6 4 H18 M6 12 H14",
        "key" => "M15 7 A4 4 0 1 0 15 15 A4 4 0 0 0 15 7 M11 11 H3 V14 M6 11 V14",
        "package" => "M21 8 L12 3 L3 8 V16 L12 21 L21 16 Z M3 8 L12 13 L21 8 M12 13 V21",
        "sequence" => "M4 6 H8 M4 12 H14 M4 18 H20",
        _ => "M8 7 L3 12 L8 17 M16 7 L21 12 L16 17",
    };
    public bool IsCode => Kind is "view" or "function" or "procedure" or "package" or "trigger" or "sequence" or "type";
    public bool IsTableLike => Kind is "table" or "view";

    public void AddPlaceholder() => Children.Add(new ObjectNodeVm { Name = "loading…", Kind = "info" });

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded || Loader == null) return;
        _loaded = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try { await Loader!(this); }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new ObjectNodeVm { Name = "error: " + ex.Message, Kind = "info" });
        }
    }

    public void Reset() { _loaded = false; Children.Clear(); AddPlaceholder(); }
}

public sealed record QueryHistoryEntry(DateTime When, string Profile, string Sql)
{
    public string Preview => Sql.Replace("\r", " ").Replace("\n", " ").Trim() is var s && s.Length > 110 ? s[..110] + "…" : Sql.Replace("\r", " ").Replace("\n", " ").Trim();
    public string WhenText => When.ToString("MMM d, HH:mm");
}

public partial class SqlEditorViewModel : ObservableObject, ISqlCompletionSource
{
    private readonly ProfileStore _profileStore = new();
    private DbConnection? _conn;
    private DbTransaction? _tx;
    private IDbProvider? _provider;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, List<DbObjectInfo>> _objectCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ColumnInfo>> _columnCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string HistoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PgBackupManager", "query_history.json");

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _selectedProfile;
    public ObservableCollection<QueryTabVm> Tabs { get; } = new();
    [ObservableProperty] private QueryTabVm? _selectedTab;
    public ObservableCollection<string> Schemas { get; } = new();
    [ObservableProperty] private string? _selectedSchema;
    public ObservableCollection<ObjectNodeVm> Explorer { get; } = new();
    public ObservableCollection<QueryHistoryEntry> History { get; } = new();
    [ObservableProperty] private string _explorerFilter = "";

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _autoCommit = true;
    [ObservableProperty] private bool _inTransaction;
    [ObservableProperty] private bool _stopOnError = true;
    [ObservableProperty] private string _connectionText = "Not connected";
    [ObservableProperty] private string _elapsedText = "";

    // Raised with the 1-based absolute line of the first failing statement.
    public event EventHandler<int>? ErrorAtLine;

    public SqlEditorViewModel()
    {
        ReloadProfiles();
        ProfileStore.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(ReloadProfiles);
        Navigator.OpenSqlRequested += (_, r) => Application.Current?.Dispatcher.Invoke(() =>
        {
            var tab = NewTabWith(r.Title, r.Sql);
            if (r.ProfileId.HasValue && Profiles.FirstOrDefault(p => p.Id == r.ProfileId) is { } prof) SelectedProfile = prof;
            tab.IsDirty = false;
        });
        LoadHistory();
        var t = NewTabWith("Query 1", "-- Ctrl+Enter / F5 runs the selection (or everything), Ctrl+Shift+Enter runs the statement at the cursor.\n-- Ctrl+Space for completions.\n\n");
        t.IsDirty = false;
    }

    public void ReloadProfiles()
    {
        var id = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var p in _profileStore.LoadAll().OrderBy(p => p.Engine).ThenBy(p => p.Name)) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        _ = DisconnectAsync();
        Explorer.Clear();
        Schemas.Clear();
    }

    partial void OnSelectedSchemaChanged(string? value)
    {
        if (value == null || !IsConnected || _provider == null) return;
        _ = SetSessionSchemaAsync(value);
    }

    partial void OnAutoCommitChanged(bool value)
    {
        if (value && _tx != null) Log("Auto-commit re-enabled — the open transaction is still pending: Commit or Rollback it.");
    }

    partial void OnExplorerFilterChanged(string value) => BuildExplorerRoots();

    // --------------------------------------------------------------- tabs

    public QueryTabVm NewTabWith(string title, string code, string? path = null)
    {
        var tab = new QueryTabVm { Title = title, Code = code, FilePath = path };
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    [RelayCommand] private void NewTab() => NewTabWith($"Query {Tabs.Count + 1}", "").IsDirty = false;

    [RelayCommand]
    private void CloseTab(QueryTabVm? tab)
    {
        tab ??= SelectedTab;
        if (tab == null) return;
        if (tab.IsDirty && !string.IsNullOrWhiteSpace(tab.Code) &&
            MessageBox.Show($"Close '{tab.Title}' without saving?", "Unsaved query", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (Tabs.Count == 0) NewTab();
        SelectedTab = Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "SQL scripts (*.sql;*.txt;*.pks;*.pkb;*.prc;*.fnc;*.trg)|*.sql;*.txt;*.pks;*.pkb;*.prc;*.fnc;*.trg|All files (*.*)|*.*", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) OpenPath(f);
    }

    public void OpenPath(string f)
    {
        var info = new FileInfo(f);
        if (info.Length > 20 * 1024 * 1024)
        {
            if (MessageBox.Show($"{info.Name} is {info.Length / 1024 / 1024:N0} MB — too big to edit comfortably.\n\nRun it directly against the current connection instead?",
                    "Large script", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _ = RunFileAsync(f);
            return;
        }
        var tab = NewTabWith(Path.GetFileName(f), File.ReadAllText(f), f);
        tab.IsDirty = false;
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedTab == null) return;
        if (SelectedTab.FilePath == null) { SaveAs(); return; }
        File.WriteAllText(SelectedTab.FilePath, SelectedTab.Code);
        SelectedTab.IsDirty = false;
    }

    [RelayCommand]
    private void SaveAs()
    {
        if (SelectedTab == null) return;
        var dlg = new SaveFileDialog { Filter = "SQL script (*.sql)|*.sql|All files (*.*)|*.*", FileName = SelectedTab.FilePath != null ? Path.GetFileName(SelectedTab.FilePath) : SelectedTab.Title + ".sql" };
        if (dlg.ShowDialog() != true) return;
        SelectedTab.FilePath = dlg.FileName;
        SelectedTab.Title = Path.GetFileName(dlg.FileName);
        File.WriteAllText(dlg.FileName, SelectedTab.Code);
        SelectedTab.IsDirty = false;
    }

    // ------------------------------------------------------------ connect

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedProfile == null) { ConnectionText = "Pick a connection first (add one under Connections)."; return; }
        await DisconnectAsync();
        try
        {
            IsBusy = true;
            ConnectionText = $"Connecting to {SelectedProfile}...";
            _provider = DbProviders.For(SelectedProfile);
            _conn = await _provider.OpenAsync(SelectedProfile);
            IsConnected = true;
            ConnectionText = $"{_provider.Name} · {SelectedProfile} · {_conn.ServerVersion}";
            await LoadSchemasAsync();
        }
        catch (Exception ex)
        {
            ConnectionText = "Connection failed: " + ex.Message;
            _conn = null; IsConnected = false;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_tx != null) { try { await _tx.RollbackAsync(); } catch { } _tx = null; InTransaction = false; }
        if (_conn != null) { try { await _conn.DisposeAsync(); } catch { } _conn = null; }
        IsConnected = false;
        ConnectionText = "Not connected";
    }

    private async Task LoadSchemasAsync()
    {
        if (SelectedProfile == null || _provider == null) return;
        await using var meta = await _provider.OpenAsync(SelectedProfile);
        var list = await _provider.ListSchemasAsync(meta);
        Schemas.Clear();
        foreach (var s in list) Schemas.Add(s);
        var def = SelectedProfile.ResolveDefaultSchema();
        _selectedSchemaSilently = true;
        SelectedSchema = Schemas.FirstOrDefault(s => string.Equals(s, def, StringComparison.OrdinalIgnoreCase)) ?? Schemas.FirstOrDefault();
        _selectedSchemaSilently = false;
        _objectCache.Clear(); _columnCache.Clear();
        BuildExplorerRoots();
    }

    private bool _selectedSchemaSilently;

    private async Task SetSessionSchemaAsync(string schema)
    {
        if (_selectedSchemaSilently || _conn == null || _provider == null) return;
        var sql = _provider.Engine switch
        {
            DbEngine.PostgreSql => $"SET search_path TO {_provider.Quote(schema)}, public",
            DbEngine.Oracle => $"ALTER SESSION SET CURRENT_SCHEMA = {_provider.Quote(schema)}",
            DbEngine.MySql => $"USE {_provider.Quote(schema)}",
            _ => null,
        };
        if (sql == null) return;
        try
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql; cmd.Transaction = _tx;
            await cmd.ExecuteNonQueryAsync();
            Log($"session schema -> {schema}");
        }
        catch (Exception ex) { Log($"couldn't switch schema: {ex.Message}"); }
    }

    // ------------------------------------------------------------ explorer

    private List<string> _allSchemas => Schemas.ToList();

    private void BuildExplorerRoots()
    {
        Explorer.Clear();
        var filter = (ExplorerFilter ?? "").Trim();
        foreach (var s in _allSchemas)
        {
            if (filter.Length > 0 && !s.Contains(filter, StringComparison.OrdinalIgnoreCase) && !(_objectCache.TryGetValue(s, out var objs) && objs.Any(o => o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))) continue;
            var node = new ObjectNodeVm { Name = s, Kind = "schema", Schema = s, Loader = LoadSchemaNodeAsync };
            node.AddPlaceholder();
            Explorer.Add(node);
            if (string.Equals(s, SelectedSchema, StringComparison.OrdinalIgnoreCase) || filter.Length > 0) node.IsExpanded = true;
        }
    }

    private async Task LoadSchemaNodeAsync(ObjectNodeVm node)
    {
        if (SelectedProfile == null || _provider == null) return;
        if (!_objectCache.TryGetValue(node.Schema, out var objs))
        {
            await using var meta = await _provider.OpenAsync(SelectedProfile);
            objs = await _provider.ListObjectsAsync(meta, node.Schema);
            _objectCache[node.Schema] = objs;
        }
        var filter = (ExplorerFilter ?? "").Trim();
        node.Children.Clear();
        foreach (var g in objs.Where(o => filter.Length == 0 || o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || node.Schema.Contains(filter, StringComparison.OrdinalIgnoreCase)).GroupBy(o => o.Type))
        {
            var group = new ObjectNodeVm { Name = $"{Plural(g.Key)} ({g.Count()})", Kind = "group", Schema = node.Schema };
            foreach (var o in g)
            {
                var kind = o.Type.ToString().ToLowerInvariant();
                var child = new ObjectNodeVm { Name = o.Name, Kind = kind, Schema = node.Schema, Object = o, Loader = o.Type is DbObjectType.Table or DbObjectType.View ? LoadColumnsAsync : null };
                if (child.Loader != null) child.AddPlaceholder();
                group.Children.Add(child);
            }
            node.Children.Add(group);
            if (g.Key == DbObjectType.Table || filter.Length > 0) group.IsExpanded = true;
        }
        if (node.Children.Count == 0) node.Children.Add(new ObjectNodeVm { Name = "(empty)", Kind = "info" });
    }

    private static string Plural(DbObjectType t) => t switch
    {
        DbObjectType.Table => "Tables", DbObjectType.View => "Views", DbObjectType.Function => "Functions", DbObjectType.Procedure => "Procedures",
        DbObjectType.Package => "Packages", DbObjectType.Sequence => "Sequences", DbObjectType.Trigger => "Triggers", _ => "Types",
    };

    private async Task LoadColumnsAsync(ObjectNodeVm node)
    {
        if (SelectedProfile == null || _provider == null) return;
        await using var meta = await _provider.OpenAsync(SelectedProfile);
        var t = await _provider.GetTableAsync(meta, node.Schema, node.Name);
        node.Children.Clear();
        if (t == null) { node.Children.Add(new ObjectNodeVm { Name = "(no columns visible)", Kind = "info" }); return; }
        _columnCache[node.Name] = t.Columns;
        _columnCache[$"{node.Schema}.{node.Name}"] = t.Columns;
        foreach (var c in t.Columns)
            node.Children.Add(new ObjectNodeVm
            {
                Name = c.Name, Kind = TypeFacts.IsKey(t, c) ? "key" : "column", Schema = node.Schema,
                Detail = $"{c.NativeType}{(c.Nullable ? "" : " not null")}{(c.IsIdentity ? " identity" : "")}",
            });
    }

    [RelayCommand]
    private async Task RefreshExplorerAsync()
    {
        _objectCache.Clear(); _columnCache.Clear();
        if (IsConnected) await LoadSchemasAsync(); else await ConnectAsync();
    }

    [RelayCommand]
    private void InsertName(ObjectNodeVm? node)
    {
        if (node == null || SelectedTab == null || node.Kind is "group" or "info") return;
        var p = _provider ?? (SelectedProfile != null ? DbProviders.For(SelectedProfile) : null);
        var text = node.Kind is "column" or "key" ? node.Name : node.Kind == "schema" ? node.Name : p?.Qualify(node.Schema, node.Name) ?? node.Name;
        InsertRequested?.Invoke(this, text);
    }

    public event EventHandler<string>? InsertRequested;

    [RelayCommand]
    private async Task SelectTopAsync(ObjectNodeVm? node)
    {
        if (node?.Object == null || _provider == null) return;
        var sql = _provider.SelectTopSql(node.Schema, node.Name, 100);
        var tab = NewTabWith(node.Name, sql + (_provider.Dialect == ScriptDialect.Oracle ? "" : ";"));
        tab.IsDirty = false;
        await RunAsync(tab.Code, 0);
    }

    [RelayCommand]
    private async Task ScriptObjectAsync(ObjectNodeVm? node)
    {
        if (node?.Object == null || _provider == null || SelectedProfile == null) return;
        try
        {
            await using var meta = await _provider.OpenAsync(SelectedProfile);
            var src = await _provider.GetObjectSourceAsync(meta, node.Object);
            NewTabWith(node.Name + " (source)", src ?? "-- no source available (permissions?)").IsDirty = false;
        }
        catch (Exception ex) { Log("script failed: " + ex.Message); }
    }

    [RelayCommand]
    private async Task ConvertObjectAsync(ObjectNodeVm? node)
    {
        if (node?.Object == null || _provider == null || SelectedProfile == null) return;
        await using var meta = await _provider.OpenAsync(SelectedProfile);
        var src = await _provider.GetObjectSourceAsync(meta, node.Object) ?? "";
        ConverterNavigator.Open(src, SelectedProfile.Engine, node.Schema);
    }

    [RelayCommand]
    private void TransferObject(ObjectNodeVm? node)
    {
        if (node == null || SelectedProfile == null) return;
        TransferNavigator.Open(SelectedProfile.Id, node.Schema, node.Object?.Type == DbObjectType.Table ? new[] { node.Name } : Array.Empty<string>());
    }

    [RelayCommand]
    private void CopyName(ObjectNodeVm? node) { if (node != null) Clipboard.SetText(node.Kind is "column" or "key" or "schema" ? node.Name : $"{node.Schema}.{node.Name}"); }

    [RelayCommand]
    private async Task ExportObjectAsync(ObjectNodeVm? node)
    {
        if (node?.Object == null || _provider == null || SelectedProfile == null) return;
        var dlg = new SaveFileDialog { FileName = node.Name + ".csv", Filter = "CSV (*.csv)|*.csv|Tab-separated (*.tsv)|*.tsv|JSON (*.json)|*.json|SQL INSERT script (*.sql)|*.sql" };
        if (dlg.ShowDialog() != true) return;
        var fmt = (ExportFormat)(dlg.FilterIndex - 1);
        await ExportSqlToFileAsync($"SELECT * FROM {_provider.Qualify(node.Schema, node.Name)}", dlg.FileName, fmt, node.Name);
    }

    [RelayCommand]
    private async Task ImportCsvAsync(ObjectNodeVm? node)
    {
        if (node == null || SelectedProfile == null) return;
        var dlg = new OpenFileDialog { Filter = "CSV / TSV (*.csv;*.tsv;*.txt)|*.csv;*.tsv;*.txt|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        var table = node.Object?.Type == DbObjectType.Table ? node.Name : Path.GetFileNameWithoutExtension(dlg.FileName);
        var p = DbProviders.For(SelectedProfile);
        table = node.Object == null ? p.NormalizeName(table) : table;
        if (!ConfirmDialogProxy($"Import '{Path.GetFileName(dlg.FileName)}' into {node.Schema}.{table} on {SelectedProfile}?\n\nThe table is created (types inferred from the data) if it doesn't exist; rows are appended otherwise.")) return;
        var tab = SelectedTab ?? NewTabWith("Import", "");
        tab.ShowMessages = true;
        IsBusy = true;
        try
        {
            var n = await CsvTools.ImportCsvAsync(SelectedProfile, node.Schema, table, dlg.FileName, hasHeader: true, separator: null, truncateFirst: false,
                log: s => Log(s));
            JobHistory.Add("Import", $"CSV -> {node.Schema}.{table}", $"{n:N0} row(s) from {Path.GetFileName(dlg.FileName)}", true);
            _objectCache.Remove(node.Schema);
        }
        catch (Exception ex) { Log("import failed: " + ex.Message); }
        finally { IsBusy = false; }
    }

    private static bool ConfirmDialogProxy(string msg) => Views.ConfirmDialog.Confirm(Application.Current?.MainWindow, "Confirm", msg, confirmText: "Continue");

    // --------------------------------------------------------------- run

    public async Task RunAsync(string sql, int lineOffset)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        if (!IsConnected) await ConnectAsync();
        if (_conn == null || _provider == null || SelectedTab == null) return;
        var tab = SelectedTab;
        var settings = new SettingsStore().Load();

        if (!AutoCommit && _tx == null) { _tx = await _conn.BeginTransactionAsync(); InTransaction = true; Log("BEGIN (manual transaction mode)"); }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        tab.Results.Clear();
        tab.Messages.Clear();
        var sw = Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => ElapsedText = $"{sw.Elapsed.TotalSeconds:N1} s";
        timer.Start();
        tab.StatusText = "Running...";
        var exec = new QueryExecutor { MaxRows = settings.EditorMaxRows, StopOnError = StopOnError };
        exec.Message += (_, m) => Application.Current.Dispatcher.BeginInvoke(() => tab.Messages.Add("  " + m));
        exec.StatementCompleted += (_, r) => Application.Current.Dispatcher.BeginInvoke(() =>
            tab.Messages.Add($"#{r.Index + 1} (line {r.Line + lineOffset}) {(r.Ok ? "✓" : "✗")} {r.FirstLine}  —  {(r.Ok ? r.Summary : r.Error)}"));
        try
        {
            var results = await exec.RunScriptAsync(_conn, _provider, sql, _tx, _cts.Token);
            int n = 1;
            foreach (var r in results)
                foreach (var t in r.Tables)
                    tab.Results.Add(new ResultSetVm { Header = $"Result {n++}", Table = t, Sql = r.Sql, Truncated = r.Truncated });
            tab.SelectedResult = tab.Results.FirstOrDefault();
            var errors = results.Count(r => !r.Ok);
            var rows = results.Sum(r => r.Tables.Sum(t => t.Rows.Count));
            var affected = results.Where(r => r.RowsAffected > 0).Sum(r => r.RowsAffected);
            tab.StatusText = $"{results.Count} statement(s) · {rows:N0} row(s) returned · {affected:N0} affected · {sw.Elapsed.TotalMilliseconds:N0} ms" + (errors > 0 ? $" · {errors} error(s)" : "");
            tab.ShowMessages = errors > 0 || tab.Results.Count == 0;
            if (errors > 0)
            {
                var first = results.First(r => !r.Ok);
                ErrorAtLine?.Invoke(this, first.Line + lineOffset);
                if (_tx != null && _provider.Engine == DbEngine.PostgreSql) Log("PostgreSQL aborted the open transaction after the error — Rollback to continue.");
            }
            AddHistory(sql);
        }
        catch (OperationCanceledException) { tab.StatusText = "Cancelled."; tab.Messages.Add("Cancelled by user."); }
        catch (Exception ex)
        {
            tab.StatusText = "ERROR: " + ex.Message;
            tab.Messages.Add("ERROR: " + ex.Message);
            tab.ShowMessages = true;
            if (_conn.State != ConnectionState.Open) { IsConnected = false; ConnectionText = "Connection lost — reconnect."; }
        }
        finally
        {
            timer.Stop();
            ElapsedText = $"{sw.Elapsed.TotalSeconds:N2} s";
            IsBusy = false;
            _cts.Dispose(); _cts = null;
        }
    }

    public string BuildExplain(string stmt)
    {
        var s = stmt.Trim().TrimEnd(';');
        return (_provider?.Engine ?? SelectedProfile?.Engine) switch
        {
            DbEngine.Oracle => $"EXPLAIN PLAN FOR {s};\nSELECT plan_table_output FROM TABLE(DBMS_XPLAN.DISPLAY())",
            DbEngine.SqlServer => $"SET SHOWPLAN_TEXT ON\nGO\n{s}\nGO\nSET SHOWPLAN_TEXT OFF",
            DbEngine.Sqlite => $"EXPLAIN QUERY PLAN {s}",
            _ => $"EXPLAIN {s}",
        };
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (_tx == null) return;
        try { await _tx.CommitAsync(); Log("COMMIT"); }
        catch (Exception ex) { Log("commit failed: " + ex.Message); }
        await _tx.DisposeAsync(); _tx = null; InTransaction = false;
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (_tx == null) return;
        try { await _tx.RollbackAsync(); Log("ROLLBACK"); }
        catch (Exception ex) { Log("rollback failed: " + ex.Message); }
        await _tx.DisposeAsync(); _tx = null; InTransaction = false;
    }

    [RelayCommand]
    private async Task RunFileAsync(string? path = null)
    {
        if (path == null)
        {
            var dlg = new OpenFileDialog { Filter = "SQL scripts (*.sql;*.txt)|*.sql;*.txt|All files (*.*)|*.*", Title = "Run a script file (not opened in the editor)" };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        if (SelectedProfile == null) return;
        if (!ConfirmDialogProxy($"Run '{Path.GetFileName(path)}' ({new FileInfo(path).Length / 1024:N0} KB) against {SelectedProfile}?\n\nEvery statement is executed; errors are logged and the run continues.")) return;
        var tab = NewTabWith("▶ " + Path.GetFileName(path), $"-- running {path}\n");
        tab.IsDirty = false; tab.ShowMessages = true;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        try
        {
            var runner = new EngineBackupRunner();
            runner.LogLine += (_, l) => Application.Current.Dispatcher.BeginInvoke(() => tab.Messages.Add(l));
            var r = await runner.RunSqlFileAsync(SelectedProfile, path, _cts.Token);
            tab.StatusText = r.Message + $" ({sw.Elapsed:mm\\:ss})";
            JobHistory.Add("Script", Path.GetFileName(path), r.Message + $" on {SelectedProfile}", r.Ok, sw.Elapsed);
        }
        catch (OperationCanceledException) { tab.StatusText = "Cancelled."; }
        catch (Exception ex) { tab.StatusText = "ERROR: " + ex.Message; tab.Messages.Add(ex.Message); }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    // ------------------------------------------------------------- export

    [RelayCommand]
    private void ExportGrid()
    {
        var rs = SelectedTab?.SelectedResult;
        if (rs == null) return;
        var dlg = new SaveFileDialog { FileName = "result.csv", Filter = "CSV (*.csv)|*.csv|Tab-separated (*.tsv)|*.tsv" };
        if (dlg.ShowDialog() != true) return;
        CsvTools.ExportTable(rs.Table, dlg.FileName, dlg.FilterIndex == 2 ? ExportFormat.Tsv : ExportFormat.Csv);
        Log($"exported {rs.Table.Rows.Count:N0} row(s) to {dlg.FileName}");
    }

    [RelayCommand]
    private async Task ExportFullAsync()
    {
        var rs = SelectedTab?.SelectedResult;
        if (rs == null || SelectedProfile == null) return;
        var dlg = new SaveFileDialog { FileName = "export.csv", Filter = "CSV (*.csv)|*.csv|Tab-separated (*.tsv)|*.tsv|JSON (*.json)|*.json|SQL INSERT script (*.sql)|*.sql" };
        if (dlg.ShowDialog() != true) return;
        await ExportSqlToFileAsync(rs.Sql, dlg.FileName, (ExportFormat)(dlg.FilterIndex - 1), "exported");
    }

    private async Task ExportSqlToFileAsync(string sql, string path, ExportFormat fmt, string tableName)
    {
        if (SelectedProfile == null) return;
        var p = DbProviders.For(SelectedProfile);
        IsBusy = true;
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await p.OpenAsync(SelectedProfile);
            var progress = new Progress<long>(n => ElapsedText = $"{n:N0} rows…");
            var n = await CsvTools.ExportQueryAsync(conn, p, sql.Trim().TrimEnd(';'), path, fmt, tableName, progress);
            Log($"exported {n:N0} row(s) to {path} in {sw.Elapsed.TotalSeconds:N1} s");
            JobHistory.Add("Export", Path.GetFileName(path), $"{n:N0} row(s) from {SelectedProfile}", true, sw.Elapsed);
        }
        catch (Exception ex) { Log("export failed: " + ex.Message); }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------ history

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            foreach (var h in JsonSerializer.Deserialize<List<QueryHistoryEntry>>(File.ReadAllText(HistoryPath)) ?? new()) History.Add(h);
        }
        catch { }
    }

    private void AddHistory(string sql)
    {
        if (History.Count > 0 && History[0].Sql == sql) return;
        History.Insert(0, new QueryHistoryEntry(DateTime.Now, SelectedProfile?.Name ?? "", sql.Length > 20000 ? sql[..20000] : sql));
        while (History.Count > 300) History.RemoveAt(History.Count - 1);
        try { Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!); File.WriteAllText(HistoryPath, JsonSerializer.Serialize(History.ToList())); } catch { }
    }

    [RelayCommand]
    private void OpenHistory(QueryHistoryEntry? h) { if (h != null) NewTabWith("History", h.Sql).IsDirty = false; }

    private void Log(string s)
    {
        var tab = SelectedTab;
        if (tab == null) return;
        Application.Current?.Dispatcher.BeginInvoke(() => { tab.Messages.Add(s); });
    }

    // ------------------------------------------------------- completions

    public IEnumerable<(string Text, string Kind)> GetCompletions(string? qualifier)
    {
        if (qualifier != null)
        {
            if (_columnCache.TryGetValue(qualifier, out var cols)) return cols.Select(c => (c.Name, "column"));
            if (_objectCache.TryGetValue(qualifier, out var objs)) return objs.Select(o => (o.Name, o.Type.ToString().ToLowerInvariant()));
            // alias or not-yet-loaded table: kick off a column load for next time
            _ = PreloadColumnsAsync(qualifier);
            return Enumerable.Empty<(string, string)>();
        }
        var list = new List<(string, string)>();
        list.AddRange(Schemas.Select(s => (s, "schema")));
        if (SelectedSchema != null && _objectCache.TryGetValue(SelectedSchema, out var cur)) list.AddRange(cur.Select(o => (o.Name, o.Type.ToString().ToLowerInvariant())));
        list.AddRange(SqlHighlighting.Keywords.Select(k => (k, "keyword")));
        list.AddRange(SqlHighlighting.Types.Select(k => (k, "type")));
        return list.DistinctBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase);
    }

    private async Task PreloadColumnsAsync(string table)
    {
        if (SelectedProfile == null || _provider == null || SelectedSchema == null) return;
        try
        {
            if (!_objectCache.ContainsKey(SelectedSchema))
            {
                await using var m0 = await _provider.OpenAsync(SelectedProfile);
                _objectCache[SelectedSchema] = await _provider.ListObjectsAsync(m0, SelectedSchema);
            }
            var obj = _objectCache[SelectedSchema].FirstOrDefault(o => string.Equals(o.Name, table, StringComparison.OrdinalIgnoreCase));
            if (obj == null) return;
            await using var meta = await _provider.OpenAsync(SelectedProfile);
            var t = await _provider.GetTableAsync(meta, SelectedSchema, obj.Name);
            if (t != null) _columnCache[table] = t.Columns;
        }
        catch { }
    }
}

// Tiny cross-tab mailboxes for the converter and transfer pages.
public static class ConverterNavigator
{
    public static event EventHandler<(string Source, DbEngine From, string Schema)>? Requested;
    public static void Open(string source, DbEngine from, string schema) { Requested?.Invoke(null, (source, from, schema)); Navigator.Go("Converter"); }
}

public static class TransferNavigator
{
    public static event EventHandler<(Guid ProfileId, string Schema, string[] Tables)>? Requested;
    public static void Open(Guid profileId, string schema, string[] tables) { Requested?.Invoke(null, (profileId, schema, tables)); Navigator.Go("Transfer"); }
}
