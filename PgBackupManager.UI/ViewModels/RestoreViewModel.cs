using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;
using PgBackupManager.UI.Views;

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
    [ObservableProperty] private bool _useLatestBackup;
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

    // Parallel restore workers (pg_restore --jobs). Postgres itself refuses to
    // combine --jobs with --single-transaction, so this is only usable once
    // Single transaction is unchecked — enforced in StartRestoreAsync's
    // pre-flight check, not silently overridden here.
    [ObservableProperty] private int _parallelJobs = 1;

    [ObservableProperty] private bool _showNew = true;
    [ObservableProperty] private bool _showExisting;
    [ObservableProperty] private bool _showMissing;
    [ObservableProperty] private string _searchText = "";

    // Elapsed / ETA while a restore is running. Weighted by total TOC entries
    // in the ticked schemas (tables, data, indexes, constraints, etc. all count
    // individually) since that is the actual unit pg_restore's verbose output
    // advances through, one "creating X" / "processing data for table X" line
    // at a time.
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _hasProgress;
    private Stopwatch? _stopwatch;
    private DispatcherTimer? _elapsedTimer;
    private int _totalEntriesInScope;
    private int _doneEntries;
    private static readonly Regex RestoreProgressRx =
        new(@"^pg_restore: (creating|processing data for table|executing SEQUENCE SET) ", RegexOptions.Compiled);

    public ObservableCollection<DiffRow> Rows { get; } = new();
    public ObservableCollection<SchemaFilterItem> BackupSchemas { get; } = new();
    private List<DiffRow> _allRows = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private int _newCount;
    [ObservableProperty] private int _existingCount;
    [ObservableProperty] private int _missingCount;
    [ObservableProperty] private int _selectedCount;
    // Objects that will actually be restored = everything in the ticked schemas
    // (full restore). Drives the "WILL RESTORE" card. This is a workload size,
    // not an outcome — see ChangesCount for what actually happens.
    [ObservableProperty] private int _willRestoreCount;

    // What will REALLY happen to the target given the current Drop-existing /
    // Single-transaction options — drives the "CHANGES TO TARGET" card.
    [ObservableProperty] private int _changesCount;
    [ObservableProperty] private string _changesDetail = "";
    [ObservableProperty] private Brush _changesBrush = Brushes.Gray;

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
    partial void OnCleanFirstChanged(bool value) => UpdateWillRestore();
    partial void OnSingleTransactionChanged(bool value) => UpdateWillRestore();

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        UpdateTargetDisplay();
        InvalidateAnalysis();
        if (value != null)
            StatusText = $"Target set to '{value.Name}'. Click Analyze to compare the backup against it.";
    }

    // A diff (and the schema tick list it drives) is only valid for the exact
    // file it was computed from. Changing the file — including via the
    // "Use latest" shortcut, which can grab a DIFFERENT project's newer backup
    // if the Default Restore Source folder is shared — must not leave a stale
    // schema selection sitting there for Start Restore to blindly reuse
    // against the new file.
    partial void OnBackupFileChanged(string value) => InvalidateAnalysis();

    private void InvalidateAnalysis()
    {
        _allRows = new();
        _allToc = Array.Empty<TocEntry>();
        Rows.Clear();
        BackupSchemas.Clear();
        NewCount = ExistingCount = MissingCount = SelectedCount = WillRestoreCount = 0;
        ChangesCount = 0; ChangesDetail = ""; ChangesBrush = Brushes.Gray;
        NewBreakdown = ExistingBreakdown = MissingBreakdown = "";
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

    // Directory-format backups (pg_dump -Fd) are a FOLDER, not a single file —
    // OpenFileDialog can't select one, so this is a separate picker.
    [RelayCommand]
    private void BrowseFolder()
    {
        var settings = _settingsStore.Load();
        var dlg = new OpenFolderDialog
        {
            Title = "Pick a directory-format PostgreSQL backup folder",
            InitialDirectory = Directory.Exists(settings.DefaultRestoreSource) ? settings.DefaultRestoreSource : null,
        };
        if (dlg.ShowDialog() == true) BackupFile = dlg.FolderName;
    }

    private static readonly string[] BackupFileExtensions = { ".dump", ".sql", ".tar", ".backup" };

    // Searches the whole Default Restore Source tree (including the Y/M/D
    // auto-folders backups get saved under) for the most recently written
    // backup — same extension set RetentionPolicy uses for single-file
    // backups, PLUS directory-format backups (folders containing pg_dump -Fd's
    // toc.dat marker), since a folder has no extension to match on.
    private string? FindLatestBackupFile()
    {
        var root = _settingsStore.Load().DefaultRestoreSource;
        if (!Directory.Exists(root)) return null;

        var fileCandidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => BackupFileExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Select(p => (Path: p, When: new FileInfo(p).LastWriteTime));

        var folderCandidates = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, "toc.dat")))
            .Select(d => (Path: d, When: new FileInfo(Path.Combine(d, "toc.dat")).LastWriteTime));

        return fileCandidates.Concat(folderCandidates)
            .OrderByDescending(c => c.When)
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    partial void OnUseLatestBackupChanged(bool value)
    {
        if (!value) return;
        var latest = FindLatestBackupFile();
        if (latest is null)
        {
            StatusText = "No backup files found under the Default Restore Source folder — check Settings.";
            return;
        }
        BackupFile = latest;
        StatusText = $"Using latest backup: {Path.GetFileName(latest)}";
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
        // Directory-format backups are a folder, not a single file — accept either.
        if (string.IsNullOrWhiteSpace(BackupFile) || (!File.Exists(BackupFile) && !Directory.Exists(BackupFile))) { StatusText = "Pick a valid backup file."; return; }
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

            NewBreakdown = BuildBreakdown(DiffStatus.NewInBackup);
            ExistingBreakdown = BuildBreakdown(DiffStatus.Existing);
            // MissingCount / MissingBreakdown are schema-scoped and set by
            // UpdateWillRestore() below, once BackupSchemas exists to scope against.

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
    // schemaFilter, when given, restricts the breakdown to those schemas only.
    private string BuildBreakdown(DiffStatus status, IReadOnlySet<string>? schemaFilter = null)
    {
        var rows = _allRows.Where(r => r.Status == status
            && (schemaFilter == null || schemaFilter.Contains(r.Schema))).ToList();
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

    // Full restore = every restorable object in the ticked schemas. Also
    // recomputes MISSING (scoped to just the ticked schemas — objects in
    // unrelated target schemas are noise, not something this restore touches)
    // and the CHANGES preview, since both depend on which schemas are ticked
    // and on the Drop-existing / Single-transaction options.
    private void UpdateWillRestore()
    {
        var included = BackupSchemas.Where(s => s.IsChecked)
            .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        WillRestoreCount = _allRows.Count(r => r.DumpId.HasValue && included.Contains(r.Schema));

        MissingCount = _allRows.Count(r => r.Status == DiffStatus.MissingFromBackup && included.Contains(r.Schema));
        MissingBreakdown = BuildBreakdown(DiffStatus.MissingFromBackup, included);

        var existingInScope = _allRows.Count(r => r.DumpId.HasValue && r.Status == DiffStatus.Existing && included.Contains(r.Schema));
        var newInScope = _allRows.Count(r => r.DumpId.HasValue && r.Status == DiffStatus.NewInBackup && included.Contains(r.Schema));

        if (existingInScope > 0 && SingleTransaction && !CleanFirst)
        {
            // Matches the pre-flight guard in StartRestoreAsync: this exact
            // combination is a guaranteed "already exists" failure that rolls
            // back the whole transaction, so nothing actually changes.
            ChangesCount = 0;
            ChangesDetail = "blocked — tick 'Drop existing first' or use an empty DB";
            ChangesBrush = Brushes.IndianRed;
        }
        else if (CleanFirst)
        {
            ChangesCount = newInScope + existingInScope;
            ChangesDetail = $"{existingInScope} replaced, {newInScope} created";
            ChangesBrush = Brushes.Goldenrod;
        }
        else
        {
            ChangesCount = newInScope;
            ChangesDetail = existingInScope > 0
                ? $"{newInScope} created · {existingInScope} already there, untouched"
                : $"{newInScope} created (fresh target)";
            ChangesBrush = Brushes.SeaGreen;
        }
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
            // Schema include filter applies to every row, MISSING-from-backup
            // included: a schema you've unticked (or one that's not part of
            // this backup at all) shouldn't clutter the view.
            if (hasSchemaFilter && !string.IsNullOrEmpty(r.Schema) && !included.Contains(r.Schema)) continue;

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
        if (string.IsNullOrWhiteSpace(BackupFile) || (!File.Exists(BackupFile) && !Directory.Exists(BackupFile))) { StatusText = "Pick a backup file."; return; }
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

        var settings = _settingsStore.Load();
        var tools = PgToolsLocator.Locate(settings.PgBinDirOverride);
        if (string.IsNullOrEmpty(tools.PgRestore)) { StatusText = "pg_restore.exe not found. Configure it in Settings."; return; }

        var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);

        // Pre-flight: existing objects + single-transaction + no drop = guaranteed failure.
        if (existingInScope > 0 && SingleTransaction && !CleanFirst)
        {
            ConfirmDialog.Alert(
                Application.Current?.MainWindow,
                "Restore would fail — target already has these objects",
                $"{existingInScope} object(s) already exist in '{SelectedProfile.Database}'.\n\n" +
                "A full restore re-creates every object, so pg_restore will hit \"already exists\" " +
                "errors and — because 'Single transaction' is ON — roll back the ENTIRE restore.\n\n" +
                "Do ONE of these first:\n" +
                "   • Tick 'Drop existing first (--clean --if-exists)', or\n" +
                "   • Restore into a fresh empty database (use 'Create Target DB' with a new name).");
            StatusText = $"{existingInScope} objects already exist — enable 'Drop existing first' or use an empty DB.";
            return;
        }

        // Pre-flight: pg_restore refuses --jobs together with --single-transaction —
        // this is a hard Postgres constraint, not something we can work around.
        if (ParallelJobs > 1 && SingleTransaction)
        {
            ConfirmDialog.Alert(
                Application.Current?.MainWindow,
                "Incompatible options",
                $"Parallel jobs ({ParallelJobs}) can't be combined with 'Single transaction' — " +
                "pg_restore itself refuses that combination.\n\n" +
                "Either set Parallel jobs back to 1, or uncheck 'Single transaction' " +
                "(you lose the all-or-nothing rollback safety net if you do).");
            StatusText = "Parallel jobs requires 'Single transaction' to be off.";
            return;
        }

        // Pre-flight: client tools newer than the target server can send
        // session-setup GUCs the server has never heard of — e.g. PostgreSQL 17+
        // pg_restore unconditionally sends "SET transaction_timeout = 0" during
        // its own connection init, which a PostgreSQL 15 server rejects outright
        // before a single object is touched. Caught here, before even asking
        // for confirmation, instead of failing two seconds into the real run.
        if (tools.MajorVersion.HasValue)
        {
            var serverMajor = await DatabaseAdmin.GetServerMajorVersionAsync(SelectedProfile, pwd);
            if (serverMajor.HasValue && tools.MajorVersion.Value > serverMajor.Value)
            {
                ConfirmDialog.Alert(
                    Application.Current?.MainWindow,
                    "Client tools are newer than the target server",
                    $"pg_restore {tools.Version} is newer than the target server (PostgreSQL {serverMajor}.x).\n\n" +
                    "Newer client tools can send session settings the server doesn't recognize " +
                    "(e.g. \"transaction_timeout\", added in PostgreSQL 17) and the restore fails immediately.\n\n" +
                    $"Fix: Settings → PostgreSQL Client Tools → Bin Folder Override → point at a PostgreSQL {serverMajor}.x install.");
                StatusText = $"Blocked — pg_restore {tools.Version} is newer than the server (PostgreSQL {serverMajor}.x).";
                return;
            }
        }

        // Safety confirmation — shows exactly WHERE this is going and that it is FULL.
        var confirmed = ConfirmDialog.Confirm(
            Application.Current?.MainWindow,
            isLocal ? "Confirm FULL restore" : "⚠ Confirm restore to REMOTE server",
            $"FULL restore of {ticked.Count} schema(s): {schemasInvolved}\n" +
            $"({objCount} objects + all their data, indexes, constraints & sequences)\n\n" +
            $"INTO target database:\n" +
            $"   {SelectedProfile.Database} @ {SelectedProfile.Host}:{SelectedProfile.Port}\n" +
            $"   (user: {SelectedProfile.Username})\n" +
            $"   {(isLocal ? "✓ LOCAL target — safe." : "⚠ REMOTE / network target!")}\n\n" +
            (CleanFirst ? "⚠ 'Drop existing first' is ON — existing objects will be DROPPED then recreated.\n\n" : "") +
            "Proceed?",
            confirmText: isLocal ? "Yes, restore" : "Yes, restore to remote",
            danger: CleanFirst || !isLocal);
        if (!confirmed) { StatusText = "Restore cancelled."; return; }

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
            Jobs = ParallelJobs,
            RestoreEntireFile = allTicked,
            IncludeSchemas = allTicked ? null : ticked,
        };

        LogLines.Clear();
        AppendLog($">> pg_restore · FULL restore · schemas: {schemasInvolved}");
        AppendLog($">> mode: {(allTicked ? "entire archive" : "--schema=" + string.Join(" --schema=", ticked))}");
        AppendLog($">> TARGET: {opts.Database} @ {opts.Host}:{opts.Port} (user {opts.Username})");
        AppendLog($">> options: single-tx={opts.SingleTransaction} clean={opts.CleanFirst} no-owner={opts.NoOwner} no-priv={opts.NoPrivileges} jobs={opts.Jobs}");

        _cts = new CancellationTokenSource();
        IsBusy = true; CanCancel = true;
        StatusText = "Running pg_restore...";

        // Progress is weighted by every TOC entry belonging to the ticked
        // schemas (tables, data, indexes, constraints, sequences, etc. each
        // count individually) — that's the real unit pg_restore advances
        // through one verbose line at a time, unlike a plain object count.
        var tickedSet = new HashSet<string>(ticked, StringComparer.OrdinalIgnoreCase);
        _totalEntriesInScope = _allToc.Count(e => tickedSet.Contains(e.Schema));
        _doneEntries = 0;
        ProgressPercent = 0; HasProgress = _totalEntriesInScope > 0; ElapsedText = "00:00"; EtaText = "";

        try
        {
            // pg_restore's --schema filter (used whenever this isn't a full-archive
            // restore) never selects the CREATE SCHEMA statement itself — only objects
            // belonging to that schema. Into a fresh target this schema wouldn't exist
            // yet, so the very first CREATE TABLE would fail with "schema does not
            // exist". Create any missing ones first — a no-op via IF NOT EXISTS when
            // they're already there.
            if (opts.IncludeSchemas is { Count: > 0 })
            {
                AppendLog(">> ensuring target schema(s) exist: " + string.Join(", ", opts.IncludeSchemas));
                var (schemasOk, schemaMsg) = await DatabaseAdmin.EnsureSchemasExistAsync(SelectedProfile, pwd, opts.IncludeSchemas, _cts.Token);
                if (!schemasOk)
                {
                    AppendLog($">> ERROR creating schema(s): {schemaMsg}");
                    StatusText = $"Could not create target schema(s): {schemaMsg}";
                    return;
                }
            }

            _stopwatch = Stopwatch.StartNew();
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += OnElapsedTick;
            _elapsedTimer.Start();

            var runner = new PgRestoreRunner();
            runner.Process.StdoutLine += OnLogLine;
            runner.Process.StderrLine += OnLogLine;
            runner.Process.StderrLine += OnRestoreProgressLine;

            var exit = await runner.RunAsync(tools.PgRestore!, opts, pwd, _cts.Token);

            runner.Process.StdoutLine -= OnLogLine;
            runner.Process.StderrLine -= OnLogLine;
            runner.Process.StderrLine -= OnRestoreProgressLine;

            _elapsedTimer.Stop();
            ElapsedText = FormatDuration(_stopwatch.Elapsed);

            if (exit == 0)
            {
                ProgressPercent = HasProgress ? 100 : 0;
                EtaText = HasProgress ? "done" : "";
                AppendLog($">> SUCCESS · took {ElapsedText}");
                var doneMsg = $"Restore completed into '{opts.Database}' @ {opts.Host}. (took {ElapsedText})";
                StatusText = doneMsg;

                // Re-diff against the target we just restored into, so the KPI
                // cards/table show what's REALLY there now, not stale pre-restore
                // counts — this is what let the archivo_spellbound-style no-op
                // "success" hide in plain sight before.
                AppendLog(">> re-analyzing target...");
                await AnalyzeAsync();
                StatusText = $"{doneMsg} {StatusText}";

                NotificationService.NotifyCompletion("Restore complete", doneMsg, success: true);
            }
            else
            {
                AppendLog($">> pg_restore exited with code {exit}");
                StatusText = $"pg_restore failed (exit {exit}). See log.";
                NotificationService.NotifyCompletion("Restore failed", StatusText, success: false);
            }
        }
        catch (OperationCanceledException) { AppendLog(">> Cancelled."); StatusText = "Cancelled."; }
        catch (Exception ex)
        {
            AppendLog($">> ERROR: {ex.Message}");
            StatusText = $"ERROR: {ex.Message}";
            NotificationService.NotifyCompletion("Restore error", StatusText, success: false);
        }
        finally
        {
            _elapsedTimer?.Stop();
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

    // Every "creating X" / "processing data for table X" / "executing SEQUENCE
    // SET X" verbose line is one TOC entry advancing — tally them against the
    // total entries in scope (computed once at restore start) for a live %.
    private void OnRestoreProgressLine(object? sender, string line)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.BeginInvoke(() => OnRestoreProgressLine(sender, line));
            return;
        }

        if (!RestoreProgressRx.IsMatch(line)) return;
        _doneEntries++;
        if (_totalEntriesInScope > 0)
            ProgressPercent = Math.Clamp((double)_doneEntries / _totalEntriesInScope * 100.0, 0, 100);
    }

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        if (_stopwatch is null) return;
        ElapsedText = FormatDuration(_stopwatch.Elapsed);

        if (!HasProgress) { EtaText = ""; return; }
        if (ProgressPercent < 1.0) { EtaText = "estimating..."; return; }

        var remainingFraction = (100.0 - ProgressPercent) / ProgressPercent;
        var eta = TimeSpan.FromSeconds(_stopwatch.Elapsed.TotalSeconds * remainingFraction);
        EtaText = $"~{FormatDuration(eta)} remaining";
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
