using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PgBackupManager.Core.Services;

namespace PgBackupManager.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();

    [ObservableProperty] private string _pgBinDirOverride = "";
    [ObservableProperty] private string _defaultBackupRoot = "";
    [ObservableProperty] private string _defaultRestoreSource = "";
    [ObservableProperty] private bool _useAutoFolders = true;
    [ObservableProperty] private int _retentionDays = 30;

    [ObservableProperty] private string _detectedPgVersion = "";
    [ObservableProperty] private string _detectedPgDump = "";
    [ObservableProperty] private string _detectedPgRestore = "";
    [ObservableProperty] private string _detectedPsql = "";
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private int _retentionPreviewCount;
    [ObservableProperty] private string _settingsFilePath = "";

    public SettingsViewModel()
    {
        var s = _store.Load();
        PgBinDirOverride = s.PgBinDirOverride ?? "";
        DefaultBackupRoot = s.DefaultBackupRoot;
        DefaultRestoreSource = s.DefaultRestoreSource;
        UseAutoFolders = s.UseAutoFolders;
        RetentionDays = s.RetentionDays;
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
    }

    partial void OnPgBinDirOverrideChanged(string value) => DetectTools();
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
        });
        StatusText = $"Saved to {_store.FilePath}";
    }

    [RelayCommand]
    private void RunRetentionCleanup()
    {
        var deleted = RetentionPolicy.DeleteExpired(DefaultBackupRoot, RetentionDays, DateTime.Now);
        StatusText = $"Deleted {deleted} backup file(s) older than {RetentionDays} days from {DefaultBackupRoot}.";
        PreviewRetention();
    }
}
