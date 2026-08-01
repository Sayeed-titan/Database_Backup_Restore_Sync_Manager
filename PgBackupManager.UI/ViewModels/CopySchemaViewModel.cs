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

public partial class CopySchemaViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private readonly SettingsStore _settingsStore = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _selectedProfile;

    public ObservableCollection<string> AvailableSchemas { get; } = new();
    [ObservableProperty] private string? _sourceSchema;
    [ObservableProperty] private string _newSchemaName = "";

    [ObservableProperty] private string _statusText = "Pick a profile and click Load Schemas.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string _elapsedText = "";
    private Stopwatch? _stopwatch;
    private DispatcherTimer? _elapsedTimer;

    public ObservableCollection<string> LogLines { get; } = new();

    public CopySchemaViewModel()
    {
        ReloadProfiles();
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

    [RelayCommand]
    private async Task LoadSchemasAsync()
    {
        if (SelectedProfile is null) { StatusText = "Pick a profile first."; return; }
        try
        {
            IsBusy = true;
            StatusText = "Loading schemas...";
            var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);
            var objs = await DbObjectInspector.InspectAsync(SelectedProfile.BuildConnectionString(pwd));

            var previouslySelected = SourceSchema;
            AvailableSchemas.Clear();
            foreach (var s in objs.Where(o => o.Kind == DbObjectKind.Schema).Select(o => o.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                AvailableSchemas.Add(s);
            SourceSchema = AvailableSchemas.Contains(previouslySelected ?? "") ? previouslySelected : AvailableSchemas.FirstOrDefault();

            StatusText = $"Loaded {AvailableSchemas.Count} schema(s) from '{SelectedProfile.Database}'.";
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

    [RelayCommand]
    private async Task CopySchemaAsync()
    {
        if (SelectedProfile is null) { StatusText = "Pick a profile."; return; }
        if (string.IsNullOrWhiteSpace(SourceSchema)) { StatusText = "Pick a source schema."; return; }

        var newName = NewSchemaName.Trim();
        if (string.IsNullOrWhiteSpace(newName)) { StatusText = "Type a name for the new schema."; return; }
        if (string.Equals(newName, SourceSchema, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "New schema name must be different from the source.";
            return;
        }

        var pwd = SecretProtector.Unprotect(SelectedProfile.EncryptedPasswordBase64);

        // Pre-flight: the new name must not already exist — CREATE SCHEMA has
        // no "IF NOT EXISTS" in a plain pg_dump, so this would otherwise abort
        // the whole (single-transaction) restore on its very first statement.
        var liveObjects = await DbObjectInspector.InspectAsync(SelectedProfile.BuildConnectionString(pwd));
        var existingSchemas = new HashSet<string>(
            liveObjects.Where(o => o.Kind == DbObjectKind.Schema).Select(o => o.Name),
            StringComparer.OrdinalIgnoreCase);
        if (existingSchemas.Contains(newName))
        {
            ConfirmDialog.Alert(
                Application.Current?.MainWindow,
                "Schema already exists",
                $"A schema named '{newName}' already exists in '{SelectedProfile.Database}'. Pick a different name.");
            StatusText = $"'{newName}' already exists — pick a different name.";
            return;
        }

        var settings = _settingsStore.Load();
        var tools = PgToolsLocator.Locate(settings.PgBinDirOverride);
        if (string.IsNullOrEmpty(tools.PgDump) || string.IsNullOrEmpty(tools.Psql))
        {
            StatusText = "pg_dump.exe / psql.exe not found. Configure them in Settings.";
            return;
        }

        // Same version-mismatch guard as Backup/Restore — pg_dump connects to
        // read the source, so a newer client can fail the same way.
        if (tools.MajorVersion.HasValue)
        {
            var serverMajor = await DatabaseAdmin.GetServerMajorVersionAsync(SelectedProfile, pwd);
            if (serverMajor.HasValue && tools.MajorVersion.Value > serverMajor.Value)
            {
                var switched = ConfirmDialog.ShowVersionMismatch(
                    Application.Current?.MainWindow, "pg_dump", tools.Version ?? "?", serverMajor.Value, settings.PgBinDirOverride);
                StatusText = switched
                    ? "Switched PostgreSQL client tools — click Copy Schema again to retry."
                    : $"Blocked — pg_dump {tools.Version} is newer than the server (PostgreSQL {serverMajor}.x).";
                return;
            }
        }

        var objCount = liveObjects.Count(o => o.Kind != DbObjectKind.Schema
            && string.Equals(o.Schema, SourceSchema, StringComparison.OrdinalIgnoreCase));

        var confirmed = ConfirmDialog.Confirm(
            Application.Current?.MainWindow,
            "Confirm schema copy",
            $"Copy schema '{SourceSchema}' ({objCount} objects + all their data) into a NEW schema named '{newName}'\n" +
            $"in database '{SelectedProfile.Database}' @ {SelectedProfile.Host}:{SelectedProfile.Port}.\n\n" +
            "The source schema is only read from — nothing about it changes.\n\nProceed?",
            confirmText: "Yes, copy");
        if (!confirmed) { StatusText = "Cancelled."; return; }

        LogLines.Clear();
        AppendLog($">> copying schema '{SourceSchema}' -> '{newName}' in '{SelectedProfile.Database}' @ {SelectedProfile.Host}");

        _cts = new CancellationTokenSource();
        IsBusy = true; CanCancel = true;
        StatusText = "Copying schema...";
        ElapsedText = "00:00";
        _stopwatch = Stopwatch.StartNew();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = FormatDuration(_stopwatch.Elapsed);
        _elapsedTimer.Start();

        try
        {
            var runner = new SchemaCopyRunner();
            runner.DumpProcess.StdoutLine += OnLogLine;
            runner.DumpProcess.StderrLine += OnLogLine;
            runner.RestoreProcess.StdoutLine += OnLogLine;
            runner.RestoreProcess.StderrLine += OnLogLine;

            var opts = new SchemaCopyOptions
            {
                Host = SelectedProfile.Host,
                Port = SelectedProfile.Port,
                Database = SelectedProfile.Database,
                Username = SelectedProfile.Username,
                SourceSchema = SourceSchema!,
                NewSchemaName = newName,
            };

            var exit = await runner.RunAsync(tools.PgDump!, tools.Psql!, opts, pwd, _cts.Token);

            _elapsedTimer.Stop();
            ElapsedText = FormatDuration(_stopwatch.Elapsed);

            if (exit == 0)
            {
                AppendLog($">> SUCCESS · took {ElapsedText}");
                var doneMsg = $"Copied '{SourceSchema}' to '{newName}' in '{SelectedProfile.Database}' (took {ElapsedText}).";
                StatusText = doneMsg;
                await LoadSchemasAsync();
                NotificationService.NotifyCompletion("Schema copy complete", doneMsg, success: true);
            }
            else
            {
                AppendLog($">> FAILED (exit {exit}) — the transaction was rolled back, nothing was left half-created.");
                StatusText = $"Copy failed (exit {exit}). See log.";
                NotificationService.NotifyCompletion("Schema copy failed", StatusText, success: false);
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
            NotificationService.NotifyCompletion("Schema copy error", StatusText, success: false);
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

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
