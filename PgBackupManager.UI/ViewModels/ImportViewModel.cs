using System;
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

    // Postgres side — reuses the same saved connection profiles as every other tab.
    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _targetProfile;
    [ObservableProperty] private string _targetSchema = "dcci_migration_from_mssql";

    [ObservableProperty] private bool _dryRun = true;

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
        foreach (var p in _profileStore.LoadAll().OrderBy(p => p.Name)) Profiles.Add(p);
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
        }
        catch (Exception ex)
        {
            StatusText = $"ERROR loading tables: {ex.Message}";
        }
    }

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
                if (!DryRun) NotificationService.NotifyCompletion("Import complete", StatusText, success: true);
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
