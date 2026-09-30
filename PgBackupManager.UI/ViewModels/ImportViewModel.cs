using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;
using PgBackupManager.UI.Views;

namespace PgBackupManager.UI.ViewModels;

// One row of the post-import/on-demand compare report: source vs. target
// column count and real row count for one table, plus a match verdict.
public partial class CompareRowVm : ObservableObject
{
    public string Table { get; init; } = "";
    public string SourceColumns { get; init; } = "—";
    public string TargetColumns { get; init; } = "—";
    public string SourceRows { get; init; } = "—";
    public string TargetRows { get; init; } = "—";
    public bool IsMatched { get; init; }
    public string MismatchReason { get; init; } = "";
    public string StatusText => IsMatched ? "MATCHED" : "NOT MATCHED";
    public Brush StatusColor => IsMatched ? Brushes.SeaGreen : Brushes.IndianRed;
}

public partial class ImportViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private CancellationTokenSource? _cts;

    // SQL Server side — not a saved profile like Postgres connections; this
    // feature is meant for occasional migrations, not a repeatedly reused link.
    [ObservableProperty] private string _mssqlServer = Environment.MachineName;
    [ObservableProperty] private string _mssqlDatabase = "";
    [ObservableProperty] private bool _mssqlIntegratedSecurity = true;
    [ObservableProperty] private string _mssqlUsername = "";
    [ObservableProperty] private string _mssqlPassword = "";
    public bool UseSqlLogin => !MssqlIntegratedSecurity;
    partial void OnMssqlIntegratedSecurityChanged(bool value) => OnPropertyChanged(nameof(UseSqlLogin));

    public ObservableCollection<string> SourceSchemas { get; } = new();
    [ObservableProperty] private string? _sourceSchema = "dbo";

    public ObservableCollection<SyncCheckItem> AvailableTables { get; } = new();
    // What the checkbox list actually shows — a filtered view of AvailableTables
    // driven by TableSearchText, so a 200+ table source doesn't force scrolling
    // through the whole alphabet to find one table.
    public ObservableCollection<SyncCheckItem> FilteredTables { get; } = new();
    [ObservableProperty] private string _tableSearchText = "";
    partial void OnTableSearchTextChanged(string value) => ApplyTableFilter();

    // Postgres side — reuses the same saved connection profiles as every other tab.
    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _targetProfile;
    [ObservableProperty] private string _targetSchema = "dcci_migration_from_mssql";

    [ObservableProperty] private bool _dryRun = true;

    // Compare report — source (SQL Server) vs. target (Postgres), table by
    // table: column count, real row count, match/mismatch. Runs on demand
    // (Compare button) and automatically right after a real (non-dry-run)
    // import finishes, so "did it actually land correctly" doesn't need a
    // separate manual step.
    public ObservableCollection<CompareRowVm> CompareRows { get; } = new();
    private List<CompareRowVm> _allCompareRows = new();
    [ObservableProperty] private bool _hasCompareResult;
    [ObservableProperty] private bool _isComparing;
    [ObservableProperty] private int _compareSourceTables;
    [ObservableProperty] private int _compareTargetTables;
    [ObservableProperty] private long _compareSourceRows;
    [ObservableProperty] private long _compareTargetRows;
    [ObservableProperty] private int _compareMatchedCount;
    [ObservableProperty] private int _compareMismatchedCount;
    [ObservableProperty] private double _compareMatchedPercent;
    [ObservableProperty] private double _compareMismatchedPercent;
    [ObservableProperty] private bool _showCompareMatched = true;
    [ObservableProperty] private bool _showCompareMismatched = true;
    [ObservableProperty] private string _compareSearchText = "";
    partial void OnShowCompareMatchedChanged(bool value) => ApplyCompareFilter();
    partial void OnShowCompareMismatchedChanged(bool value) => ApplyCompareFilter();
    partial void OnCompareSearchTextChanged(string value) => ApplyCompareFilter();

    [ObservableProperty] private string _statusText = "Fill in the SQL Server connection, then Load Tables.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string _elapsedText = "";
    private Stopwatch? _stopwatch;
    private DispatcherTimer? _elapsedTimer;

    public ObservableCollection<string> LogLines { get; } = new();

    public string RunButtonText => DryRun ? "Preview Plan" : "Run Import";
    partial void OnDryRunChanged(bool value) => OnPropertyChanged(nameof(RunButtonText));

    public ImportViewModel()
    {
        foreach (var p in _profileStore.LoadAll().Where(p => p.Engine == DbEngine.PostgreSql).OrderBy(p => p.Name)) Profiles.Add(p);
        TargetProfile = Profiles.FirstOrDefault();
    }

    private MsSqlSource BuildSource() => new()
    {
        Server = MssqlServer,
        Database = MssqlDatabase,
        IntegratedSecurity = MssqlIntegratedSecurity,
        Username = MssqlIntegratedSecurity ? null : MssqlUsername,
        Password = MssqlIntegratedSecurity ? null : MssqlPassword,
    };

    [RelayCommand]
    private async Task LoadTablesAsync()
    {
        if (string.IsNullOrWhiteSpace(MssqlServer) || string.IsNullOrWhiteSpace(MssqlDatabase))
        {
            StatusText = "Fill in the SQL Server host and database name first.";
            return;
        }
        try
        {
            IsBusy = true;
            StatusText = "Connecting to SQL Server...";
            var source = BuildSource();

            var prevSchema = SourceSchema;
            SourceSchemas.Clear();
            foreach (var s in await MsSqlImportRunner.ListSchemasAsync(source)) SourceSchemas.Add(s);
            SourceSchema = SourceSchemas.Contains(prevSchema ?? "") ? prevSchema
                : SourceSchemas.Contains("dbo") ? "dbo" : SourceSchemas.FirstOrDefault();

            await LoadTableListAsync();
            StatusText = $"Loaded {SourceSchemas.Count} schema(s), {AvailableTables.Count} table(s) in '{SourceSchema}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"ERROR connecting to SQL Server: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSourceSchemaChanged(string? value) => _ = LoadTableListAsync();

    private async Task LoadTableListAsync()
    {
        if (string.IsNullOrWhiteSpace(MssqlDatabase) || string.IsNullOrEmpty(SourceSchema)) { AvailableTables.Clear(); return; }
        try
        {
            var source = BuildSource();
            var prevUnchecked = AvailableTables.Where(t => !t.IsChecked).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            AvailableTables.Clear();
            foreach (var t in await MsSqlImportRunner.ListTablesAsync(source, SourceSchema))
                AvailableTables.Add(new SyncCheckItem { Name = t, Label = t, IsChecked = !prevUnchecked.Contains(t) });
            ApplyTableFilter();
        }
        catch (Exception ex)
        {
            StatusText = $"ERROR loading tables: {ex.Message}";
        }
    }

    private void ApplyTableFilter()
    {
        var term = (TableSearchText ?? "").Trim();
        FilteredTables.Clear();
        foreach (var t in AvailableTables)
        {
            if (string.IsNullOrEmpty(term) || t.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                FilteredTables.Add(t);
        }
    }

    // ALL/NONE act on every loaded table, not just what the current search
    // term shows — ticking "ALL" while a filter narrows the view would
    // otherwise silently untick everything outside that filter.
    [RelayCommand] private void SelectAllTables() { foreach (var t in AvailableTables) t.IsChecked = true; }
    [RelayCommand] private void SelectNoneTables() { foreach (var t in AvailableTables) t.IsChecked = false; }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(MssqlServer) || string.IsNullOrWhiteSpace(MssqlDatabase)) { StatusText = "Fill in the SQL Server host and database name."; return; }
        if (TargetProfile is null) { StatusText = "Pick a target Postgres profile."; return; }
        if (string.IsNullOrWhiteSpace(TargetSchema)) { StatusText = "Type a target schema name."; return; }

        var tables = AvailableTables.Where(t => t.IsChecked).Select(t => t.Name).ToList();
        if (tables.Count == 0) { StatusText = "Select at least one table."; return; }

        if (!DryRun)
        {
            var confirmed = ConfirmDialog.Confirm(
                Application.Current?.MainWindow,
                "Confirm import",
                $"Import {tables.Count} table(s) from SQL Server\n" +
                $"'{MssqlDatabase}'.{SourceSchema} @ {MssqlServer}\n" +
                $"INTO Postgres schema '{TargetSchema}' in '{TargetProfile.Database}' @ {TargetProfile.Host}:{TargetProfile.Port}\n\n" +
                "Missing tables are created fresh; tables that already exist in the target only get rows appended (no structure changes).\n\nProceed?",
                confirmText: "Yes, import");
            if (!confirmed) { StatusText = "Cancelled."; return; }
        }

        LogLines.Clear();
        _cts = new CancellationTokenSource();
        IsBusy = true; CanCancel = true;
        StatusText = DryRun ? "Building preview..." : "Importing...";
        ElapsedText = "00:00";
        _stopwatch = Stopwatch.StartNew();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = FormatDuration(_stopwatch.Elapsed);
        _elapsedTimer.Start();

        try
        {
            var runner = new MsSqlImportRunner();
            runner.LogLine += (_, line) => AppendLog(line);

            var tgtPwd = SecretProtector.Unprotect(TargetProfile.EncryptedPasswordBase64);
            var opts = new ImportOptions
            {
                Source = BuildSource(),
                TargetProfile = TargetProfile,
                TargetPassword = tgtPwd,
                TargetSchema = TargetSchema.Trim(),
                SourceSchema = SourceSchema!,
                Tables = tables,
                DryRun = DryRun,
            };

            var result = await runner.RunAsync(opts, _cts.Token);

            _elapsedTimer.Stop();
            ElapsedText = FormatDuration(_stopwatch.Elapsed);

            if (result.Ok)
            {
                StatusText = $"{result.Summary} (took {ElapsedText})";
                if (!DryRun)
                {
                    NotificationService.NotifyCompletion("Import complete", StatusText, success: true);
                    AppendLog(">> comparing source vs target...");
                    await RunCompareAsync(tables, opts.SourceSchema, opts.TargetSchema);
                }
            }
            else
            {
                StatusText = $"Import failed: {result.Summary}";
                NotificationService.NotifyCompletion("Import failed", StatusText, success: false);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog(">> Cancelled.");
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($">> ERROR: {ex.Message}");
            StatusText = $"ERROR: {ex.Message}";
            NotificationService.NotifyCompletion("Import error", StatusText, success: false);
        }
        finally
        {
            _elapsedTimer?.Stop();
            IsBusy = false; CanCancel = false;
            _cts?.Dispose(); _cts = null;
        }
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (string.IsNullOrWhiteSpace(MssqlServer) || string.IsNullOrWhiteSpace(MssqlDatabase)) { StatusText = "Fill in the SQL Server host and database name."; return; }
        if (TargetProfile is null) { StatusText = "Pick a target Postgres profile."; return; }
        if (string.IsNullOrWhiteSpace(TargetSchema)) { StatusText = "Type a target schema name."; return; }
        if (AvailableTables.Count == 0) { StatusText = "Load tables first."; return; }

        var tables = AvailableTables.Select(t => t.Name).ToList();
        await RunCompareAsync(tables, SourceSchema!, TargetSchema.Trim());
    }

    // Shared by the manual Compare button and the automatic post-import run —
    // both need the same source/target schema pairing the tables came from,
    // which the caller already knows (typed in / just used for the import).
    private async Task RunCompareAsync(System.Collections.Generic.List<string> tables, string sourceSchema, string targetSchema)
    {
        if (TargetProfile is null) return;

        IsBusy = true; IsComparing = true;
        StatusText = "Comparing source vs target...";
        try
        {
            var tgtPwd = SecretProtector.Unprotect(TargetProfile.EncryptedPasswordBase64);
            var summary = await DataCompareRunner.CompareAsync(BuildSource(), sourceSchema, TargetProfile, tgtPwd, targetSchema, tables);

            _allCompareRows = summary.Rows.Select(r => new CompareRowVm
            {
                Table = r.Table,
                SourceColumns = r.ExistsInSource ? r.SourceColumns!.Value.ToString() : "—",
                TargetColumns = r.ExistsInTarget ? r.TargetColumns!.Value.ToString() : "—",
                SourceRows = r.ExistsInSource ? r.SourceRows!.Value.ToString("N0") : "—",
                TargetRows = r.ExistsInTarget ? r.TargetRows!.Value.ToString("N0") : "—",
                IsMatched = r.FullyMatched,
                MismatchReason = !r.ExistsInSource ? "missing in source"
                    : !r.ExistsInTarget ? "missing in target"
                    : !r.ColumnsMatch && !r.RowsMatch ? "columns + rows differ"
                    : !r.ColumnsMatch ? "column count differs"
                    : "row count differs",
            }).ToList();

            CompareSourceTables = summary.SourceTableCount;
            CompareTargetTables = summary.TargetTableCount;
            CompareSourceRows = summary.SourceTotalRows;
            CompareTargetRows = summary.TargetTotalRows;
            CompareMatchedCount = summary.MatchedCount;
            CompareMismatchedCount = summary.MismatchedCount;
            CompareMatchedPercent = summary.MatchedPercent;
            CompareMismatchedPercent = summary.MismatchedPercent;
            HasCompareResult = true;
            ApplyCompareFilter();

            StatusText = $"Compare complete — {summary.MatchedCount}/{summary.Rows.Count} table(s) fully matched ({summary.MatchedPercent}%).";
        }
        catch (Exception ex)
        {
            StatusText = $"ERROR comparing: {ex.Message}";
        }
        finally
        {
            IsBusy = false; IsComparing = false;
        }
    }

    private void ApplyCompareFilter()
    {
        var term = (CompareSearchText ?? "").Trim();
        CompareRows.Clear();
        foreach (var r in _allCompareRows)
        {
            var statusOk = (r.IsMatched && ShowCompareMatched) || (!r.IsMatched && ShowCompareMismatched);
            if (!statusOk) continue;
            if (!string.IsNullOrEmpty(term) && !r.Table.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            CompareRows.Add(r);
        }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void CopyLog()
    {
        if (LogLines.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, LogLines));
    }

    private void AppendLog(string line)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.BeginInvoke(() => AppendLog(line));
        else
            LogLines.Add(line);
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
