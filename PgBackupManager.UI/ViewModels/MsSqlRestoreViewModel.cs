using System;
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

// SQL Server counterpart to RestoreViewModel — no TOC/object diff (BACKUP
// DATABASE has no per-object granularity), so "Analyze" just reads the
// backup's own header and checks whether the target database name is taken.
public partial class MsSqlRestoreViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _selectedProfile;

    [ObservableProperty] private string _backupFile = "";
    [ObservableProperty] private string _statusText = "Pick a .bak file and a target SQL Server profile, then Analyze.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _isAnalyzed;

    [ObservableProperty] private string _restoreTargetText = "(no profile selected)";
    [ObservableProperty] private Brush _restoreTargetBrush = Brushes.Gray;
    [ObservableProperty] private string _restoreTargetHint = "";

    [ObservableProperty] private string _targetDatabase = "";
    [ObservableProperty] private bool _overwriteExisting;
    [ObservableProperty] private bool _targetExists;

    [ObservableProperty] private string _originalDatabaseName = "";
    [ObservableProperty] private DateTime? _backupFinishDate;
    [ObservableProperty] private int _fileCount;

    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _hasProgress;
    private Stopwatch? _stopwatch;
    private DispatcherTimer? _elapsedTimer;
    private static readonly Regex PercentRx = new(@"(\d+) percent processed", RegexOptions.Compiled);

    public ObservableCollection<string> LogLines { get; } = new();

    public MsSqlRestoreViewModel()
    {
        ReloadProfiles();
        UpdateTargetDisplay();
    }

    public void ReloadProfiles()
    {
        var currentId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var p in _profileStore.LoadAll().Where(p => p.Engine == DbEngine.SqlServer).OrderBy(p => p.Name))
            Profiles.Add(p);
        SelectedProfile = currentId.HasValue
            ? Profiles.FirstOrDefault(p => p.Id == currentId) ?? Profiles.FirstOrDefault()
            : Profiles.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        UpdateTargetDisplay();
        InvalidateAnalysis();
    }

    partial void OnBackupFileChanged(string value) => InvalidateAnalysis();

    partial void OnTargetDatabaseChanged(string value) => _ = RefreshTargetExistsAsync();
    partial void OnOverwriteExistingChanged(bool value) { }

    private void InvalidateAnalysis()
    {
        IsAnalyzed = false;
        OriginalDatabaseName = ""; BackupFinishDate = null; FileCount = 0;
        TargetDatabase = ""; TargetExists = false; OverwriteExisting = false;
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
        RestoreTargetText = $"{p.Host}{(p.Port is > 0 and not 1433 ? $",{p.Port}" : "")}   ({(p.SqlIntegratedSecurity ? "Windows Auth" : $"user: {p.Username}")})";

        var isLocal = IsLocalHost(p.Host);
        if (isLocal)
        {
            RestoreTargetBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x7A, 0x3D));
            RestoreTargetHint = "Local target — safe to restore.";
        }
        else
        {
            RestoreTargetBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x50, 0x00));
            RestoreTargetHint = "REMOTE target — BACKUP/RESTORE paths are resolved by the SQL Server engine itself, " +
                                 "not this app. The .bak path must be reachable from THAT server (e.g. a UNC share), not just this PC.";
        }
    }

    private static bool IsLocalHost(string host) =>
        host.Trim().ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or ".";

    [RelayCommand]
    private void BrowseFile()
    {
        var settings = _settingsStore.Load();
        var dlg = new OpenFileDialog
        {
            Title = "Pick a SQL Server backup file",
            Filter = "SQL Server backup (*.bak)|*.bak|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(settings.DefaultRestoreSource) ? settings.DefaultRestoreSource : null,
        };
        if (dlg.ShowDialog() == true) BackupFile = dlg.FileName;
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(BackupFile) || !File.Exists(BackupFile)) { StatusText = "Pick a valid .bak file."; return; }
        if (SelectedProfile is null) { StatusText = "Pick a target profile."; return; }

        IsBusy = true;
        try
        {
            StatusText = "Reading backup header...";
            var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);
            var header = await MsSqlRestoreRunner.InspectAsync(SelectedProfile, pwd, BackupFile);

            OriginalDatabaseName = header.DatabaseName;
            BackupFinishDate = header.BackupFinishDate;
            FileCount = header.Files.Count;
            TargetDatabase = header.DatabaseName;

            await RefreshTargetExistsAsync();
            IsAnalyzed = true;
            StatusText = $"Backup of '{header.DatabaseName}' from {header.BackupFinishDate:yyyy-MM-dd HH:mm} · {header.Files.Count} file(s). " +
                         (TargetExists ? $"'{TargetDatabase}' already exists on the target — tick 'Overwrite existing' to replace it."
                                       : $"'{TargetDatabase}' does not exist yet — will be created fresh.");
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

    private async Task RefreshTargetExistsAsync()
    {
        if (SelectedProfile is null || string.IsNullOrWhiteSpace(TargetDatabase)) { TargetExists = false; return; }
        try
        {
            var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);
            TargetExists = await MsSqlAdmin.DatabaseExistsAsync(SelectedProfile, pwd, TargetDatabase);
        }
        catch { TargetExists = false; }
    }

    [RelayCommand]
    private async Task StartRestoreAsync()
    {
        if (!IsAnalyzed) { StatusText = "Run Analyze first."; return; }
        if (SelectedProfile is null) { StatusText = "Pick a target profile."; return; }
        if (string.IsNullOrWhiteSpace(TargetDatabase)) { StatusText = "Type a target database name."; return; }

        if (TargetExists && !OverwriteExisting)
        {
            ConfirmDialog.Alert(
                Application.Current?.MainWindow,
                "Database already exists",
                $"'{TargetDatabase}' already exists on {SelectedProfile.Host}.\n\n" +
                "Tick 'Overwrite existing' to replace it (WITH REPLACE), or change the target database name to restore alongside it.");
            StatusText = $"'{TargetDatabase}' already exists — tick 'Overwrite existing' or pick a different name.";
            return;
        }

        var isLocal = IsLocalHost(SelectedProfile.Host);
        var confirmed = ConfirmDialog.Confirm(
            Application.Current?.MainWindow,
            isLocal ? "Confirm restore" : "⚠ Confirm restore to REMOTE server",
            $"RESTORE DATABASE from:\n   {Path.GetFileName(BackupFile)}\n\n" +
            $"INTO target database:\n   {TargetDatabase} @ {SelectedProfile.Host}\n" +
            $"   {(isLocal ? "✓ LOCAL target — safe." : "⚠ REMOTE / network target — .bak path must be reachable from THAT server!")}\n\n" +
            (TargetExists ? "⚠ 'Overwrite existing' is ON — the current database will be REPLACED.\n\n" : "") +
            "Proceed?",
            confirmText: isLocal ? "Yes, restore" : "Yes, restore to remote",
            danger: TargetExists || !isLocal);
        if (!confirmed) { StatusText = "Restore cancelled."; return; }

        var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);
        var opts = new MsSqlRestoreOptions
        {
            Host = SelectedProfile.Host,
            Port = SelectedProfile.Port,
            IntegratedSecurity = SelectedProfile.SqlIntegratedSecurity,
            Username = SelectedProfile.Username,
            BackupFile = BackupFile,
            TargetDatabase = TargetDatabase,
            OverwriteExisting = OverwriteExisting,
        };

        LogLines.Clear();
        AppendLog($">> RESTORE DATABASE [{opts.TargetDatabase}] FROM '{Path.GetFileName(BackupFile)}'");

        _cts = new CancellationTokenSource();
        IsBusy = true; CanCancel = true;
        StatusText = "Running restore...";
        ProgressPercent = 0; HasProgress = true; ElapsedText = "00:00";

        _stopwatch = Stopwatch.StartNew();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = FormatDuration(_stopwatch.Elapsed);
        _elapsedTimer.Start();

        try
        {
            var runner = new MsSqlRestoreRunner();
            runner.LogLine += OnRunnerLogLine;
            MsSqlRestoreResult result;
            try { result = await runner.RunAsync(SelectedProfile, pwd, opts, _cts.Token); }
            finally { runner.LogLine -= OnRunnerLogLine; }

            _elapsedTimer.Stop();
            ElapsedText = FormatDuration(_stopwatch.Elapsed);

            if (result.Ok)
            {
                ProgressPercent = 100;
                StatusText = $"{result.Message} (took {ElapsedText})";
                NotificationService.NotifyCompletion("Restore complete", StatusText, success: true);
                await RefreshTargetExistsAsync();
            }
            else
            {
                StatusText = $"Restore failed: {result.Message}";
                NotificationService.NotifyCompletion("Restore failed", StatusText, success: false);
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

    private void OnRunnerLogLine(object? sender, string line)
    {
        AppendLog(line);
        var m = PercentRx.Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
            ProgressPercent = pct;
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
