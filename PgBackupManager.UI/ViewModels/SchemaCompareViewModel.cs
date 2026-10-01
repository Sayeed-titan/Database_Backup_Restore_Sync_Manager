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

public sealed class SchemaDiffRow
{
    public SchemaDiffItem Item { get; init; } = null!;
    public string Status => Item.Status switch
    {
        CompareStatus.OnlyInSource => "only in source",
        CompareStatus.OnlyInTarget => "only in target",
        CompareStatus.Different => "different",
        _ => "same",
    };
    public string Kind => Item.ObjectType;
    public string Name => Item.Display;
    public string Detail => Item.Detail;
    public bool IsSource => Item.Status == CompareStatus.OnlyInSource;
    public bool IsTarget => Item.Status == CompareStatus.OnlyInTarget;
    public bool IsDiff => Item.Status == CompareStatus.Different;
}

public partial class SchemaCompareViewModel : ObservableObject
{
    private SchemaCompareRunner? _runner;
    private CancellationTokenSource? _cts;
    private List<SchemaDiffRow> _all = new();

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _sourceProfile;
    [ObservableProperty] private ConnectionProfile? _targetProfile;
    public ObservableCollection<string> SourceSchemas { get; } = new();
    public ObservableCollection<string> TargetSchemas { get; } = new();
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
    public IReadOnlyList<Choice<NameCase>> NameCases { get; } = new Choice<NameCase>[]
    {
        new(Core.Providers.NameCase.TargetDefault, "Target default (PG lower, Oracle UPPER)"),
        new(Core.Providers.NameCase.Lower, "lower_case"),
        new(Core.Providers.NameCase.Upper, "UPPER_CASE"),
        new(Core.Providers.NameCase.Preserve, "Preserve exactly"),
    };
    [ObservableProperty] private Choice<NameCase>? _nameCase;
    [ObservableProperty] private bool _compareCode = true;
    [ObservableProperty] private bool _compareDefinitions = true;

    public ObservableCollection<SchemaDiffRow> Rows { get; } = new();
    [ObservableProperty] private SchemaDiffRow? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showSource = true;
    [ObservableProperty] private bool _showTarget = true;
    [ObservableProperty] private bool _showDifferent = true;
    [ObservableProperty] private int _onlySourceCount;
    [ObservableProperty] private int _onlyTargetCount;
    [ObservableProperty] private int _differentCount;
    [ObservableProperty] private int _tablesInBoth;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Pick two connections + schemas (any engines) and Compare. Nothing is changed — the sync script opens in the SQL Editor for review.";

    public string SourceDef => Selected?.Item.SourceDef ?? (Selected == null ? "" : "-- (no definition on this side)");
    public string TargetDef => Selected?.Item.TargetDef ?? (Selected == null ? "" : "-- (no definition on this side)");
    partial void OnSelectedChanged(SchemaDiffRow? value) { OnPropertyChanged(nameof(SourceDef)); OnPropertyChanged(nameof(TargetDef)); }

    partial void OnSearchChanged(string value) => Apply();
    partial void OnShowSourceChanged(bool value) => Apply();
    partial void OnShowTargetChanged(bool value) => Apply();
    partial void OnShowDifferentChanged(bool value) => Apply();

    public SchemaCompareViewModel()
    {
        NameCase = NameCases[0];
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

    private async Task LoadSideAsync(ConnectionProfile? p, ObservableCollection<string> dbs, Action<string?> selectDb, string hasProp, string? prefer,
        ObservableCollection<string> schemas, Action<string?> selectSchema)
    {
        _loadingDbs = true;
        string? chosen;
        try { chosen = await DbPicker.LoadAsync(p, dbs, prefer); selectDb(chosen); }
        finally { _loadingDbs = false; OnPropertyChanged(hasProp); }
        await LoadSchemasAsync(p?.WithDatabase(chosen), schemas, selectSchema);
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

    [RelayCommand]
    private void Swap()
    {
        var (sp, ss, tp, ts) = (SourceProfile, SourceSchema, TargetProfile, TargetSchema);
        _pendingDb = (TargetDatabase, SourceDatabase);
        SourceProfile = tp; TargetProfile = sp;
        Application.Current?.Dispatcher.BeginInvoke(() => { SourceSchema = ts; TargetSchema = ss; }, System.Windows.Threading.DispatcherPriority.Background);
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (SourceProfile == null || TargetProfile == null || string.IsNullOrEmpty(SourceSchema) || string.IsNullOrEmpty(TargetSchema))
        { StatusText = "Pick both connections and both schemas."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        _runner = new SchemaCompareRunner();
        _runner.Progress += (_, m) => Application.Current?.Dispatcher.BeginInvoke(() => StatusText = m);
        try
        {
            var r = await _runner.CompareAsync(new SchemaCompareOptions
            {
                Source = SrcEff!, Target = TgtEff!, SourceSchema = SourceSchema!, TargetSchema = TargetSchema!,
                NameCase = NameCase?.Value ?? Core.Providers.NameCase.TargetDefault, CompareCode = CompareCode, CompareDefinitions = CompareDefinitions,
            }, _cts.Token);
            _all = r.Items.Select(i => new SchemaDiffRow { Item = i }).ToList();
            OnlySourceCount = r.Count(CompareStatus.OnlyInSource);
            OnlyTargetCount = r.Count(CompareStatus.OnlyInTarget);
            DifferentCount = r.Count(CompareStatus.Different);
            TablesInBoth = r.TablesCompared;
            HasResult = true;
            Apply();
            StatusText = r.Items.Count == 0 ? "The schemas match — no differences." : $"{r.Items.Count} difference(s). Select rows (or none = all) and Generate Sync Script.";
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        catch (Exception ex) { StatusText = "ERROR: " + ex.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    private void Apply()
    {
        Rows.Clear();
        var term = Search.Trim();
        foreach (var r in _all)
        {
            if (r.IsSource && !ShowSource || r.IsTarget && !ShowTarget || r.IsDiff && !ShowDifferent) continue;
            if (term.Length > 0 && !(r.Name + " " + r.Kind + " " + r.Detail).Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(r);
        }
    }

    // selected = rows ticked in the grid; empty = everything currently shown.
    public void GenerateScript(IList<SchemaDiffRow> selected)
    {
        if (_runner == null) { StatusText = "Run a compare first."; return; }
        var items = (selected.Count > 0 ? selected : Rows.ToList()).Select(r => r.Item).ToList();
        var script = _runner.BuildSyncScript(items);
        Navigator.OpenSql($"sync {SourceSchema} → {TargetSchema}", script, TargetProfile?.Id, TgtEff?.Database);
        StatusText = $"Sync script for {items.Count} difference(s) opened in the SQL Editor on {TargetProfile} — review, then Run.";
    }
}
