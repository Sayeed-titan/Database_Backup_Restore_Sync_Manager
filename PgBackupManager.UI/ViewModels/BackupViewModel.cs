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
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;

namespace PgBackupManager.UI.ViewModels;

public partial class SchemaNode : ObservableObject
{
    public string Name { get; init; } = "";
    // Objects are organised into folders (Tables, Views, Functions, Sequences, Types).
    public ObservableCollection<ObjectGroup> Groups { get; } = new();
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isExpanded;
    public bool HasObjects => Groups.Any(g => g.AllCount > 0);
    public int TableCount { get; set; }
    public int FunctionCount { get; set; }
    public int ViewCount { get; set; }
    public int TypeCount { get; set; }
    public int SequenceCount { get; set; }
    public string Summary => $"{TableCount} tables · {FunctionCount} fns · {ViewCount} views · {TypeCount} types · {SequenceCount} seqs";

    public void RefreshSummary() { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(HasObjects)); }

    // True while a schema-level tick is cascading down to its tables, so the
    // view-model can tell a cascade apart from a manual single-table click.
    public bool Cascading { get; private set; }

    // Ticking the schema selects its tables (the only object kind pg_dump can
    // target individually); functions/views/types/sequences ride along with the
    // schema automatically when the schema is dumped.
    partial void OnIsCheckedChanged(bool value)
    {
        Cascading = true;
        foreach (var g in Groups)
            if (g.IsTableGroup)
                foreach (var o in g.AllObjects) o.IsChecked = value;
        Cascading = false;
    }
}

public partial class ObjectGroup : ObservableObject
{
    public string Title { get; init; } = "";       // "Tables", "Functions", …
    public DbObjectKind Kind { get; init; }
    public bool IsTableGroup => Kind == DbObjectKind.Table;
    public List<ObjectNode> AllObjects { get; } = new();          // full set
    public ObservableCollection<ObjectNode> Objects { get; } = new(); // filtered view
    [ObservableProperty] private bool _isExpanded;
    public int AllCount => AllObjects.Count;
    public string Header => $"{Title} ({AllObjects.Count})";
    public bool HasVisible => Objects.Count > 0;

    public void ShowAll()
    {
        Objects.Clear();
        foreach (var o in AllObjects) Objects.Add(o);
        OnPropertyChanged(nameof(HasVisible));
    }

    public void ShowOnly(IEnumerable<ObjectNode> items)
    {
        Objects.Clear();
        foreach (var o in items) Objects.Add(o);
        OnPropertyChanged(nameof(HasVisible));
    }
}

public partial class ObjectNode : ObservableObject
{
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public DbObjectKind Kind { get; init; }
    public string FullName => $"{Schema}.{Name}";
    public bool IsTable => Kind == DbObjectKind.Table;

    public string KindLabel => Kind switch
    {
        DbObjectKind.Table => "TABLE",
        DbObjectKind.View => "VIEW",
        DbObjectKind.Function => "FUNCTION",
        DbObjectKind.Procedure => "PROC",
        DbObjectKind.Type => "TYPE",
        DbObjectKind.Sequence => "SEQ",
        _ => Kind.ToString().ToUpperInvariant()
    };

    [ObservableProperty] private bool _isChecked;
}

public partial class BackupViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _selectedProfile;

    public ObservableCollection<SchemaNode> Schemas { get; } = new();
    private List<SchemaNode> _allSchemas = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Select a profile and click 'Load DB objects'.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string _selectedSummary = "0 selected";

    // Elapsed / ETA while a backup is running. ETA is weighted by on-disk table
    // bytes (fetched once, up front) rather than table count, since a handful of
    // huge tables next to many empty ones makes table-count a poor time proxy.
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _hasProgress;
    private Stopwatch? _stopwatch;
    private DispatcherTimer? _elapsedTimer;
    private IReadOnlyDictionary<string, long> _tableBytes = new Dictionary<string, long>();
    private long _totalBytes;
    private long _doneBytes;
    // Tables already credited — a set rather than "the one current table"
    // because --jobs > 1 dumps several tables concurrently, so there is no
    // single "in progress" table to track sequentially.
    private readonly HashSet<string> _tablesCredited = new(StringComparer.Ordinal);
    private static readonly Regex DumpingTableRx = new(@"dumping contents of table ""(.+)""", RegexOptions.Compiled);

    // pg_dump --jobs=N. Only valid with Directory format — enforced as a
    // pre-flight check in StartBackupAsync, not silently overridden here.
    [ObservableProperty] private int _parallelJobs = 1;

    // When Scope == "Selected schemas" and more than one is ticked, run one
    // pg_dump per schema (its own file) instead of combining them into a
    // single archive — all files still land in the same destination folder.
    [ObservableProperty] private bool _separateFilePerSchema;

    public List<string> Formats { get; } = new() { "Custom (.dump)", "Plain SQL (.sql)", "Tar (.tar)", "Directory (parallel)" };
    [ObservableProperty] private string _selectedFormat = "Custom (.dump)";

    public List<string> Scopes { get; } = new() { "Full database", "Selected schemas", "Selected tables" };
    [ObservableProperty] private string _selectedScope = "Full database";

    public List<string> Contents { get; } = new() { "Schema + Data", "Schema only", "Data only" };
    [ObservableProperty] private string _selectedContent = "Schema + Data";

    [ObservableProperty] private string _destinationRoot = "";
    [ObservableProperty] private bool _useAutoFolders = true;
    [ObservableProperty] private string _filenamePreview = "";
    [ObservableProperty] private string _fullPathPreview = "";

    public ObservableCollection<string> LogLines { get; } = new();

    public BackupViewModel()
    {
        var settings = _settingsStore.Load();
        DestinationRoot = settings.DefaultBackupRoot;
        UseAutoFolders = settings.UseAutoFolders;
        ReloadProfiles();
    }

    public void ReloadProfiles()
    {
        var currentId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var p in _profileStore.LoadAll().OrderBy(p => p.Name))
            Profiles.Add(p);
        SelectedProfile = currentId.HasValue
            ? Profiles.FirstOrDefault(p => p.Id == currentId) ?? Profiles.FirstOrDefault()
            : Profiles.FirstOrDefault();
        UpdateFilenamePreview();
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value) => UpdateFilenamePreview();
    partial void OnSelectedFormatChanged(string value) => UpdateFilenamePreview();
    partial void OnSelectedScopeChanged(string value) => UpdateFilenamePreview();
    partial void OnSelectedContentChanged(string value) => UpdateFilenamePreview();
    partial void OnDestinationRootChanged(string value) => UpdateFilenamePreview();
    partial void OnUseAutoFoldersChanged(bool value) => UpdateFilenamePreview();
    partial void OnSeparateFilePerSchemaChanged(bool value) => UpdateFilenamePreview();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void UpdateFilenamePreview()
    {
        if (SelectedProfile is null) { FilenamePreview = ""; FullPathPreview = ""; return; }
        var job = BuildJob();
        var now = DateTime.Now;

        var tickedSchemas = _allSchemas.Where(s => s.IsChecked).Select(s => s.Name).ToList();
        if (job.SeparateFilePerSchema && job.Scope == BackupScope.SpecificSchemas && tickedSchemas.Count > 1)
        {
            var folder = FilenameBuilder.BuildFolder(job.DestinationRoot, job.Database, job.UseAutoFolders, now);
            var ext = string.IsNullOrEmpty(job.Extension) ? "" : "." + job.Extension;
            FilenamePreview = $"{tickedSchemas.Count} separate files";
            FullPathPreview = $@"{folder}\{job.Database}_<schema>_{now:yyyyMMdd_HHmmss}{ext}  ×{tickedSchemas.Count}";
            return;
        }

        FilenamePreview = FilenameBuilder.BuildFileName(job, now);
        FullPathPreview = FilenameBuilder.BuildFullPath(job, now);
    }

    private BackupJob BuildJob() => new()
    {
        Host = SelectedProfile?.Host ?? "",
        Port = SelectedProfile?.Port ?? 5432,
        Database = SelectedProfile?.Database ?? "",
        Username = SelectedProfile?.Username ?? "",
        Format = SelectedFormat switch
        {
            "Plain SQL (.sql)" => BackupFormat.Plain,
            "Tar (.tar)" => BackupFormat.Tar,
            "Directory (parallel)" => BackupFormat.Directory,
            _ => BackupFormat.Custom
        },
        Scope = SelectedScope switch
        {
            "Selected schemas" => BackupScope.SpecificSchemas,
            "Selected tables" => BackupScope.SpecificTables,
            _ => BackupScope.FullDatabase
        },
        Content = SelectedContent switch
        {
            "Schema only" => DumpContent.SchemaOnly,
            "Data only" => DumpContent.DataOnly,
            _ => DumpContent.Both
        },
        DestinationRoot = DestinationRoot,
        UseAutoFolders = UseAutoFolders,
        Jobs = ParallelJobs,
        SeparateFilePerSchema = SeparateFilePerSchema,
    };

    private void ApplyFilter()
    {
        var term = SearchText?.Trim() ?? "";
        Schemas.Clear();
        foreach (var s in _allSchemas)
        {
            if (string.IsNullOrEmpty(term))
            {
                // No filter: show every folder in full, collapsed to defaults.
                foreach (var g in s.Groups) { g.ShowAll(); g.IsExpanded = false; }
                s.IsExpanded = false;
                Schemas.Add(s);
                continue;
            }

            var matchSchema = s.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
            var anyMatch = false;
            foreach (var g in s.Groups)
            {
                if (matchSchema)
                {
                    g.ShowAll();
                }
                else
                {
                    var hits = g.AllObjects.Where(o => o.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
                    g.ShowOnly(hits);
                }
                g.IsExpanded = g.HasVisible;          // auto-open folders with hits
                if (g.HasVisible) anyMatch = true;
            }
            if (matchSchema || anyMatch)
            {
                s.IsExpanded = true;                  // auto-open schemas with hits
                Schemas.Add(s);
            }
        }
    }

    private void SchemaNode_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SchemaNode.IsChecked)) return;
        UpdateSelectedSummary();
        // Ticking a schema means "back up whole schemas".
        if (sender is SchemaNode { IsChecked: true }) SelectedScope = "Selected schemas";
        UpdateFilenamePreview();
    }

    // A manual single-table tick (not part of a schema cascade) means
    // "back up selected tables" — flip the scope so the dump targets tables.
    private void ObjectNode_PropertyChanged(SchemaNode schema, ObjectNode node, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ObjectNode.IsChecked)) return;
        if (node.IsChecked && !schema.Cascading) SelectedScope = "Selected tables";
    }

    private void UpdateSelectedSummary()
    {
        var count = _allSchemas.Count(s => s.IsChecked);
        SelectedSummary = $"{count} selected";
    }

    [RelayCommand]
    private async Task LoadObjectsAsync()
    {
        if (SelectedProfile is null) { StatusText = "Pick a profile first."; return; }
        try
        {
            IsBusy = true;
            StatusText = "Connecting and querying object catalog...";
            var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);
            var connStr = SelectedProfile.BuildConnectionString(pwd);
            var objects = await DbObjectInspector.InspectAsync(connStr);

            _allSchemas = objects
                .Where(o => o.Kind == DbObjectKind.Schema)
                .Select(s => new SchemaNode { Name = s.Name })
                .ToList();
            var schemaMap = _allSchemas.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

            // Bucket objects per schema per kind so we can build folder groups.
            var buckets = _allSchemas.ToDictionary(
                s => s.Name,
                _ => new Dictionary<DbObjectKind, List<ObjectNode>>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var o in objects.Where(o => o.Kind != DbObjectKind.Schema))
            {
                if (!schemaMap.TryGetValue(o.Schema, out var node)) continue;
                // Procedures are reported as functions by the catalog query → one bucket.
                var kind = o.Kind == DbObjectKind.Procedure ? DbObjectKind.Function : o.Kind;
                var byKind = buckets[node.Name];
                if (!byKind.TryGetValue(kind, out var listForKind)) { listForKind = new(); byKind[kind] = listForKind; }
                listForKind.Add(new ObjectNode { Schema = o.Schema, Name = o.Name, Kind = kind });
                switch (kind)
                {
                    case DbObjectKind.Table: node.TableCount++; break;
                    case DbObjectKind.Function: node.FunctionCount++; break;
                    case DbObjectKind.View: node.ViewCount++; break;
                    case DbObjectKind.Type: node.TypeCount++; break;
                    case DbObjectKind.Sequence: node.SequenceCount++; break;
                }
            }

            // Folder order: Tables, Views, Functions, Sequences, Types.
            (DbObjectKind Kind, string Title)[] folderOrder =
            {
                (DbObjectKind.Table, "Tables"),
                (DbObjectKind.View, "Views"),
                (DbObjectKind.Function, "Functions"),
                (DbObjectKind.Sequence, "Sequences"),
                (DbObjectKind.Type, "Types"),
            };

            foreach (var s in _allSchemas)
            {
                var byKind = buckets[s.Name];
                foreach (var (kind, title) in folderOrder)
                {
                    if (!byKind.TryGetValue(kind, out var items) || items.Count == 0) continue;
                    var group = new ObjectGroup { Title = title, Kind = kind };
                    foreach (var o in items.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        group.AllObjects.Add(o);
                        group.Objects.Add(o);
                        // Tables drive "Selected tables" scope when picked individually.
                        if (o.IsTable) o.PropertyChanged += (snd, ev) => ObjectNode_PropertyChanged(s, (ObjectNode)snd!, ev);
                    }
                    s.Groups.Add(group);
                }
                s.RefreshSummary();
                s.PropertyChanged += SchemaNode_PropertyChanged;
            }

            ApplyFilter();
            UpdateSelectedSummary();
            var totalTables = _allSchemas.Sum(s => s.TableCount);
            var totalFns = _allSchemas.Sum(s => s.FunctionCount);
            StatusText = $"Loaded {_allSchemas.Count} schemas · {totalTables} tables · {totalFns} functions.";
        }
        catch (Exception ex)
        {
            StatusText = $"ERROR loading objects: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dlg = new OpenFolderDialog { Title = "Select backup destination root" };
        if (!string.IsNullOrEmpty(DestinationRoot) && Directory.Exists(DestinationRoot))
            dlg.InitialDirectory = DestinationRoot;
        if (dlg.ShowDialog() == true) DestinationRoot = dlg.FolderName;
    }

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (SelectedProfile is null) { StatusText = "Pick a profile first."; return; }
        if (string.IsNullOrWhiteSpace(DestinationRoot)) { StatusText = "Pick a destination root folder."; return; }

        // pg_dump refuses --jobs with any format other than Directory — only
        // that format lays tables out as separate files workers can write to
        // concurrently, so this is a hard restriction, not a UI preference.
        if (ParallelJobs > 1 && SelectedFormat != "Directory (parallel)")
        {
            StatusText = "Parallel jobs requires the 'Directory (parallel)' format — pg_dump won't run parallel workers with any other format.";
            return;
        }

        var settings = _settingsStore.Load();
        var tools = PgToolsLocator.Locate(settings.PgBinDirOverride);
        if (string.IsNullOrEmpty(tools.PgDump))
        {
            StatusText = "pg_dump.exe not found. Configure it in Settings.";
            return;
        }

        var job = BuildJob();

        if (job.Scope == BackupScope.SpecificSchemas)
        {
            job.IncludeSchemas = _allSchemas.Where(s => s.IsChecked).Select(s => s.Name).ToList();
            if (job.IncludeSchemas.Count == 0) { StatusText = "Tick at least one schema."; return; }
        }
        else if (job.Scope == BackupScope.SpecificTables)
        {
            job.IncludeTables = _allSchemas
                .SelectMany(s => s.Groups).Where(g => g.IsTableGroup)
                .SelectMany(g => g.AllObjects)
                .Where(o => o.IsChecked).Select(o => o.FullName).ToList();
            if (job.IncludeTables.Count == 0) { StatusText = "Tick at least one table."; return; }
        }

        var now = DateTime.Now;

        // Only meaningful for a multi-schema dump — one schema ticked behaves
        // exactly like today (single combined file), no need to branch for it.
        var runSeparate = job.SeparateFilePerSchema && job.Scope == BackupScope.SpecificSchemas && job.IncludeSchemas.Count > 1;

        LogLines.Clear();
        if (runSeparate)
        {
            var folder = FilenameBuilder.BuildFolder(job.DestinationRoot, job.Database, job.UseAutoFolders, now);
            AppendLog($">> pg_dump × {job.IncludeSchemas.Count} (one file per schema) → {folder}");
            AppendLog($">> args: --host={job.Host} --port={job.Port} --dbname={job.Database} --format={job.FormatChar} content={job.Content}");
        }
        else
        {
            job.FullOutputPath = FilenameBuilder.BuildFullPath(job, now);
            AppendLog($">> pg_dump → {job.FullOutputPath}");
            AppendLog($">> args: --host={job.Host} --port={job.Port} --dbname={job.Database} --format={job.FormatChar} content={job.Content} scope={job.Scope}");
        }

        _cts = new CancellationTokenSource();
        IsBusy = true; CanCancel = true;
        StatusText = "Running pg_dump...";

        // Reset progress state from any previous run before this one starts.
        // Not reset again between per-schema sub-runs below — bytes are keyed
        // "schema.table" (see TableSizeEstimator), so totals accumulate safely
        // across every sub-run into one running percentage for the whole job.
        _tableBytes = new Dictionary<string, long>();
        _totalBytes = 0; _doneBytes = 0; _tablesCredited.Clear();
        ProgressPercent = 0; HasProgress = false; ElapsedText = "00:00"; EtaText = "";

        try
        {
            var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);

            // Best-effort size estimate for the ETA — if this fails (network hiccup,
            // no privilege, whatever) the backup still proceeds, just without an ETA.
            // One query covers every schema in scope regardless of runSeparate, since
            // job.IncludeSchemas already lists all of them.
            try
            {
                var connStr = SelectedProfile.BuildConnectionString(pwd);
                var estimate = await TableSizeEstimator.EstimateAsync(connStr, job, _cts.Token);
                _tableBytes = estimate.BytesByTable;
                _totalBytes = estimate.TotalBytes;
                HasProgress = _totalBytes > 0;
            }
            catch { /* no ETA this run — not fatal */ }

            _stopwatch = Stopwatch.StartNew();
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += OnElapsedTick;
            _elapsedTimer.Start();

            var (exitCode, outputPaths) = runSeparate
                ? await RunSeparateSchemaDumpsAsync(job, tools.PgDump!, pwd, now, _cts.Token)
                : (await RunOnePgDumpAsync(job, tools.PgDump!, pwd, _cts.Token), new List<string> { job.FullOutputPath });

            _elapsedTimer.Stop();
            ElapsedText = FormatDuration(_stopwatch.Elapsed);

            if (exitCode == 0)
            {
                _doneBytes = _totalBytes;
                ProgressPercent = HasProgress ? 100 : 0;
                EtaText = HasProgress ? "done" : "";
                var size = outputPaths.Sum(p => SizeOfOutput(p, job.Format));
                AppendLog($">> SUCCESS · {outputPaths.Count} file(s) · size {size / 1024.0:F1} KB · took {ElapsedText}");
                StatusText = runSeparate
                    ? $"Backup OK: {outputPaths.Count} files (one per schema) (took {ElapsedText})"
                    : $"Backup OK: {Path.GetFileName(job.FullOutputPath)} (took {ElapsedText})";
                NotificationService.NotifyCompletion("Backup complete", StatusText, success: true);
            }
            else
            {
                AppendLog($">> pg_dump exited with code {exitCode}");
                StatusText = $"pg_dump failed (exit {exitCode}). See log.";
                NotificationService.NotifyCompletion("Backup failed", StatusText, success: false);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog(">> Cancelled by user.");
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($">> ERROR: {ex.Message}");
            StatusText = $"ERROR: {ex.Message}";
            NotificationService.NotifyCompletion("Backup error", StatusText, success: false);
        }
        finally
        {
            _elapsedTimer?.Stop();
            IsBusy = false; CanCancel = false;
            _cts?.Dispose(); _cts = null;
        }
    }

    private async Task<int> RunOnePgDumpAsync(BackupJob job, string pgDumpExe, string pwd, CancellationToken ct)
    {
        var runner = new PgDumpRunner();
        runner.Process.StdoutLine += OnLogLine;
        runner.Process.StderrLine += OnLogLine;
        runner.Process.StderrLine += OnDumpProgressLine;
        try
        {
            return await runner.RunAsync(pgDumpExe, job, pwd, ct);
        }
        finally
        {
            runner.Process.StdoutLine -= OnLogLine;
            runner.Process.StderrLine -= OnLogLine;
            runner.Process.StderrLine -= OnDumpProgressLine;
        }
    }

    // Runs one pg_dump per ticked schema, each producing its own file in the
    // same destination folder (same day-folder, same timestamp). Stops at the
    // first failure so a broken schema doesn't get lost in a "SUCCESS" line —
    // whatever files were already produced by earlier schemas stay on disk.
    private async Task<(int ExitCode, List<string> OutputPaths)> RunSeparateSchemaDumpsAsync(
        BackupJob job, string pgDumpExe, string pwd, DateTime now, CancellationToken ct)
    {
        var outputs = new List<string>();
        for (int i = 0; i < job.IncludeSchemas.Count; i++)
        {
            var schema = job.IncludeSchemas[i];
            var subJob = new BackupJob
            {
                Host = job.Host,
                Port = job.Port,
                Database = job.Database,
                Username = job.Username,
                Format = job.Format,
                Scope = BackupScope.SpecificSchemas,
                Content = job.Content,
                IncludeSchemas = new List<string> { schema },
                DestinationRoot = job.DestinationRoot,
                UseAutoFolders = job.UseAutoFolders,
                Jobs = job.Jobs,
                FullOutputPath = FilenameBuilder.BuildFullPath(job, now, schema),
            };

            AppendLog($">> [{i + 1}/{job.IncludeSchemas.Count}] schema '{schema}' → {subJob.FullOutputPath}");
            var exit = await RunOnePgDumpAsync(subJob, pgDumpExe, pwd, ct);
            outputs.Add(subJob.FullOutputPath);
            if (exit != 0)
            {
                AppendLog($">> schema '{schema}' FAILED (exit {exit}) — stopping, {i} of {job.IncludeSchemas.Count} completed.");
                return (exit, outputs);
            }
        }
        return (0, outputs);
    }

    // Directory format writes a folder (one file per table plus a TOC), not a
    // single file — FileInfo can't size that.
    private static long SizeOfOutput(string path, BackupFormat format) => format == BackupFormat.Directory
        ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
        : new FileInfo(path).Length;

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

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

    // pg_dump's verbose stderr announces "dumping contents of table X" as each
    // table starts. Credit the table's bytes as soon as it starts rather than
    // waiting for the next table's line: with --jobs > 1 several tables dump
    // concurrently, so there is no single "previous" table to wait on, and
    // crediting on start (vs. confirmed completion) is the simplification that
    // works the same way whether jobs=1 or jobs=N.
    private void OnDumpProgressLine(object? sender, string line)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.BeginInvoke(() => OnDumpProgressLine(sender, line));
            return;
        }

        var m = DumpingTableRx.Match(line);
        if (!m.Success) return;

        var table = m.Groups[1].Value;
        if (_tablesCredited.Add(table) && _tableBytes.TryGetValue(table, out var bytes))
            _doneBytes += bytes;

        if (_totalBytes > 0)
            ProgressPercent = Math.Clamp((double)_doneBytes / _totalBytes * 100.0, 0, 100);
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
