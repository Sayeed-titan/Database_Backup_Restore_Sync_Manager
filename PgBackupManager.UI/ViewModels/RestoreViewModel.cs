using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;

namespace PgBackupManager.UI.ViewModels;

public partial class DiffRow : ObservableObject
{
    public string Kind { get; init; } = "";
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public DiffStatus Status { get; init; }
    public int? DumpId { get; init; }
    public string FullName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
    public string StatusText => Status switch
    {
        DiffStatus.NewInBackup => "NEW (will create)",
        DiffStatus.Existing => "EXISTS",
        DiffStatus.MissingFromBackup => "MISSING from backup",
        _ => ""
    };
    public Brush StatusColor => Status switch
    {
        DiffStatus.NewInBackup => Brushes.SeaGreen,
        DiffStatus.Existing => Brushes.Goldenrod,
        DiffStatus.MissingFromBackup => Brushes.IndianRed,
        _ => Brushes.Gray
    };

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isSelectable = true;
}

public partial class SchemaFilterItem : ObservableObject
{
    public string Name { get; init; } = "";
    public int Total { get; init; }
    public int NewCount { get; init; }
    public string Label => $"{Name}  ({Total})";
    public Action<SchemaFilterItem>? Toggled;

    [ObservableProperty] private bool _isChecked = true;
    partial void OnIsCheckedChanged(bool value) => Toggled?.Invoke(this);
}

public partial class RestoreViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private CancellationTokenSource? _cts;

    private IReadOnlyList<TocEntry> _allToc = Array.Empty<TocEntry>();

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _selectedProfile;

    [ObservableProperty] private string _backupFile = "";
    [ObservableProperty] private string _statusText = "Pick a backup file and a target profile, then 'Analyze'.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;

    // Restore target display (where pg_restore will WRITE)
    [ObservableProperty] private string _restoreTargetText = "(no profile selected)";
    [ObservableProperty] private Brush _restoreTargetBrush = Brushes.Gray;
    [ObservableProperty] private string _restoreTargetHint = "";

    [ObservableProperty] private bool _singleTransaction = true;
    [ObservableProperty] private bool _cleanFirst;
    [ObservableProperty] private bool _noOwner = true;
    [ObservableProperty] private bool _noPrivileges = true;

    [ObservableProperty] private bool _showNew = true;
    [ObservableProperty] private bool _showExisting;
    [ObservableProperty] private bool _showMissing;
    [ObservableProperty] private string _searchText = "";

    public ObservableCollection<DiffRow> Rows { get; } = new();
    public ObservableCollection<SchemaFilterItem> BackupSchemas { get; } = new();
    private List<DiffRow> _allRows = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private int _newCount;
    [ObservableProperty] private int _existingCount;
    [ObservableProperty] private int _missingCount;
    [ObservableProperty] private int _selectedCount;
    // Objects that will actually be restored = everything in the ticked schemas
    // (full restore). Drives the "WILL RESTORE" card.
    [ObservableProperty] private int _willRestoreCount;

    // Per-kind breakdowns (e.g. "0 tables · 250 fns · 2 types") so it is obvious
    // what each bucket contains — answers "why does NEW show only functions?".
    [ObservableProperty] private string _newBreakdown = "";
    [ObservableProperty] private string _existingBreakdown = "";
    [ObservableProperty] private string _missingBreakdown = "";

    public RestoreViewModel()
    {
        ReloadProfiles();
        UpdateTargetDisplay();
    }

    public void ReloadProfiles()
    {
        var currentId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var p in _profileStore.LoadAll().OrderBy(p => p.Name)) Profiles.Add(p);
        SelectedProfile = currentId.HasValue
            ? Profiles.FirstOrDefault(p => p.Id == currentId) ?? Profiles.FirstOrDefault()
            : Profiles.FirstOrDefault();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowNewChanged(bool value) => ApplyFilter();
    partial void OnShowExistingChanged(bool value) => ApplyFilter();
    partial void OnShowMissingChanged(bool value) => ApplyFilter();

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        UpdateTargetDisplay();
        // A diff is only valid against the DB it was computed on. Switching the
        // target invalidates it — clear so nobody restores using a stale comparison.
        _allRows = new();
        _allToc = Array.Empty<TocEntry>();
        Rows.Clear();
        BackupSchemas.Clear();
        NewCount = ExistingCount = MissingCount = SelectedCount = WillRestoreCount = 0;
        NewBreakdown = ExistingBreakdown = MissingBreakdown = "";
        if (value != null)
            StatusText = $"Target set to '{value.Name}'. Click Analyze to compare the backup against it.";
    }

    private void UpdateTargetDisplay()
    {
        if (SelectedProfile is null)
        {
            RestoreTargetText = "(no profile selected)";
            RestoreTargetBrush = Brushes.Gray;
            RestoreTargetHint = "";
            return;
        }

        var p = SelectedProfile;
        RestoreTargetText = $"{p.Database}  @  {p.Host}:{p.Port}   (user: {p.Username})";

        var host = p.Host.Trim().ToLowerInvariant();
        var isLocal = host is "localhost" or "127.0.0.1" or "::1" or ".";
        if (isLocal)
        {
            RestoreTargetBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x7A, 0x3D)); // green = safe
            RestoreTargetHint = "Local target — safe to restore.";
        }
        else
        {
            RestoreTargetBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x50, 0x00)); // orange = caution
            RestoreTargetHint = "REMOTE target — this writes to a network server. Double-check before restoring.";
        }
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var settings = _settingsStore.Load();
        var dlg = new OpenFileDialog
        {
            Title = "Pick a PostgreSQL backup file",
            Filter = "PG backup (*.dump;*.sql;*.tar;*.backup)|*.dump;*.sql;*.tar;*.backup|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(settings.DefaultRestoreSource) ? settings.DefaultRestoreSource : null,
        };
        if (dlg.ShowDialog() == true) BackupFile = dlg.FileName;
    }

    [RelayCommand]
    private async Task CreateTargetDbAsync()
    {
        if (SelectedProfile is null) { StatusText = "Pick a target profile first."; return; }
        IsBusy = true;
        try
        {
            StatusText = $"Creating database '{SelectedProfile.Database}' on {SelectedProfile.Host}...";
            var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);
            var (ok, msg) = await DatabaseAdmin.CreateDatabaseAsync(SelectedProfile, pwd);
            StatusText = ok ? msg : $"Create DB failed: {msg}";
        }
        catch (Exception ex)
        {
            StatusText = $"Create DB error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(BackupFile) || !File.Exists(BackupFile)) { StatusText = "Pick a valid backup file."; return; }
        if (SelectedProfile is null) { StatusText = "Pick a target profile."; return; }

        var settings = _settingsStore.Load();
        var tools = PgToolsLocator.Locate(settings.PgBinDirOverride);
        if (string.IsNullOrEmpty(tools.PgRestore)) { StatusText = "pg_restore.exe not found. Configure it in Settings."; return; }

        IsBusy = true;
        try
        {
            StatusText = $"Parsing backup TOC and querying '{SelectedProfile.Database}'...";
            var tocTask = BackupInspector.InspectAsync(tools.PgRestore!, BackupFile);
            var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);
            var liveTask = DbObjectInspector.InspectAsync(SelectedProfile.BuildConnectionString(pwd));

            await Task.WhenAll(tocTask, liveTask);
            _allToc = await tocTask;
            var live = await liveTask;

            var diff = ObjectDiffer.Diff(_allToc, live);

            _allRows = diff.Select(d => new DiffRow
            {
                Kind = d.Kind,
                Schema = d.Schema,
                Name = d.Name,
                Status = d.Status,
                DumpId = d.DumpId,
                IsChecked = d.Status == DiffStatus.NewInBackup,
                IsSelectable = d.DumpId.HasValue,
            }).ToList();

            foreach (var r in _allRows)
                r.PropertyChanged += Row_PropertyChanged;

            NewCount = _allRows.Count(r => r.Status == DiffStatus.NewInBackup);
            ExistingCount = _allRows.Count(r => r.Status == DiffStatus.Existing);
            MissingCount = _allRows.Count(r => r.Status == DiffStatus.MissingFromBackup);

            NewBreakdown = BuildBreakdown(DiffStatus.NewInBackup);
            ExistingBreakdown = BuildBreakdown(DiffStatus.Existing);
            MissingBreakdown = BuildBreakdown(DiffStatus.MissingFromBackup);

            BuildSchemaFilter();
            ApplyFilter();
            UpdateSelectedCount();
            UpdateWillRestore();
            StatusText = $"Backup has {_allToc.Count} TOC entries · {NewCount} new, {ExistingCount} existing, {MissingCount} missing-from-backup. " +
                         $"FULL restore of ticked schema(s) into '{SelectedProfile.Database}' @ {SelectedProfile.Host} — all objects + data.";
        }
        catch (Exception ex)
        {
            StatusText = $"ERROR analyzing: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // "12 tables · 250 fns · 3 views · 2 types · 5 seqs" — only non-zero kinds shown.
    private string BuildBreakdown(DiffStatus status)
    {
        var rows = _allRows.Where(r => r.Status == status).ToList();
        if (rows.Count == 0) return "—";

        int Count(string kind) => rows.Count(r => r.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        var parts = new List<string>();
        void Add(int n, string label) { if (n > 0) parts.Add($"{n} {label}"); }

        Add(Count("TABLE"), "tables");
        Add(Count("FUNCTION") + Count("PROCEDURE"), "fns");
        Add(Count("VIEW") + Count("MATERIALIZED VIEW"), "views");
        Add(Count("TYPE") + Count("DOMAIN"), "types");
        Add(Count("SEQUENCE"), "seqs");
        Add(Count("SCHEMA"), "schemas");

        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    private void BuildSchemaFilter()
    {
        foreach (var s in BackupSchemas) s.Toggled = null;
        BackupSchemas.Clear();

        // Only schemas that actually exist IN THE BACKUP FILE (objects carrying a
        // dump id). Live-only schemas (MISSING-from-backup) are not restorable and
        // must not appear here.
        var groups = _allRows
            .Where(r => r.DumpId.HasValue && !string.IsNullOrEmpty(r.Schema))
            .GroupBy(r => r.Schema, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            var item = new SchemaFilterItem
            {
                Name = g.Key,
                Total = g.Count(),
                NewCount = g.Count(r => r.Status == DiffStatus.NewInBackup),
                IsChecked = true,
            };
            item.Toggled = OnSchemaToggled;
            BackupSchemas.Add(item);
        }
    }

    private void OnSchemaToggled(SchemaFilterItem item)
    {
        ApplyFilter();
        UpdateWillRestore();
    }

    // Full restore = every restorable object in the ticked schemas.
    private void UpdateWillRestore()
    {
        var included = BackupSchemas.Where(s => s.IsChecked)
            .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        WillRestoreCount = _allRows.Count(r => r.DumpId.HasValue && included.Contains(r.Schema));
    }

    private void ApplyFilter()
    {
        var term = (SearchText ?? "").Trim();
        var included = BackupSchemas.Where(s => s.IsChecked)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasSchemaFilter = BackupSchemas.Count > 0;

        Rows.Clear();
        foreach (var r in _allRows)
        {
            // Schema include filter only governs restorable (backup) objects.
            // MISSING-from-backup rows are live-only and not affected by the chips.
            if (hasSchemaFilter && r.DumpId.HasValue && !string.IsNullOrEmpty(r.Schema) && !included.Contains(r.Schema)) continue;

            var statusOk = (r.Status == DiffStatus.NewInBackup && ShowNew)
                        || (r.Status == DiffStatus.Existing && ShowExisting)
                        || (r.Status == DiffStatus.MissingFromBackup && ShowMissing);
            if (!statusOk) continue;

            if (!string.IsNullOrEmpty(term)
                && !r.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !r.Kind.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;

            Rows.Add(r);
        }
    }

    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffRow.IsChecked)) UpdateSelectedCount();
    }

    private void UpdateSelectedCount() =>
        SelectedCount = _allRows.Count(r => r.IsChecked && r.DumpId.HasValue);

    [RelayCommand] private void SelectAllVisible() { foreach (var r in Rows.Where(r => r.IsSelectable)) r.IsChecked = true; UpdateSelectedCount(); }
    [RelayCommand] private void SelectNoneVisible() { foreach (var r in Rows) r.IsChecked = false; UpdateSelectedCount(); }
    [RelayCommand]
    private void SelectNewOnly()
    {
        var included = BackupSchemas.Where(s => s.IsChecked).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasSchemaFilter = BackupSchemas.Count > 0;
        foreach (var r in _allRows)
        {
            var schemaOk = !hasSchemaFilter || string.IsNullOrEmpty(r.Schema) || included.Contains(r.Schema);
            r.IsChecked = schemaOk && r.IsSelectable && r.Status == DiffStatus.NewInBackup;
        }
        ApplyFilter();
        UpdateSelectedCount();
    }

    [RelayCommand]
    private async Task StartRestoreAsync()
    {
        if (string.IsNullOrWhiteSpace(BackupFile) || !File.Exists(BackupFile)) { StatusText = "Pick a backup file."; return; }
        if (SelectedProfile is null) { StatusText = "Pick a target profile."; return; }

        // FULL-FIDELITY restore driven by the "Schemas to restore" checkboxes.
        // pg_restore replays the WHOLE schema (tables, data, indexes, constraints,
        // sequences, triggers) so nothing is dropped.
        var ticked = BackupSchemas.Where(s => s.IsChecked).Select(s => s.Name).ToList();
        if (BackupSchemas.Count == 0) { StatusText = "Run Analyze first."; return; }
        if (ticked.Count == 0) { StatusText = "Tick at least one schema to restore."; return; }

        var allTicked = ticked.Count == BackupSchemas.Count;
        var schemasInvolved = string.Join(", ", ticked.OrderBy(s => s));

        // Count what is being restored (definitions; data rides along automatically).
        var objCount = _allRows.Count(r => r.DumpId.HasValue && ticked.Contains(r.Schema, StringComparer.OrdinalIgnoreCase));
        var existingInScope = _allRows.Count(r => r.DumpId.HasValue && r.Status == DiffStatus.Existing
                                                  && ticked.Contains(r.Schema, StringComparer.OrdinalIgnoreCase));

        var isLocal = SelectedProfile.Host.Trim().ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or ".";

        // Pre-flight: existing objects + single-transaction + no drop = guaranteed failure.
        if (existingInScope > 0 && SingleTransaction && !CleanFirst)
        {
            MessageBox.Show(
                $"{existingInScope} object(s) already exist in '{SelectedProfile.Database}'.\n\n" +
                "A full restore re-creates every object, so pg_restore will hit \"already exists\" " +
                "errors and — because 'Single transaction' is ON — roll back the ENTIRE restore.\n\n" +
                "Do ONE of these first:\n" +
                "   • Tick 'Drop existing first (--clean --if-exists)', or\n" +
                "   • Restore into a fresh empty database (use 'Create Target DB' with a new name).",
                "Restore would fail — target already has these objects",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText = $"{existingInScope} objects already exist — enable 'Drop existing first' or use an empty DB.";
            return;
        }

        // Safety confirmation — shows exactly WHERE this is going and that it is FULL.
        var confirm = MessageBox.Show(
            $"FULL restore of {ticked.Count} schema(s): {schemasInvolved}\n" +
            $"({objCount} objects + all their data, indexes, constraints & sequences)\n\n" +
            $"INTO target database:\n" +
            $"   {SelectedProfile.Database} @ {SelectedProfile.Host}:{SelectedProfile.Port}\n" +
            $"   (user: {SelectedProfile.Username})\n" +
            $"   {(isLocal ? "✓ LOCAL target — safe." : "⚠ REMOTE / network target!")}\n\n" +
            (CleanFirst ? "⚠ 'Drop existing first' is ON — existing objects will be DROPPED then recreated.\n\n" : "") +
            "Proceed?",
            isLocal ? "Confirm FULL restore" : "⚠ CONFIRM RESTORE TO REMOTE SERVER",
            MessageBoxButton.YesNo,
            (CleanFirst || !isLocal) ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) { StatusText = "Restore cancelled."; return; }

        var settings = _settingsStore.Load();
        var tools = PgToolsLocator.Locate(settings.PgBinDirOverride);
        if (string.IsNullOrEmpty(tools.PgRestore)) { StatusText = "pg_restore.exe not found. Configure it in Settings."; return; }

        var opts = new RestoreOptions
        {
            Host = SelectedProfile.Host,
            Port = SelectedProfile.Port,
            Database = SelectedProfile.Database,
            Username = SelectedProfile.Username,
            BackupFile = BackupFile,
            SingleTransaction = SingleTransaction,
            CleanFirst = CleanFirst,
            NoOwner = NoOwner,
            NoPrivileges = NoPrivileges,
            RestoreEntireFile = allTicked,
            IncludeSchemas = allTicked ? null : ticked,
        };

        LogLines.Clear();
        AppendLog($">> pg_restore · FULL restore · schemas: {schemasInvolved}");
        AppendLog($">> mode: {(allTicked ? "entire archive" : "--schema=" + string.Join(" --schema=", ticked))}");
        AppendLog($">> TARGET: {opts.Database} @ {opts.Host}:{opts.Port} (user {opts.Username})");
        AppendLog($">> options: single-tx={opts.SingleTransaction} clean={opts.CleanFirst} no-owner={opts.NoOwner} no-priv={opts.NoPrivileges}");

        _cts = new CancellationTokenSource();
        IsBusy = true; CanCancel = true;
        StatusText = "Running pg_restore...";

        try
        {
            var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);
            var runner = new PgRestoreRunner();
            runner.Process.StdoutLine += OnLogLine;
            runner.Process.StderrLine += OnLogLine;

            var exit = await runner.RunAsync(tools.PgRestore!, opts, pwd, _cts.Token);

            runner.Process.StdoutLine -= OnLogLine;
            runner.Process.StderrLine -= OnLogLine;

            if (exit == 0)
            {
                AppendLog(">> SUCCESS");
                StatusText = $"Restore completed into '{opts.Database}' @ {opts.Host}.";
            }
            else
            {
                AppendLog($">> pg_restore exited with code {exit}");
                StatusText = $"pg_restore failed (exit {exit}). See log.";
            }
        }
        catch (OperationCanceledException) { AppendLog(">> Cancelled."); StatusText = "Cancelled."; }
        catch (Exception ex) { AppendLog($">> ERROR: {ex.Message}"); StatusText = $"ERROR: {ex.Message}"; }
        finally
        {
            IsBusy = false; CanCancel = false;
            _cts?.Dispose(); _cts = null;
        }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void CopyLog()
    {
        if (LogLines.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, LogLines));
    }

    private void OnLogLine(object? sender, string line) => AppendLog(line);

    private void AppendLog(string line)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == false)
            Application.Current.Dispatcher.BeginInvoke(() => AppendLog(line));
        else
            LogLines.Add(line);
    }
}
