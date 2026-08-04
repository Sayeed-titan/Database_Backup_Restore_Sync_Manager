using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;
using PgBackupManager.UI.Views;

namespace PgBackupManager.UI.ViewModels;

public partial class SyncCheckItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    [ObservableProperty] private bool _isChecked = true;
}

public partial class SyncViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _sourceProfile;
    [ObservableProperty] private ConnectionProfile? _targetProfile;

    public ObservableCollection<string> SourceSchemas { get; } = new();
    public ObservableCollection<string> TargetSchemas { get; } = new();
    [ObservableProperty] private string? _sourceSchema;
    [ObservableProperty] private string? _targetSchema;

    public ObservableCollection<SyncCheckItem> AvailableTables { get; } = new();
    public ObservableCollection<SyncCheckItem> AvailableRoutines { get; } = new();

    [ObservableProperty] private bool _syncSchema = true;
    [ObservableProperty] private bool _syncData = true;
    // Index maps 1:1 onto DataSyncMode (FullRefresh=0, Upsert=1, Mirror=2) — see SyncView.xaml combo items.
    [ObservableProperty] private int _dataModeIndex = 1;
    [ObservableProperty] private bool _dryRun = true;

    [ObservableProperty] private string _statusText = "Pick a source and target profile, then Load Schemas.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string _elapsedText = "";
    private Stopwatch? _stopwatch;
    private DispatcherTimer? _elapsedTimer;

    public ObservableCollection<string> LogLines { get; } = new();

    public string RunButtonText => DryRun ? "Preview Plan" : "Run Sync";
    partial void OnDryRunChanged(bool value) => OnPropertyChanged(nameof(RunButtonText));

    public SyncViewModel()
    {
        ReloadProfiles();
    }

    public void ReloadProfiles()
    {
        var srcId = SourceProfile?.Id;
        var tgtId = TargetProfile?.Id;
        Profiles.Clear();
        foreach (var p in _profileStore.LoadAll().OrderBy(p => p.Name)) Profiles.Add(p);
        SourceProfile = srcId.HasValue ? Profiles.FirstOrDefault(p => p.Id == srcId) ?? Profiles.FirstOrDefault() : Profiles.FirstOrDefault();
        TargetProfile = tgtId.HasValue ? Profiles.FirstOrDefault(p => p.Id == tgtId) ?? Profiles.FirstOrDefault() : Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private async Task LoadSchemasAsync()
    {
        if (SourceProfile is null || TargetProfile is null) { StatusText = "Pick both a source and target profile first."; return; }
        try
        {
            IsBusy = true;
            StatusText = "Loading schemas...";

            var srcPwd = SecretProtector.Unprotect(SourceProfile.EncryptedPasswordBase64);
            var tgtPwd = SecretProtector.Unprotect(TargetProfile.EncryptedPasswordBase64);

            var srcTask = DbObjectInspector.InspectAsync(SourceProfile.BuildConnectionString(srcPwd));
            var tgtTask = DbObjectInspector.InspectAsync(TargetProfile.BuildConnectionString(tgtPwd));
            await Task.WhenAll(srcTask, tgtTask);

            var prevSrc = SourceSchema;
            var prevTgt = TargetSchema;
            SourceSchemas.Clear();
            foreach (var s in srcTask.Result.Where(o => o.Kind == DbObjectKind.Schema).Select(o => o.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                SourceSchemas.Add(s);
            TargetSchemas.Clear();
            foreach (var s in tgtTask.Result.Where(o => o.Kind == DbObjectKind.Schema).Select(o => o.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                TargetSchemas.Add(s);

            SourceSchema = SourceSchemas.Contains(prevSrc ?? "") ? prevSrc : SourceSchemas.FirstOrDefault();
            TargetSchema = TargetSchemas.Contains(prevTgt ?? "") ? prevTgt : (TargetSchemas.Contains(SourceSchema ?? "") ? SourceSchema : TargetSchemas.FirstOrDefault());

            StatusText = $"Loaded {SourceSchemas.Count} source / {TargetSchemas.Count} target schema(s).";
            await LoadObjectsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"ERROR loading schemas: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSourceSchemaChanged(string? value) => _ = LoadObjectsAsync();

    private async Task LoadObjectsAsync()
    {
        if (SourceProfile is null || string.IsNullOrEmpty(SourceSchema)) { AvailableTables.Clear(); AvailableRoutines.Clear(); return; }
        try
        {
            var pwd = SecretProtector.Unprotect(SourceProfile.EncryptedPasswordBase64);
            var objs = await DbObjectInspector.InspectAsync(SourceProfile.BuildConnectionString(pwd));

            var prevTables = AvailableTables.Where(t => !t.IsChecked).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var prevRoutines = AvailableRoutines.Where(t => !t.IsChecked).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            AvailableTables.Clear();
            foreach (var t in objs.Where(o => o.Kind == DbObjectKind.Table && string.Equals(o.Schema, SourceSchema, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                AvailableTables.Add(new SyncCheckItem { Name = t.Name, Label = t.Name, IsChecked = !prevTables.Contains(t.Name) });

            AvailableRoutines.Clear();
            foreach (var f in objs.Where(o => o.Kind == DbObjectKind.Function && string.Equals(o.Schema, SourceSchema, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                AvailableRoutines.Add(new SyncCheckItem { Name = f.DisplayName, Label = f.DisplayName, IsChecked = !prevRoutines.Contains(f.DisplayName) });
        }
        catch (Exception ex)
        {
            StatusText = $"ERROR loading tables/functions: {ex.Message}";
        }
    }

    [RelayCommand] private void SelectAllTables() { foreach (var t in AvailableTables) t.IsChecked = true; }
    [RelayCommand] private void SelectNoneTables() { foreach (var t in AvailableTables) t.IsChecked = false; }
    [RelayCommand] private void SelectAllRoutines() { foreach (var r in AvailableRoutines) r.IsChecked = true; }
    [RelayCommand] private void SelectNoneRoutines() { foreach (var r in AvailableRoutines) r.IsChecked = false; }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (SourceProfile is null || TargetProfile is null) { StatusText = "Pick both a source and target profile."; return; }
        if (string.IsNullOrWhiteSpace(SourceSchema) || string.IsNullOrWhiteSpace(TargetSchema)) { StatusText = "Pick a source and target schema."; return; }

        var tables = AvailableTables.Where(t => t.IsChecked).Select(t => t.Name).ToList();
        var routines = AvailableRoutines.Where(r => r.IsChecked).Select(r => r.Name).ToList();

        if (!SyncSchema && !SyncData) { StatusText = "Pick Schema, Data, or both."; return; }
        if (tables.Count == 0 && routines.Count == 0) { StatusText = "Select at least one table or function/procedure."; return; }
        if (SyncData && tables.Count == 0) { StatusText = "Data sync needs at least one table selected."; return; }

        var dataMode = (DataSyncMode)DataModeIndex;

        var srcPwd = SecretProtector.Unprotect(SourceProfile.EncryptedPasswordBase64);
        var tgtPwd = SecretProtector.Unprotect(TargetProfile.EncryptedPasswordBase64);

        var settings = _settingsStore.Load();
        var tools = PgToolsLocator.Locate(settings.PgBinDirOverride);

        // Schema-sync (new tables) and Full Refresh data both shell out to
        // pg_dump against the SOURCE server, so the same client/server
        // version guard used by Backup/Restore/Copy Schema applies here too.
        var needsPgTools = SyncSchema || dataMode == DataSyncMode.FullRefresh;
        if (needsPgTools && tools.MajorVersion.HasValue)
        {
            var serverMajor = await DatabaseAdmin.GetServerMajorVersionAsync(SourceProfile, srcPwd);
            if (serverMajor.HasValue && tools.MajorVersion.Value > serverMajor.Value)
            {
                var switched = ConfirmDialog.ShowVersionMismatch(
                    Application.Current?.MainWindow, "pg_dump", tools.Version ?? "?", serverMajor.Value, settings.PgBinDirOverride);
                StatusText = switched
                    ? "Switched PostgreSQL client tools — click again to retry."
                    : $"Blocked — pg_dump {tools.Version} is newer than the source server (PostgreSQL {serverMajor}.x).";
                return;
            }
        }

        if (!DryRun)
        {
            var what = new List<string>();
            if (SyncSchema) what.Add($"structure for {tables.Count} table(s) + {routines.Count} function/procedure(s)");
            if (SyncData) what.Add($"{dataMode} data for {tables.Count} table(s)");

            var danger = dataMode == DataSyncMode.Mirror || dataMode == DataSyncMode.FullRefresh;
            var extraWarning = dataMode == DataSyncMode.Mirror
                ? "\n\nMIRROR will DELETE target rows that don't exist in the source."
                : dataMode == DataSyncMode.FullRefresh && SyncData
                    ? "\n\nFULL REFRESH will TRUNCATE each selected table on the target before reloading it."
                    : "";

            var confirmed = ConfirmDialog.Confirm(
                Application.Current?.MainWindow,
                "Confirm sync",
                $"Sync {string.Join(" and ", what)}\n" +
                $"FROM '{SourceProfile.Database}'.{SourceSchema} @ {SourceProfile.Host}:{SourceProfile.Port}\n" +
                $"TO   '{TargetProfile.Database}'.{TargetSchema} @ {TargetProfile.Host}:{TargetProfile.Port}" +
                extraWarning + "\n\nProceed?",
                confirmText: "Yes, sync",
                danger: danger);
            if (!confirmed) { StatusText = "Cancelled."; return; }
        }

        LogLines.Clear();
        _cts = new CancellationTokenSource();
        IsBusy = true; CanCancel = true;
        StatusText = DryRun ? "Building preview..." : "Syncing...";
        ElapsedText = "00:00";
        _stopwatch = Stopwatch.StartNew();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = FormatDuration(_stopwatch.Elapsed);
        _elapsedTimer.Start();

        try
        {
            var runner = new SchemaSyncRunner();
            runner.LogLine += (_, line) => AppendLog(line);

            var opts = new SyncOptions
            {
                SourceProfile = SourceProfile,
                SourcePassword = srcPwd,
                TargetProfile = TargetProfile,
                TargetPassword = tgtPwd,
                SourceSchema = SourceSchema!,
                TargetSchema = TargetSchema!,
                SyncSchema = SyncSchema,
                SyncData = SyncData,
                DataMode = dataMode,
                Tables = tables,
                RoutineSignatures = routines,
                DryRun = DryRun,
                PgDumpExe = tools.PgDump,
                PsqlExe = tools.Psql,
            };

            var result = await runner.RunAsync(opts, _cts.Token);

            _elapsedTimer.Stop();
            ElapsedText = FormatDuration(_stopwatch.Elapsed);

            if (result.Ok)
            {
                StatusText = $"{result.Summary} (took {ElapsedText})";
                if (!DryRun) NotificationService.NotifyCompletion("Sync complete", StatusText, success: true);
            }
            else
            {
                StatusText = $"Sync failed: {result.Summary}";
                NotificationService.NotifyCompletion("Sync failed", StatusText, success: false);
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
            NotificationService.NotifyCompletion("Sync error", StatusText, success: false);
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
