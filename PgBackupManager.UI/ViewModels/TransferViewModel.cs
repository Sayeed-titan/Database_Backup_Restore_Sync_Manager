using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;
using PgBackupManager.UI.Views;

namespace PgBackupManager.UI.ViewModels;

public partial class TransferItem : ObservableObject
{
    public DbObjectInfo Obj { get; init; } = new("", "", DbObjectType.Table);
    public string Name => Obj.Name;
    public string TypeLabel => Obj.Type.ToString().ToUpperInvariant();
    [ObservableProperty] private bool _isChecked;
}

public partial class TableRunRow : ObservableObject
{
    public string Table { get; init; } = "";
    [ObservableProperty] private string _status = "pending";
    [ObservableProperty] private string _rows = "";
    [ObservableProperty] private string _time = "";
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private string _detail = "";
}

public sealed record Choice<T>(T Value, string Label) { public override string ToString() => Label; }

public partial class TransferViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private CancellationTokenSource? _cts;
    private (Guid ProfileId, string Schema, string[] Tables)? _pendingRequest;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _sourceProfile;
    [ObservableProperty] private ConnectionProfile? _targetProfile;
    public ObservableCollection<string> SourceSchemas { get; } = new();
    public ObservableCollection<string> TargetSchemas { get; } = new();
    [ObservableProperty] private string? _sourceSchema;

    // Database on the server (SQL Server / PostgreSQL): defaults to the profile's own, any other can be picked.
    public ObservableCollection<string> SourceDatabases { get; } = new();
    public ObservableCollection<string> TargetDatabases { get; } = new();
    [ObservableProperty] private string? _sourceDatabase;
    [ObservableProperty] private string? _targetDatabase;
    public bool HasSourceDatabases => SourceDatabases.Count > 1;
    public bool HasTargetDatabases => TargetDatabases.Count > 1;
    private bool _loadingDbs;
    // The profile as actually connected to: selected connection + chosen database.
    private ConnectionProfile? SrcEff => SourceProfile?.WithDatabase(SourceDatabase);
    private ConnectionProfile? TgtEff => TargetProfile?.WithDatabase(TargetDatabase);
    [ObservableProperty] private string _targetSchema = "";

    public ObservableCollection<TransferItem> Tables { get; } = new();
    public ObservableCollection<TransferItem> CodeObjects { get; } = new();
    public ObservableCollection<TransferItem> FilteredTables { get; } = new();
    public ObservableCollection<TransferItem> FilteredCode { get; } = new();
    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public IReadOnlyList<Choice<TableLoadMode>> Modes { get; } = new Choice<TableLoadMode>[]
    {
        new(TableLoadMode.CreateOrAppend, "Create missing tables, append rows"),
        new(TableLoadMode.TruncateAndLoad, "Create missing, empty existing, then load"),
        new(TableLoadMode.Upsert, "Upsert by primary key (insert or update)"),
        new(TableLoadMode.DropAndRecreate, "Drop & recreate every table, then load"),
        new(TableLoadMode.StructureOnly, "Structure only (create tables, no rows)"),
    };
    [ObservableProperty] private Choice<TableLoadMode>? _mode;

    public IReadOnlyList<Choice<NameCase>> NameCases { get; } = new Choice<NameCase>[]
    {
        new(Core.Providers.NameCase.TargetDefault, "Target default (PG lower, Oracle UPPER)"),
        new(Core.Providers.NameCase.Lower, "lower_case"),
        new(Core.Providers.NameCase.Upper, "UPPER_CASE"),
        new(Core.Providers.NameCase.Preserve, "Preserve exactly"),
    };
    [ObservableProperty] private Choice<NameCase>? _nameCase;

    [ObservableProperty] private bool _dryRun = true;
    [ObservableProperty] private bool _applyCode;
    [ObservableProperty] private bool _continueOnError = true;
    [ObservableProperty] private bool _copyIndexes = true;
    [ObservableProperty] private bool _copyForeignKeys = true;
    [ObservableProperty] private string _rowFilter = "";
    [ObservableProperty] private string _commitEvery = "0";

    public ObservableCollection<JobPreset> Presets { get; } = new();
    [ObservableProperty] private JobPreset? _selectedPreset;
    [ObservableProperty] private string _presetName = "";

    public ObservableCollection<TableRunRow> RunRows { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    [ObservableProperty] private string _statusText = "Pick a source and a target connection, then Load Objects.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private double _overallPercent;
    [ObservableProperty] private double _tablePercent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string? _lastScriptPath;

    public string RunButtonText => DryRun ? "Preview Plan" : "Run Transfer";
    partial void OnDryRunChanged(bool value) => OnPropertyChanged(nameof(RunButtonText));
    public string SelectionSummary => $"{Tables.Count(t => t.IsChecked)} of {Tables.Count} table(s) · {CodeObjects.Count(t => t.IsChecked)} of {CodeObjects.Count} code object(s)";
    public string EngineHint => SourceProfile == null || TargetProfile == null ? ""
        : SourceProfile.Engine == TargetProfile.Engine ? $"{Label(SourceProfile)} → {Label(TargetProfile)} · same engine: code objects are copied with the schema renamed"
        : $"{Label(SourceProfile)} → {Label(TargetProfile)} · tables are type-mapped; code objects go through the converter";
    private static string Label(ConnectionProfile p) => DbProviders.For(p).Name;

    public TransferViewModel()
    {
        Mode = Modes[0];
        NameCase = NameCases[0];
        ReloadProfiles();
        ReloadPresets();
        ProfileStore.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(ReloadProfiles);
        PresetStore.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(ReloadPresets);
        TransferNavigator.Requested += (_, r) => Application.Current?.Dispatcher.Invoke(() => _ = ApplyRequestAsync(r));
    }

    public void ReloadProfiles()
    {
        var s = SourceProfile?.Id; var t = TargetProfile?.Id;
        Profiles.Clear();
        foreach (var p in _profileStore.LoadAll().OrderBy(p => p.Engine).ThenBy(p => p.Name)) Profiles.Add(p);
        SourceProfile = Profiles.FirstOrDefault(p => p.Id == s) ?? Profiles.FirstOrDefault();
        // Never guess the target — it is the side that gets written to.
        TargetProfile = Profiles.FirstOrDefault(p => p.Id == t);
    }

    private void ReloadPresets()
    {
        var id = SelectedPreset?.Id;
        Presets.Clear();
        foreach (var p in PresetStore.LoadAll().Where(p => p.Kind == PresetKind.Transfer)) Presets.Add(p);
        _suppressPreset = true;
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == id);
        _suppressPreset = false;
    }

    private async Task ApplyRequestAsync((Guid ProfileId, string Schema, string[] Tables) r)
    {
        SourceProfile = Profiles.FirstOrDefault(p => p.Id == r.ProfileId) ?? SourceProfile;
        _pendingRequest = r;
        await LoadObjectsAsync();
    }

    partial void OnSourceProfileChanged(ConnectionProfile? value)
    {
        Tables.Clear(); CodeObjects.Clear(); SourceSchemas.Clear(); ApplyFilter();
        OnPropertyChanged(nameof(EngineHint));
        _ = LoadDatabasesAsync(value, SourceDatabases, v => SourceDatabase = v, () => OnPropertyChanged(nameof(HasSourceDatabases)), _pendingDb.Source);
    }

    partial void OnTargetProfileChanged(ConnectionProfile? value)
    {
        OnPropertyChanged(nameof(EngineHint));
        _ = LoadDatabasesAsync(value, TargetDatabases, v => TargetDatabase = v, () => OnPropertyChanged(nameof(HasTargetDatabases)), _pendingDb.Target)
            .ContinueWith(_ => Ui(() => _ = LoadTargetSchemasAsync()));
    }

    // Database picked for a preset / request, applied once the profile's list has loaded.
    private (string? Source, string? Target) _pendingDb;

    partial void OnSourceDatabaseChanged(string? value)
    {
        if (_loadingDbs) return;
        Tables.Clear(); CodeObjects.Clear(); SourceSchemas.Clear(); SourceSchema = null; ApplyFilter();
        _ = LoadObjectsAsync();
    }

    partial void OnTargetDatabaseChanged(string? value)
    {
        if (_loadingDbs) return;
        _ = LoadTargetSchemasAsync();
    }

    private async Task LoadDatabasesAsync(ConnectionProfile? p, ObservableCollection<string> into, Action<string?> select, Action changed, string? prefer)
    {
        _loadingDbs = true;
        into.Clear();
        string? chosen = null;
        try
        {
            if (p != null)
            {
                chosen = prefer ?? p.Database;
                if (p.Engine is DbEngine.SqlServer or DbEngine.PostgreSql)
                {
                    try
                    {
                        var prov = DbProviders.For(p);
                        await using var c = await prov.OpenAsync(p);
                        foreach (var d in await prov.ListDatabasesAsync(c)) into.Add(d);
                    }
                    catch { /* offline / no permission — fall back to the profile's own database */ }
                    if (!string.IsNullOrEmpty(p.Database) && !into.Contains(p.Database, StringComparer.OrdinalIgnoreCase)) into.Insert(0, p.Database);
                    chosen = into.FirstOrDefault(d => string.Equals(d, chosen, StringComparison.OrdinalIgnoreCase)) ?? into.FirstOrDefault();
                }
            }
            select(chosen);
        }
        finally { _loadingDbs = false; changed(); }
    }

    partial void OnSourceSchemaChanged(string? value)
    {
        if (!_loadingSchemas) _ = LoadObjectListAsync();
        if (string.IsNullOrWhiteSpace(TargetSchema) && value != null && TargetProfile != null)
            TargetSchema = TargetProfile.Engine == DbEngine.Sqlite ? "main" : NameCasing.Apply(value, NameCase?.Value ?? Core.Providers.NameCase.TargetDefault, DbProviders.For(TargetProfile).NormalizeName);
    }

    private bool _loadingSchemas;

    [RelayCommand]
    private async Task LoadObjectsAsync()
    {
        if (SourceProfile == null) { StatusText = "Pick a source connection."; return; }
        try
        {
            IsBusy = true;
            StatusText = $"Connecting to {SourceProfile}" + (string.IsNullOrEmpty(SourceDatabase) ? "" : $" · {SourceDatabase}") + "...";
            var sp = DbProviders.For(SourceProfile);
            await using var c = await sp.OpenAsync(SrcEff!);
            var schemas = await sp.ListSchemasAsync(c);
            _loadingSchemas = true;
            var prev = _pendingRequest?.Schema ?? SourceSchema;
            SourceSchemas.Clear();
            foreach (var s in schemas) SourceSchemas.Add(s);
            SourceSchema = SourceSchemas.FirstOrDefault(s => string.Equals(s, prev, StringComparison.OrdinalIgnoreCase))
                           ?? SourceSchemas.FirstOrDefault(s => string.Equals(s, SourceProfile.ResolveDefaultSchema(), StringComparison.OrdinalIgnoreCase))
                           ?? SourceSchemas.FirstOrDefault();
            _loadingSchemas = false;
            await LoadObjectListAsync();
        }
        catch (Exception ex) { StatusText = "ERROR: " + ex.Message; }
        finally { IsBusy = false; _loadingSchemas = false; }
    }

    private async Task LoadObjectListAsync()
    {
        if (SourceProfile == null || string.IsNullOrEmpty(SourceSchema)) return;
        try
        {
            var sp = DbProviders.For(SourceProfile);
            await using var c = await sp.OpenAsync(SrcEff!);
            var objs = await sp.ListObjectsAsync(c, SourceSchema);
            var want = _pendingRequest?.Tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Tables.Clear(); CodeObjects.Clear();
            foreach (var o in objs)
            {
                var item = new TransferItem { Obj = o, IsChecked = o.Type == DbObjectType.Table && (want == null || want.Count == 0 || want.Contains(o.Name)) };
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectionSummary));
                if (o.Type == DbObjectType.Table) Tables.Add(item); else CodeObjects.Add(item);
            }
            _pendingRequest = null;
            ApplyFilter();
            StatusText = $"Loaded {Tables.Count} table(s) and {CodeObjects.Count} code object(s) from {SourceSchema}.";
        }
        catch (Exception ex) { StatusText = "ERROR loading objects: " + ex.Message; }
    }

    private async Task LoadTargetSchemasAsync()
    {
        TargetSchemas.Clear();
        if (TargetProfile == null) return;
        try
        {
            var tp = DbProviders.For(TargetProfile);
            await using var c = await tp.OpenAsync(TgtEff!);
            foreach (var s in await tp.ListSchemasAsync(c)) TargetSchemas.Add(s);
        }
        catch { /* offline target — the schema can still be typed */ }
    }

    private void ApplyFilter()
    {
        var term = (SearchText ?? "").Trim();
        FilteredTables.Clear(); FilteredCode.Clear();
        foreach (var t in Tables.Where(t => term.Length == 0 || t.Name.Contains(term, StringComparison.OrdinalIgnoreCase))) FilteredTables.Add(t);
        foreach (var t in CodeObjects.Where(t => term.Length == 0 || t.Name.Contains(term, StringComparison.OrdinalIgnoreCase))) FilteredCode.Add(t);
        OnPropertyChanged(nameof(SelectionSummary));
    }

    [RelayCommand] private void AllTables() { foreach (var t in Tables) t.IsChecked = true; }
    [RelayCommand] private void NoTables() { foreach (var t in Tables) t.IsChecked = false; }
    [RelayCommand] private void AllCode() { foreach (var t in CodeObjects) t.IsChecked = true; }
    [RelayCommand] private void NoCode() { foreach (var t in CodeObjects) t.IsChecked = false; }

    [RelayCommand]
    private void Swap()
    {
        _pendingDb = (TargetDatabase, SourceDatabase);
        (SourceProfile, TargetProfile) = (TargetProfile, SourceProfile);
        StatusText = "Swapped source and target — Load Objects to refresh the list.";
    }

    private TransferOptions? BuildOptions(bool dryRun, out string? error)
    {
        error = null;
        if (SourceProfile == null || TargetProfile == null) { error = "Pick both connections."; return null; }
        if (string.IsNullOrEmpty(SourceSchema)) { error = "Load objects and pick a source schema."; return null; }
        if (string.IsNullOrWhiteSpace(TargetSchema)) { error = "Type a target schema."; return null; }
        if (SourceProfile.Id == TargetProfile.Id && string.Equals(SrcEff!.Database, TgtEff!.Database, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SourceSchema, TargetSchema.Trim(), StringComparison.OrdinalIgnoreCase))
        { error = "Source and target are the same schema in the same database."; return null; }
        var tables = Tables.Where(t => t.IsChecked).Select(t => t.Name).ToList();
        var code = CodeObjects.Where(t => t.IsChecked).Select(t => t.Obj).ToList();
        if (tables.Count == 0 && code.Count == 0) { error = "Select at least one table or code object."; return null; }
        long.TryParse(CommitEvery, out var every);
        return new TransferOptions
        {
            Source = SrcEff!, Target = TgtEff!, SourceSchema = SourceSchema!, TargetSchema = TargetSchema.Trim(),
            Tables = tables, CodeObjects = code, Mode = Mode?.Value ?? TableLoadMode.CreateOrAppend, NameCase = NameCase?.Value ?? Core.Providers.NameCase.TargetDefault,
            DryRun = dryRun, ContinueOnError = ContinueOnError, ApplyCode = ApplyCode, RowFilter = string.IsNullOrWhiteSpace(RowFilter) ? null : RowFilter.Trim(),
            CommitEveryRows = Math.Max(0, every), CopyIndexes = CopyIndexes, CopyForeignKeys = CopyForeignKeys,
        };
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        var o = BuildOptions(DryRun, out var err);
        if (o == null) { StatusText = err!; return; }
        if (!DryRun)
        {
            var destructive = o.Mode is TableLoadMode.DropAndRecreate or TableLoadMode.TruncateAndLoad;
            if (!ConfirmDialog.Confirm(Application.Current?.MainWindow, "Confirm transfer",
                    $"Copy {o.Tables.Count} table(s){(o.CodeObjects.Count > 0 ? $" and {o.CodeObjects.Count} code object(s)" : "")}\n" +
                    $"FROM [{DbProviders.For(o.Source).Name}] {o.Source} · {o.SourceSchema}\n" +
                    $"INTO [{DbProviders.For(o.Target).Name}] {o.Target} · {o.TargetSchema}\n\n" +
                    $"Mode: {Mode?.Label}\n" +
                    (destructive ? "\n⚠ This mode DELETES existing target data for the selected tables.\n" : "") +
                    (o.CodeObjects.Count > 0 && ApplyCode ? "\nConverted code will be executed on the target.\n" : "") +
                    "\nThe source is only read. Proceed?",
                    confirmText: "Yes, transfer", danger: destructive)) { StatusText = "Cancelled."; return; }
        }

        LogLines.Clear(); RunRows.Clear();
        foreach (var t in o.Tables) RunRows.Add(new TableRunRow { Table = t });
        _cts = new CancellationTokenSource();
        IsBusy = true; CanCancel = true; OverallPercent = 0; TablePercent = 0;
        StatusText = DryRun ? "Building plan..." : "Transferring...";
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => ElapsedText = sw.Elapsed.ToString(sw.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
        timer.Start();
        try
        {
            var runner = new TransferRunner();
            runner.LogLine += (_, l) => Ui(() =>
            {
                LogLines.Add(l);
                // "  [table] ..." lines drive the per-table status column
                var m = System.Text.RegularExpressions.Regex.Match(l, @"^\s+\[(?<t>[^\]]+)\]\s(?<msg>.+)$");
                if (m.Success && RunRows.FirstOrDefault(r => r.Table == m.Groups["t"].Value) is { } row)
                {
                    row.Detail = m.Groups["msg"].Value;
                    if (m.Groups["msg"].Value.StartsWith("ERROR", StringComparison.Ordinal)) { row.Failed = true; row.Status = "failed"; }
                    else if (row.Status == "pending") row.Status = DryRun ? "planned" : "running";
                }
            });
            runner.Progress += (_, p) => Ui(() =>
            {
                TablePercent = p.EstimatedRows is > 0 ? Math.Min(100, p.Rows * 100.0 / p.EstimatedRows.Value) : 0;
                OverallPercent = (p.TableIndex + TablePercent / 100.0) * 100.0 / Math.Max(1, p.TableCount);
                ProgressText = $"{p.Table}: {p.Rows:N0}{(p.EstimatedRows.HasValue ? " / " + p.EstimatedRows.Value.ToString("N0") : "")} rows  ·  table {p.TableIndex + 1} of {p.TableCount}";
                if (RunRows.FirstOrDefault(r => r.Table == p.Table) is { } row) row.Rows = p.Rows.ToString("N0");
            });

            var result = await runner.RunAsync(o, _cts.Token);
            foreach (var tr in result.Tables)
                if (RunRows.FirstOrDefault(r => r.Table == tr.Table) is { } row)
                {
                    row.Status = !tr.Ok ? "failed" : DryRun ? "planned" : "done";
                    row.Failed = !tr.Ok;
                    row.Rows = DryRun ? "" : tr.Rows.ToString("N0");
                    row.Time = tr.Elapsed.TotalSeconds < 1 ? $"{tr.Elapsed.TotalMilliseconds:N0} ms" : $"{tr.Elapsed.TotalSeconds:N1} s";
                    if (tr.Error != null) row.Detail = tr.Error;
                }
            OverallPercent = 100;
            LastScriptPath = result.ScriptPath;
            StatusText = $"{result.Summary} (took {ElapsedText})";
            if (!DryRun)
            {
                NotificationService.NotifyCompletion(result.Ok ? "Transfer complete" : "Transfer finished with errors", StatusText, result.Ok, record: false);
                JobHistory.Add("Transfer", $"{o.Source}.{o.SourceSchema} → {o.Target}.{o.TargetSchema}", result.Summary, result.Ok, sw.Elapsed, JobHistory.SaveLog("transfer", LogLines));
            }
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        catch (Exception ex) { StatusText = "ERROR: " + ex.Message; LogLines.Add(">> ERROR: " + ex.Message); }
        finally
        {
            timer.Stop();
            IsBusy = false; CanCancel = false;
            _cts?.Dispose(); _cts = null;
        }
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        var o = BuildOptions(true, out var err);
        if (o == null) { StatusText = err!; return; }
        IsBusy = true;
        StatusText = "Counting rows on both sides...";
        RunRows.Clear();
        try
        {
            var rows = await TransferRunner.CompareAsync(o);
            foreach (var r in rows)
                RunRows.Add(new TableRunRow
                {
                    Table = r.Table,
                    Status = r.Matched ? "MATCHED" : !r.ExistsInTarget ? "missing in target" : "NOT MATCHED",
                    Failed = !r.Matched,
                    Rows = $"{r.SourceRows?.ToString("N0") ?? "—"} → {r.TargetRows?.ToString("N0") ?? "—"}",
                    Detail = $"columns {r.SourceColumns?.ToString() ?? "—"} → {r.TargetColumns?.ToString() ?? "—"}",
                });
            StatusText = $"Compare: {rows.Count(r => r.Matched)}/{rows.Count} table(s) match (columns + row counts).";
        }
        catch (Exception ex) { StatusText = "ERROR comparing: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();
    [RelayCommand] private void CopyLog() { if (LogLines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, LogLines)); }

    [RelayCommand]
    private void OpenScript()
    {
        if (LastScriptPath == null || !File.Exists(LastScriptPath)) return;
        Navigator.OpenSql(Path.GetFileName(LastScriptPath), File.ReadAllText(LastScriptPath), TargetProfile?.Id);
    }

    // ------------------------------------------------------------- presets

    private bool _suppressPreset;

    partial void OnSelectedPresetChanged(JobPreset? value)
    {
        if (_suppressPreset || value?.Transfer == null) return;
        var t = value.Transfer;
        PresetName = value.Name;
        _pendingDb = (t.SourceDatabase, t.TargetDatabase);
        SourceProfile = Profiles.FirstOrDefault(p => p.Id == t.SourceProfileId) ?? SourceProfile;
        TargetProfile = Profiles.FirstOrDefault(p => p.Id == t.TargetProfileId) ?? TargetProfile;
        // Same profile as before -> no profile-changed event, so apply the database directly.
        _loadingDbs = true;
        if (t.SourceDatabase != null) SourceDatabase = SourceDatabases.FirstOrDefault(d => string.Equals(d, t.SourceDatabase, StringComparison.OrdinalIgnoreCase)) ?? SourceDatabase;
        if (t.TargetDatabase != null) TargetDatabase = TargetDatabases.FirstOrDefault(d => string.Equals(d, t.TargetDatabase, StringComparison.OrdinalIgnoreCase)) ?? TargetDatabase;
        _loadingDbs = false;
        TargetSchema = t.TargetSchema;
        Mode = Modes.FirstOrDefault(m => m.Value == t.Mode) ?? Modes[0];
        NameCase = NameCases.FirstOrDefault(m => m.Value == t.NameCase) ?? NameCases[0];
        ApplyCode = t.ApplyCode;
        RowFilter = t.RowFilter ?? "";
        CommitEvery = t.CommitEveryRows.ToString();
        CopyIndexes = t.CopyIndexes;
        CopyForeignKeys = t.CopyForeignKeys;
        _pendingRequest = (t.SourceProfileId, t.SourceSchema, t.Tables.ToArray());
        _ = LoadObjectsAsync().ContinueWith(_ => Ui(() =>
        {
            var codeSet = t.CodeObjects.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in CodeObjects) c.IsChecked = codeSet.Contains(c.Name);
            if (t.Tables.Count == 0) foreach (var x in Tables) x.IsChecked = true;
            StatusText = $"Loaded preset '{value.Name}'.";
        }));
    }

    [RelayCommand]
    private void SavePreset()
    {
        var o = BuildOptions(false, out var err);
        if (o == null) { StatusText = err!; return; }
        if (string.IsNullOrWhiteSpace(PresetName)) { StatusText = "Type a preset name first."; return; }
        var allTables = Tables.All(t => t.IsChecked);
        PresetStore.Upsert(new JobPreset
        {
            Name = PresetName.Trim(),
            Kind = PresetKind.Transfer,
            Transfer = new TransferPreset
            {
                SourceProfileId = o.Source.Id, TargetProfileId = o.Target.Id, SourceDatabase = SourceProfile?.Database == o.Source.Database ? null : o.Source.Database, TargetDatabase = TargetProfile?.Database == o.Target.Database ? null : o.Target.Database, SourceSchema = o.SourceSchema, TargetSchema = o.TargetSchema,
                // "all tables" is saved as an empty list, so new tables are picked up by scheduled runs too
                Tables = allTables ? new() : o.Tables.ToList(),
                CodeObjects = o.CodeObjects.ToList(), Mode = o.Mode, NameCase = o.NameCase, ApplyCode = o.ApplyCode,
                RowFilter = o.RowFilter, CommitEveryRows = o.CommitEveryRows, CopyIndexes = o.CopyIndexes, CopyForeignKeys = o.CopyForeignKeys,
            },
        });
        StatusText = $"Saved preset '{PresetName.Trim()}'" + (allTables ? " (all tables — new tables included automatically)." : ".") + " Schedule it from the Scheduler page.";
    }

    private static void Ui(Action a)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == false) Application.Current.Dispatcher.BeginInvoke(a);
        else a();
    }
}
