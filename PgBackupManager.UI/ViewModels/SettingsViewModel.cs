using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;
using PgBackupManager.UI.Views;

namespace PgBackupManager.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();

    [ObservableProperty] private string _pgBinDirOverride = "";

    // "Nothing installed at all" fallback, next to Quick Switch — fetches a
    // client tools set directly instead of sending the user off to install
    // PostgreSQL themselves. Same PgToolsDownloadDialog the version-mismatch
    // guard (ConfirmDialog.ShowVersionMismatch) uses.
    public IReadOnlyList<int> DownloadableVersions { get; } = PgToolsDownloader.SupportedMajorVersions;
    [ObservableProperty] private int _selectedDownloadVersion = PgToolsDownloader.SupportedMajorVersions.FirstOrDefault();

    // Every complete PostgreSQL client install found on this machine (Program
    // Files roots + whatever's currently configured, even if it lives
    // elsewhere) — lets switching between e.g. a PG15 and a PG18 install be a
    // single pick instead of re-typing/browsing a folder path each time.
    public ObservableCollection<PgInstall> DetectedInstalls { get; } = new();
    [ObservableProperty] private PgInstall? _selectedInstall;
    [ObservableProperty] private string _defaultBackupRoot = "";
    [ObservableProperty] private string _defaultRestoreSource = "";
    [ObservableProperty] private bool _useAutoFolders = true;
    [ObservableProperty] private int _retentionDays = 30;

    [ObservableProperty] private bool _notifyOnCompletion = true;
    [ObservableProperty] private int _notificationDurationSeconds = 6;
    [ObservableProperty] private bool _flashTaskbarOnCompletion = true;

    [ObservableProperty] private string _detectedPgVersion = "";
    [ObservableProperty] private string _detectedPgDump = "";
    [ObservableProperty] private string _detectedPgRestore = "";
    [ObservableProperty] private string _detectedPsql = "";
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private int _retentionPreviewCount;
    [ObservableProperty] private string _settingsFilePath = "";

    public string CurrentVersionText => $"v{UpdateService.CurrentVersion.Major}.{UpdateService.CurrentVersion.Minor}";

    public SettingsViewModel()
    {
        LoadFromStore();
    }

    // Shared by the constructor and Cancel — reloads every field from
    // whatever is currently saved on disk, discarding any in-memory edits.
    private void LoadFromStore()
    {
        var s = _store.Load();
        PgBinDirOverride = s.PgBinDirOverride ?? "";
        DefaultBackupRoot = s.DefaultBackupRoot;
        DefaultRestoreSource = s.DefaultRestoreSource;
        UseAutoFolders = s.UseAutoFolders;
        RetentionDays = s.RetentionDays;
        NotifyOnCompletion = s.NotifyOnCompletion;
        NotificationDurationSeconds = s.NotificationDurationSeconds;
        FlashTaskbarOnCompletion = s.FlashTaskbarOnCompletion;
        SettingsFilePath = _store.FilePath;
        DetectTools();
        PreviewRetention();
    }

    private void DetectTools()
    {
        var bin = string.IsNullOrWhiteSpace(PgBinDirOverride) ? null : PgBinDirOverride;
        var tools = PgToolsLocator.Locate(bin);
        DetectedPgDump = tools.PgDump ?? "(not found)";
        DetectedPgRestore = tools.PgRestore ?? "(not found)";
        DetectedPsql = tools.Psql ?? "(not found)";
        DetectedPgVersion = tools.Version ?? "(unknown)";

        DetectedInstalls.Clear();
        foreach (var install in PgToolsLocator.DetectAllInstalls(bin))
            DetectedInstalls.Add(install);

        // Reflect whichever install is ACTUALLY active right now (override or
        // auto-detected) so Quick Switch always shows the current version at a
        // glance, instead of sitting blank until you deliberately change it.
        var activeBinDir = string.IsNullOrEmpty(tools.PgDump) ? null : Path.GetDirectoryName(tools.PgDump);
        SelectedInstall = activeBinDir is null
            ? null
            : DetectedInstalls.FirstOrDefault(i => string.Equals(
                Path.GetFullPath(i.BinDir), Path.GetFullPath(activeBinDir), StringComparison.OrdinalIgnoreCase));
    }

    partial void OnPgBinDirOverrideChanged(string value) => DetectTools();

    // Picking an install from the dropdown is the "single click" switch —
    // just fills in Bin Folder Override with that install's path. Guarded
    // against re-firing when it already matches, since setting
    // PgBinDirOverride re-runs DetectTools() (which rebuilds this very list).
    partial void OnSelectedInstallChanged(PgInstall? value)
    {
        if (value != null && !string.Equals(value.BinDir, PgBinDirOverride, StringComparison.OrdinalIgnoreCase))
            PgBinDirOverride = value.BinDir;
    }
    partial void OnDefaultBackupRootChanged(string value) => PreviewRetention();
    partial void OnRetentionDaysChanged(int value) => PreviewRetention();

    private void PreviewRetention()
    {
        try
        {
            RetentionPreviewCount = RetentionPolicy.FindExpired(DefaultBackupRoot, RetentionDays, DateTime.Now).Count;
        }
        catch { RetentionPreviewCount = 0; }
    }

    [RelayCommand]
    private void DownloadPgTools()
    {
        var dlg = new PgToolsDownloadDialog(SelectedDownloadVersion) { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() == true && dlg.InstalledBinDir != null)
        {
            PgBinDirOverride = dlg.InstalledBinDir; // triggers OnPgBinDirOverrideChanged -> DetectTools()
            StatusText = $"Downloaded PostgreSQL {SelectedDownloadVersion} client tools — click Save Settings to keep this.";
        }
    }

    [RelayCommand]
    private void BrowsePgBin()
    {
        var dlg = new OpenFolderDialog { Title = "Select PostgreSQL bin folder (contains pg_dump.exe)" };
        if (Directory.Exists(PgBinDirOverride)) dlg.InitialDirectory = PgBinDirOverride;
        if (dlg.ShowDialog() == true) PgBinDirOverride = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseBackupRoot()
    {
        var dlg = new OpenFolderDialog { Title = "Default backup root" };
        if (Directory.Exists(DefaultBackupRoot)) dlg.InitialDirectory = DefaultBackupRoot;
        if (dlg.ShowDialog() == true) DefaultBackupRoot = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseRestoreSource()
    {
        var dlg = new OpenFolderDialog { Title = "Default restore source folder" };
        if (Directory.Exists(DefaultRestoreSource)) dlg.InitialDirectory = DefaultRestoreSource;
        if (dlg.ShowDialog() == true) DefaultRestoreSource = dlg.FolderName;
    }

    [RelayCommand]
    private void Save()
    {
        _store.Save(new Core.Models.AppSettings
        {
            PgBinDirOverride = string.IsNullOrWhiteSpace(PgBinDirOverride) ? null : PgBinDirOverride.Trim(),
            DefaultBackupRoot = DefaultBackupRoot.Trim(),
            DefaultRestoreSource = DefaultRestoreSource.Trim(),
            UseAutoFolders = UseAutoFolders,
            RetentionDays = RetentionDays,
            NotifyOnCompletion = NotifyOnCompletion,
            NotificationDurationSeconds = NotificationDurationSeconds,
            FlashTaskbarOnCompletion = FlashTaskbarOnCompletion,
        });
        StatusText = $"Saved to {_store.FilePath}";
    }

    [RelayCommand]
    private void Cancel()
    {
        LoadFromStore();
        StatusText = "Changes discarded — reverted to last saved settings.";
    }

    // Previews the CURRENT (possibly unsaved) toggle/duration values directly,
    // rather than going through NotificationService (which reloads whatever
    // was last saved to disk) — so you can check how it'll look before saving.
    [RelayCommand]
    private void TestNotification()
    {
        if (NotifyOnCompletion)
            ToastWindow.Show("Test notification", "This is what a backup/restore completion looks like.", true, NotificationDurationSeconds);
        if (FlashTaskbarOnCompletion)
            NotificationService.FlashTaskbar();

        StatusText = (NotifyOnCompletion || FlashTaskbarOnCompletion)
            ? "Sent test notification — minimize the window to check it shows up while minimized too."
            : "Both notification options are off — nothing to test. Tick one above first.";
    }

    [RelayCommand]
    private void RunRetentionCleanup()
    {
        var deleted = RetentionPolicy.DeleteExpired(DefaultBackupRoot, RetentionDays, DateTime.Now);
        StatusText = $"Deleted {deleted} backup file(s) older than {RetentionDays} days from {DefaultBackupRoot}.";
        PreviewRetention();
    }

    // Manual check always reports back (found / up to date / error) since the
    // user explicitly asked — unlike the silent automatic check on startup.
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        StatusText = "Checking for updates...";
        await UpdateService.CheckAsync(silent: false);
        StatusText = "";
    }
}
