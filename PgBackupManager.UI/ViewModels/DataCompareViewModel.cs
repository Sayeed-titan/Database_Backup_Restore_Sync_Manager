using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;

namespace PgBackupManager.UI.ViewModels;

public sealed class DataDiffRowVm
{
    public DataDiff Diff { get; init; } = null!;
    public string ChangedText { get; init; } = "";
    public string Status => Diff.Status switch
    {
        DataDiffStatus.OnlyInSource => "only in source",
        DataDiffStatus.OnlyInTarget => "only in target",
        _ => "different",
    };
    public string Key => Diff.KeyText;
    public bool IsSource => Diff.Status == DataDiffStatus.OnlyInSource;
    public bool IsTarget => Diff.Status == DataDiffStatus.OnlyInTarget;
    public bool IsDiff => Diff.Status == DataDiffStatus.Different;
}

public sealed record DataCellDiff(string Column, string Source, string Target, bool Changed);

public partial class DataCompareViewModel : ObservableObject
{
    private const int MaxShown = 20_000;
    private TableDiffResult? _result;
    private CancellationTokenSource? _cts;
    private readonly TableDiffRunner _runner = new();

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _sourceProfile;
    [ObservableProperty] private ConnectionProfile? _targetProfile;
    public ObservableCollection<string> SourceSchemas { get; } = new();
    public ObservableCollection<string> TargetSchemas { get; } = new();
    public ObservableCollection<string> SourceTables { get; } = new();
    public ObservableCollection<string> TargetTables { get; } = new();
    public ObservableCollection<string> SourceDatabases { get; } = new();
    public ObservableCollection<string> TargetDatabases { get; } = new();
    [ObservableProperty] private string? _sourceDatabase;
    [ObservableProperty] private string? _targetDatabase;
    public bool HasSourceDatabases => SourceDatabases.Count > 1;
    public bool HasTargetDatabases => TargetDatabases.Count > 1;
    private bool _loadingDbs;
    private (string? Source, string? Target) _pendingDb;
    private ConnectionProfile? SrcEff => SourceProfile?.WithDatabase(SourceDatabase);
    private ConnectionProfile? TgtEff => TargetProfile?.WithDatabase(TargetDatabase);
    [ObservableProperty] private string? _sourceSchema;
    [ObservableProperty] private string? _targetSchema;
    [ObservableProperty] private string? _sourceTable;
    [ObservableProperty] private string? _targetTable;

    [ObservableProperty] private string _keyColumns = "";
    [ObservableProperty] private string _ignoreColumns = "";
    [ObservableProperty] private string _sourceFilter = "";
    [ObservableProperty] private string _targetFilter = "";
    [ObservableProperty] private bool _ignoreTrailingSpaces = true;
    [ObservableProperty] private bool _showOptions;

    public ObservableCollection<DataDiffRowVm> Rows { get; } = new();
    public ObservableCollection<DataCellDiff> Cells { get; } = new();
    [ObservableProperty] private DataDiffRowVm? _selected;
    [ObservableProperty] private bool _showSource = true;
    [ObservableProperty] private bool _showTarget = true;
    [ObservableProperty] private bool _showDifferent = true;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private long _sameCount;
    [ObservableProperty] private int _onlySourceCount;
    [ObservableProperty] private int _onlyTargetCount;
    [ObservableProperty] private int _differentCount;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _applyInsert = true;
    [ObservableProperty] private bool _applyUpdate = true;
    [ObservableProperty] private bool _applyDelete;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _statusText = "Pick a table on each side (any engines) and Compare. Rows are matched on the primary key; nothing is written until you Apply.";

    partial void OnShowSourceChanged(bool value) => Filter();
    partial void OnShowTargetChanged(bool value) => Filter();
    partial void OnShowDifferentChanged(bool value) => Filter();
    partial void OnSearchChanged(string value) => Filter();

    public DataCompareViewModel()
    {
        Reload();
        ProfileStore.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(Reload);
    }

    private void Reload()
    {
        var s = SourceProfile?.Id; var t = TargetProfile?.Id;
        Profiles.Clear();
        foreach (var p in new ProfileStore().LoadAll().OrderBy(p => p.Engine).ThenBy(p => p.Name)) Profiles.Add(p);
        SourceProfile = Profiles.FirstOrDefault(p => p.Id == s) ?? Profiles.FirstOrDefault();
        TargetProfile = Profiles.FirstOrDefault(p => p.Id == t);
    }

    partial void OnSourceProfileChanged(ConnectionProfile? value) => _ = LoadSideAsync(value, SourceDatabases, v => SourceDatabase = v, nameof(HasSourceDatabases), _pendingDb.Source, SourceSchemas, v => SourceSchema = v);
    partial void OnTargetProfileChanged(ConnectionProfile? value) => _ = LoadSideAsync(value, TargetDatabases, v => TargetDatabase = v, nameof(HasTargetDatabases), _pendingDb.Target, TargetSchemas, v => TargetSchema = v);
    partial void OnSourceDatabaseChanged(string? value) { if (!_loadingDbs) _ = LoadSchemasAsync(SrcEff, SourceSchemas, v => SourceSchema = v); }
    partial void OnTargetDatabaseChanged(string? value) { if (!_loadingDbs) _ = LoadSchemasAsync(TgtEff, TargetSchemas, v => TargetSchema = v); }
    partial void OnSourceSchemaChanged(string? value) => _ = LoadTablesAsync(SrcEff, value, SourceTables, v => SourceTable = v, null);
    partial void OnTargetSchemaChanged(string? value) => _ = LoadTablesAsync(TgtEff, value, TargetTables, v => TargetTable = v, SourceTable);

    private async Task LoadSideAsync(ConnectionProfile? p, ObservableCollection<string> dbs, Action<string?> selectDb, string hasProp, string? prefer,
        ObservableCollection<string> schemas, Action<string?> selectSchema)
    {
        _loadingDbs = true;
        string? chosen;
        try { chosen = await DbPicker.LoadAsync(p, dbs, prefer); selectDb(chosen); }
        finally { _loadingDbs = false; OnPropertyChanged(hasProp); }
        await LoadSchemasAsync(p?.WithDatabase(chosen), schemas, selectSchema);
    }

    // Picking a source table pre-selects the same-named target table.
    partial void OnSourceTableChanged(string? value)
    {
        var match = TargetTables.FirstOrDefault(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase));
        if (match != null) TargetTable = match;
    }

    private async Task LoadSchemasAsync(ConnectionProfile? p, ObservableCollection<string> into, Action<string?> select)
    {
        into.Clear();
        if (p == null) return;
        try
        {
            var prov = DbProviders.For(p);
            await using var c = await prov.OpenAsync(p);
            foreach (var s in await prov.ListSchemasAsync(c)) into.Add(s);
            select(into.FirstOrDefault(s => string.Equals(s, p.ResolveDefaultSchema(), StringComparison.OrdinalIgnoreCase)) ?? into.FirstOrDefault());
        }
        catch (Exception ex) { StatusText = $"Couldn't list schemas of {p}: {ex.Message}"; }
    }

    private async Task LoadTablesAsync(ConnectionProfile? p, string? schema, ObservableCollection<string> into, Action<string?> select, string? prefer)
    {
        into.Clear();
        if (p == null || string.IsNullOrEmpty(schema)) return;
        try
        {
            var prov = DbProviders.For(p);
            await using var c = await prov.OpenAsync(p);
            foreach (var o in (await prov.ListObjectsAsync(c, schema)).Where(o => o.Type == DbObjectType.Table).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                into.Add(o.Name);
            select(into.FirstOrDefault(t => string.Equals(t, prefer, StringComparison.OrdinalIgnoreCase)) ?? into.FirstOrDefault());
        }
        catch (Exception ex) { StatusText = $"Couldn't list tables of {p}: {ex.Message}"; }
    }

    [RelayCommand]
    private void Swap()
    {
        var (sp, ss, st, tp, ts, tt) = (SourceProfile, SourceSchema, SourceTable, TargetProfile, TargetSchema, TargetTable);
        _pendingDb = (TargetDatabase, SourceDatabase);
        SourceProfile = tp; TargetProfile = sp;
        (SourceFilter, TargetFilter) = (TargetFilter, SourceFilter);
        Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(400);
            SourceSchema = ts; TargetSchema = ss;
            await Task.Delay(400);
            SourceTable = tt; TargetTable = st;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static List<string> SplitNames(string s) =>
        s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (SourceProfile == null || TargetProfile == null || string.IsNullOrEmpty(SourceTable) || string.IsNullOrEmpty(TargetTable))
        { StatusText = "Pick a table on both sides."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        EventHandler<string> onProgress = (_, m) => Application.Current?.Dispatcher.BeginInvoke(() => StatusText = m);
        _runner.Progress += onProgress;
        try
        {
            var o = new TableDiffOptions
            {
                Source = SrcEff!, Target = TgtEff!,
                SourceSchema = SourceSchema ?? "", SourceTable = SourceTable!, TargetSchema = TargetSchema ?? "", TargetTable = TargetTable!,
                KeyColumns = SplitNames(KeyColumns), IgnoreColumns = SplitNames(IgnoreColumns),
                SourceFilter = string.IsNullOrWhiteSpace(SourceFilter) ? null : SourceFilter.Trim(),
                TargetFilter = string.IsNullOrWhiteSpace(TargetFilter) ? null : TargetFilter.Trim(),
                IgnoreTrailingSpaces = IgnoreTrailingSpaces,
            };
            _result = await Task.Run(() => _runner.CompareAsync(o, _cts.Token));
            ShowResult();
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        catch (Exception ex) { StatusText = "ERROR: " + ex.Message; }
        finally { _runner.Progress -= onProgress; IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    private void ShowResult()
    {
        var r = _result!;
        SameCount = r.Same;
        OnlySourceCount = r.Count(DataDiffStatus.OnlyInSource);
        OnlyTargetCount = r.Count(DataDiffStatus.OnlyInTarget);
        DifferentCount = r.Count(DataDiffStatus.Different);
        var keys = string.Join(", ", r.KeyIndexes.Select(i => r.Columns[i].Source));
        Notes = $"Matched on ({keys}) · {r.Columns.Count} common column(s) · {r.SourceRows:N0} source / {r.TargetRows:N0} target rows" +
                (r.Notes.Count > 0 ? " · " + string.Join(" · ", r.Notes) : "");
        HasResult = true;
        Filter();
        StatusText = r.Diffs.Count == 0 ? "The tables match — no differences." : $"{r.Diffs.Count:N0} difference(s). Select rows (or none = all shown), tick what to apply, then Apply to Target.";
    }

    private void Filter()
    {
        Rows.Clear();
        Cells.Clear();
        if (_result == null) return;
        var term = Search.Trim();
        int shown = 0;
        foreach (var d in _result.Diffs)
        {
            if (d.Status == DataDiffStatus.OnlyInSource && !ShowSource || d.Status == DataDiffStatus.OnlyInTarget && !ShowTarget || d.Status == DataDiffStatus.Different && !ShowDifferent) continue;
            var changed = string.Join(", ", d.Changed.Select(i => _result.Columns[i].Source));
            if (term.Length > 0 && !(d.KeyText + " " + changed).Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            if (++shown > MaxShown) { StatusText = $"Showing the first {MaxShown:N0} rows — narrow with the filters (Apply still covers every matching row)."; break; }
            Rows.Add(new DataDiffRowVm { Diff = d, ChangedText = changed });
        }
    }

    partial void OnSelectedChanged(DataDiffRowVm? value)
    {
        Cells.Clear();
        if (value == null || _result == null) return;
        var d = value.Diff;
        for (int i = 0; i < _result.Columns.Count; i++)
        {
            string Side(string?[] vals, bool present) => !present ? "—" : vals[i] ?? "NULL";
            Cells.Add(new DataCellDiff(_result.Columns[i].Source,
                Side(d.SourceValues, d.Status != DataDiffStatus.OnlyInTarget),
                Side(d.TargetValues, d.Status != DataDiffStatus.OnlyInSource),
                d.Changed.Contains(i) || d.Status != DataDiffStatus.Different));
        }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    // selected = rows ticked in the grid (2+); otherwise every row matching the current filters.
    private List<DataDiff> Chosen(IList<DataDiffRowVm> selected)
    {
        if (selected.Count > 1) return selected.Select(r => r.Diff).ToList();
        var term = Search.Trim();
        return _result!.Diffs.Where(d =>
            (d.Status != DataDiffStatus.OnlyInSource || ShowSource) && (d.Status != DataDiffStatus.OnlyInTarget || ShowTarget) && (d.Status != DataDiffStatus.Different || ShowDifferent) &&
            (term.Length == 0 || (d.KeyText + " " + string.Join(", ", d.Changed.Select(i => _result.Columns[i].Source))).Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public void GenerateScript(IList<DataDiffRowVm> selected)
    {
        if (_result == null || TargetProfile == null) { StatusText = "Run a compare first."; return; }
        var diffs = Chosen(selected);
        var script = _runner.BuildScript(_result, diffs, ApplyInsert, ApplyUpdate, ApplyDelete);
        var o = _result.Options;
        Navigator.OpenSql($"data sync {o.TargetTable}", script, o.Target.Id, o.Target.Database);
        StatusText = $"Data sync script for {diffs.Count:N0} row(s) opened in the SQL Editor on {o.Target} — review, then Run (nothing has been written).";
    }

    public async Task ApplyAsync(IList<DataDiffRowVm> selected, Window owner)
    {
        if (_result == null || TargetProfile == null) return;
        var diffs = Chosen(selected);
        int ins = ApplyInsert ? diffs.Count(d => d.Status == DataDiffStatus.OnlyInSource) : 0;
        int up = ApplyUpdate ? diffs.Count(d => d.Status == DataDiffStatus.Different) : 0;
        int del = ApplyDelete ? diffs.Count(d => d.Status == DataDiffStatus.OnlyInTarget) : 0;
        if (ins + up + del == 0) { StatusText = "Nothing to apply with the current selection and options."; return; }

        var o = _result.Options;
        var msg = $"Write to TARGET {o.Target.DisplayWithEngine} ({o.Target.Host}) — {o.TargetSchema}.{o.TargetTable}:\n\n" +
                  $"  INSERT {ins:N0} row(s)\n  UPDATE {up:N0} row(s)\n  DELETE {del:N0} row(s)\n\nAll in one transaction (rolled back on any error). Continue?";
        if (MessageBox.Show(owner, msg, "Apply data changes", MessageBoxButton.YesNo, del > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        IsBusy = true;
        EventHandler<string> onProgress = (_, m) => Application.Current?.Dispatcher.BeginInvoke(() => StatusText = m);
        _runner.Progress += onProgress;
        try
        {
            var r = await Task.Run(() => _runner.ApplyAsync(_result, diffs, ApplyInsert, ApplyUpdate, ApplyDelete));
            JobHistory.Add("Data Compare", $"{o.SourceSchema}.{o.SourceTable} → {o.TargetSchema}.{o.TargetTable}",
                $"inserted {r.Inserted:N0}, updated {r.Updated:N0}, deleted {r.Deleted:N0} on {o.Target}", true);
            _runner.Progress -= onProgress;
            IsBusy = false;
            await CompareAsync();
            StatusText = $"Applied: {r.Inserted:N0} inserted, {r.Updated:N0} updated, {r.Deleted:N0} deleted — re-compared: {(_result?.Diffs.Count ?? 0):N0} difference(s) left.";
        }
        catch (Exception ex) { StatusText = "Nothing written (rolled back): " + ex.Message; }
        finally { _runner.Progress -= onProgress; IsBusy = false; }
    }
}
