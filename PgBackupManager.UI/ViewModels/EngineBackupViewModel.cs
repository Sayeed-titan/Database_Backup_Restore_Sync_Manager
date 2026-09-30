using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;
using PgBackupManager.UI.Views;

namespace PgBackupManager.UI.ViewModels;

public partial class EngineBackupViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _profile;
    [ObservableProperty] private bool _isRestore;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _schema = "";
    [ObservableProperty] private bool _schemaOnly;

    // Oracle Data Pump
    public ObservableCollection<string> Directories { get; } = new();
    [ObservableProperty] private string _directory = "DATA_PUMP_DIR";
    [ObservableProperty] private string _dumpFile = "";
    [ObservableProperty] private string _remapSchema = "";
    public IReadOnlyList<string> TableActions { get; } = new[] { "SKIP", "APPEND", "TRUNCATE", "REPLACE" };
    [ObservableProperty] private string _tableAction = "SKIP";

    public ObservableCollection<string> LogLines { get; } = new();
    [ObservableProperty] private string _statusText = "SQLite, MySQL/MariaDB and Oracle backups & restores. (PostgreSQL and SQL Server have their own pages.)";
    [ObservableProperty] private bool _isBusy;

    public bool IsSqlite => Profile?.Engine == DbEngine.Sqlite;
    public bool IsMySql => Profile?.Engine == DbEngine.MySql;
    public bool IsOracle => Profile?.Engine == DbEngine.Oracle;
    public bool UsesLocalFile => !IsOracle;
    public string EngineNote => Profile?.Engine switch
    {
        DbEngine.Sqlite => IsRestore ? "Copies the backup file over the connection's database file (online backup API) — current contents are replaced." : "Hot copy with SQLite's online backup API — safe while the database is in use. No tools needed.",
        DbEngine.MySql => IsRestore ? "Replays the .sql script in-app (no mysql client needed). DELIMITER blocks, routines and triggers are handled." : "Uses mysqldump (--single-transaction --routines --triggers --events). Install the MySQL/MariaDB client if it's not found (Settings → Dependencies).",
        DbEngine.Oracle => IsRestore ? "Data Pump import (impdp). The .dmp must already be in the server-side DIRECTORY." : "Data Pump export (expdp). The .dmp is written on the DATABASE SERVER, inside the chosen DIRECTORY object. Needs Oracle Instant Client tools (Settings → Dependencies — one-click download).",
        _ => "Add a SQLite, MySQL/MariaDB or Oracle connection under Connections first.",
    };
    public string RunText => IsRestore ? "Run Restore" : "Run Backup";

    public EngineBackupViewModel()
    {
        Reload();
        ProfileStore.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(Reload);
    }

    private void Reload()
    {
        var id = Profile?.Id;
        Profiles.Clear();
        foreach (var p in new ProfileStore().LoadAll().Where(p => p.Engine is DbEngine.Sqlite or DbEngine.MySql or DbEngine.Oracle).OrderBy(p => p.Engine).ThenBy(p => p.Name)) Profiles.Add(p);
        Profile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault();
    }

    partial void OnProfileChanged(ConnectionProfile? value)
    {
        foreach (var n in new[] { nameof(IsSqlite), nameof(IsMySql), nameof(IsOracle), nameof(UsesLocalFile), nameof(EngineNote) }) OnPropertyChanged(n);
        Schema = value?.Engine switch { DbEngine.MySql => value.Database, DbEngine.Oracle => value.ResolveDefaultSchema(), _ => "" };
        SuggestPath();
    }

    partial void OnIsRestoreChanged(bool value)
    {
        OnPropertyChanged(nameof(EngineNote)); OnPropertyChanged(nameof(RunText));
        SuggestPath();
    }

    private void SuggestPath()
    {
        if (Profile == null) return;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        if (IsOracle) { DumpFile = $"{(string.IsNullOrWhiteSpace(Schema) ? "EXPORT" : Schema.ToUpperInvariant())}_{stamp}.dmp"; return; }
        if (IsRestore) { FilePath = ""; return; }
        var s = new SettingsStore().Load();
        var name = Path.GetFileNameWithoutExtension(Profile.Database);
        var folder = FilenameBuilder.BuildFolder(s.DefaultBackupRoot, name, s.UseAutoFolders, DateTime.Now);
        FilePath = Path.Combine(folder, $"{name}_{stamp}{(IsSqlite ? ".db" : ".sql")}");
    }

    [RelayCommand]
    private void Browse()
    {
        if (IsRestore)
        {
            var dlg = new OpenFileDialog { Filter = IsSqlite ? "SQLite (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*" : "SQL script (*.sql)|*.sql|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == true) FilePath = dlg.FileName;
        }
        else
        {
            var dlg = new SaveFileDialog { FileName = Path.GetFileName(FilePath), Filter = IsSqlite ? "SQLite (*.db)|*.db" : "SQL script (*.sql)|*.sql" };
            if (dlg.ShowDialog() == true) FilePath = dlg.FileName;
        }
    }

    [RelayCommand]
    private async Task LoadDirectoriesAsync()
    {
        if (Profile == null || !IsOracle) return;
        try
        {
            Directories.Clear();
            foreach (var (name, path) in await EngineBackupRunner.OracleDirectoriesAsync(Profile)) Directories.Add(name);
            StatusText = $"{Directories.Count} DIRECTORY object(s) visible to {Profile.Username}.";
        }
        catch (Exception ex) { StatusText = "ERROR: " + ex.Message; }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Profile == null) { StatusText = "Pick a connection."; return; }
        if (UsesLocalFile && string.IsNullOrWhiteSpace(FilePath)) { StatusText = "Pick a file."; return; }
        if (IsRestore && !ConfirmDialog.Confirm(Application.Current?.MainWindow, "Confirm restore",
                $"Restore into {Profile} ({Profile.EngineLabel})?\n\n{EngineNote}", confirmText: "Yes, restore", danger: true)) return;

        LogLines.Clear();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var runner = new EngineBackupRunner();
        runner.LogLine += (_, l) => Application.Current?.Dispatcher.BeginInvoke(() => LogLines.Add(l));
        try
        {
            EngineBackupResult r = (Profile.Engine, IsRestore) switch
            {
                (DbEngine.Sqlite, false) => await runner.SqliteBackupAsync(Profile, FilePath, _cts.Token),
                (DbEngine.Sqlite, true) => await runner.SqliteRestoreAsync(FilePath, Profile, _cts.Token),
                (DbEngine.MySql, false) => await runner.MySqlDumpAsync(Profile, string.IsNullOrWhiteSpace(Schema) ? null : Schema, FilePath, SchemaOnly, _cts.Token),
                (DbEngine.MySql, true) => await runner.RunSqlFileAsync(Profile, FilePath, _cts.Token),
                _ => await runner.OracleDataPumpAsync(Profile, !IsRestore, Directory, DumpFile, string.IsNullOrWhiteSpace(Schema) ? null : Schema.ToUpperInvariant(),
                                                     string.IsNullOrWhiteSpace(RemapSchema) ? null : RemapSchema, TableAction, _cts.Token),
            };
            StatusText = $"{r.Message} (took {sw.Elapsed:mm\\:ss})";
            NotificationService.NotifyCompletion(r.Ok ? "Finished" : "Failed", StatusText, r.Ok, record: false);
            JobHistory.Add(IsRestore ? "Restore" : "Backup", $"{Profile} ({Profile.EngineLabel})", r.Message + (r.OutputPath != null ? " " + r.OutputPath : ""), r.Ok, sw.Elapsed,
                JobHistory.SaveLog("engine-backup", LogLines));
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        catch (Exception ex) { StatusText = "ERROR: " + ex.Message; LogLines.Add(">> ERROR: " + ex.Message); }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();
}
