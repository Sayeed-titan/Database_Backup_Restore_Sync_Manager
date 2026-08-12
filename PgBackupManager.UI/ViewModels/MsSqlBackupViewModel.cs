using System;
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

// SQL Server counterpart to BackupViewModel — deliberately simpler: BACKUP
// DATABASE has no pg_dump-style schema/table scoping, so there's no object
// tree here, just "pick a profile, pick a folder, go".
public partial class MsSqlBackupViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _selectedProfile;

    [ObservableProperty] private string _destinationRoot = "";
    [ObservableProperty] private bool _useAutoFolders = true;
    [ObservableProperty] private string _fullPathPreview = "";

    [ObservableProperty] private string _statusText = "Pick a SQL Server profile and a destination folder.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _hasProgress;
    private Stopwatch? _stopwatch;
    private DispatcherTimer? _elapsedTimer;

    // BACKUP DATABASE ... WITH STATS=5 prints "N percent processed" over the
    // same info-message channel sqlcmd showed it on (confirmed against a live
    // server this session) — MsSqlBackupRunner forwards those lines as-is via
    // LogLine, this is where they get turned into a progress percentage.
    private static readonly Regex PercentRx = new(@"(\d+) percent processed", RegexOptions.Compiled);

    public ObservableCollection<string> LogLines { get; } = new();

    public MsSqlBackupViewModel()
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
        foreach (var p in _profileStore.LoadAll().Where(p => p.Engine == DbEngine.SqlServer).OrderBy(p => p.Name))
            Profiles.Add(p);
        SelectedProfile = currentId.HasValue
            ? Profiles.FirstOrDefault(p => p.Id == currentId) ?? Profiles.FirstOrDefault()
            : Profiles.FirstOrDefault();
        UpdatePathPreview();
    }

    partial void OnSelectedProfileChanged(ConnectionProfile? value) => UpdatePathPreview();
    partial void OnDestinationRootChanged(string value) => UpdatePathPreview();
    partial void OnUseAutoFoldersChanged(bool value) => UpdatePathPreview();

    private void UpdatePathPreview()
    {
        if (SelectedProfile is null || string.IsNullOrWhiteSpace(DestinationRoot)) { FullPathPreview = ""; return; }
        var now = DateTime.Now;
        var folder = FilenameBuilder.BuildFolder(DestinationRoot, SelectedProfile.Database, UseAutoFolders, now);
        FullPathPreview = Path.Combine(folder, BuildFileName(SelectedProfile.Database, now));
    }

    // BACKUP DATABASE is always a full, single-file backup — no Format/Scope/
    // Content variants like pg_dump, so the name only needs db + timestamp.
    private static string BuildFileName(string database, DateTime now) => $"{database}_full_{now:yyyyMMdd_HHmmss}.bak";

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

        var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);
        var now = DateTime.Now;
        var folder = FilenameBuilder.BuildFolder(DestinationRoot, SelectedProfile.Database, UseAutoFolders, now);
        var fullPath = Path.Combine(folder, BuildFileName(SelectedProfile.Database, now));

        var job = new MsSqlBackupJob
        {
            Host = SelectedProfile.Host,
            Port = SelectedProfile.Port,
            Database = SelectedProfile.Database,
            IntegratedSecurity = SelectedProfile.SqlIntegratedSecurity,
            Username = SelectedProfile.Username,
            DestinationRoot = DestinationRoot,
            UseAutoFolders = UseAutoFolders,
            FullOutputPath = fullPath,
        };

        LogLines.Clear();
        AppendLog($">> BACKUP DATABASE [{job.Database}] -> {fullPath}");

        _cts = new CancellationTokenSource();
        IsBusy = true; CanCancel = true;
        StatusText = "Running backup...";
        ProgressPercent = 0; HasProgress = true; ElapsedText = "00:00";

        _stopwatch = Stopwatch.StartNew();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = FormatDuration(_stopwatch.Elapsed);
        _elapsedTimer.Start();

        try
        {
            var runner = new MsSqlBackupRunner();
            runner.LogLine += OnRunnerLogLine;
            MsSqlBackupResult result;
            try { result = await runner.RunAsync(SelectedProfile, pwd, job, _cts.Token); }
            finally { runner.LogLine -= OnRunnerLogLine; }

            _elapsedTimer.Stop();
            ElapsedText = FormatDuration(_stopwatch.Elapsed);

            if (result.Ok)
            {
                ProgressPercent = 100;
                var size = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                StatusText = $"Backup OK: {Path.GetFileName(fullPath)} · {size / 1024.0 / 1024.0:F1} MB (took {ElapsedText})";
                NotificationService.NotifyCompletion("Backup complete", StatusText, success: true);
            }
            else
            {
                StatusText = $"Backup failed: {result.Message}";
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
