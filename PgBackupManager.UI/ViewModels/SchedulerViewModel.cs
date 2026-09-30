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
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Views;

namespace PgBackupManager.UI.ViewModels;

public partial class ScheduleRow : ObservableObject
{
    public ScheduleEntry E { get; init; } = new();
    public string Preset => E.PresetName;
    public string When => E.Describe();
    [ObservableProperty] private string _nextRun = "…";
    [ObservableProperty] private string _lastRun = "";
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private string _status = "";
}

public partial class DayChoice : ObservableObject
{
    public string Code { get; init; } = "";
    [ObservableProperty] private bool _isChecked;
}

public partial class SchedulerViewModel : ObservableObject
{
    public ObservableCollection<JobPreset> Presets { get; } = new();
    [ObservableProperty] private JobPreset? _selectedPreset;
    public ObservableCollection<ScheduleRow> Schedules { get; } = new();
    [ObservableProperty] private ScheduleRow? _selectedSchedule;
    [ObservableProperty] private string _statusText = "Save presets (Transfer page, or the forms below), then schedule them. Tasks run through Windows Task Scheduler, even when this app is closed.";
    [ObservableProperty] private bool _isBusy;
    public ObservableCollection<string> RunLog { get; } = new();

    // schedule form
    public IReadOnlyList<ScheduleFrequency> Frequencies { get; } = Enum.GetValues<ScheduleFrequency>();
    [ObservableProperty] private ScheduleFrequency _frequency = ScheduleFrequency.Daily;
    [ObservableProperty] private string _time = "02:00";
    [ObservableProperty] private int _everyHours = 6;
    [ObservableProperty] private string _date = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
    public ObservableCollection<DayChoice> Days { get; } = new(new[] { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" }.Select(d => new DayChoice { Code = d, IsChecked = d is "MON" or "WED" or "FRI" }));
    public bool IsWeekly => Frequency == ScheduleFrequency.Weekly;
    public bool IsHourly => Frequency == ScheduleFrequency.Hourly;
    public bool IsOnce => Frequency == ScheduleFrequency.Once;
    partial void OnFrequencyChanged(ScheduleFrequency value) { OnPropertyChanged(nameof(IsWeekly)); OnPropertyChanged(nameof(IsHourly)); OnPropertyChanged(nameof(IsOnce)); }

    // backup preset form
    public ObservableCollection<ConnectionProfile> BackupProfiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _backupProfile;
    [ObservableProperty] private string _backupName = "";
    public IReadOnlyList<BackupFormat> Formats { get; } = Enum.GetValues<BackupFormat>();
    [ObservableProperty] private BackupFormat _format = BackupFormat.Custom;
    [ObservableProperty] private string _schemasCsv = "";
    [ObservableProperty] private bool _schemaOnly;
    [ObservableProperty] private string _destinationRoot = "";
    [ObservableProperty] private bool _applyRetention = true;
    public bool BackupIsPg => BackupProfile?.Engine == DbEngine.PostgreSql;
    partial void OnBackupProfileChanged(ConnectionProfile? value) { OnPropertyChanged(nameof(BackupIsPg)); if (string.IsNullOrWhiteSpace(BackupName) && value != null) BackupName = $"Nightly backup {value.Name}"; }

    // script preset form
    public ObservableCollection<ConnectionProfile> AllProfiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _scriptProfile;
    [ObservableProperty] private string _scriptPath = "";
    [ObservableProperty] private string _scriptName = "";
    [ObservableProperty] private bool _scriptStopOnError = true;

    public SchedulerViewModel()
    {
        Reload();
        PresetStore.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(ReloadPresets);
        ProfileStore.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(ReloadProfiles);
    }

    private void Reload() { ReloadProfiles(); ReloadPresets(); _ = ReloadSchedulesAsync(); }

    private void ReloadProfiles()
    {
        var all = new ProfileStore().LoadAll().OrderBy(p => p.Name).ToList();
        BackupProfiles.Clear(); AllProfiles.Clear();
        foreach (var p in all)
        {
            AllProfiles.Add(p);
            if (p.Engine != DbEngine.Oracle) BackupProfiles.Add(p);
        }
        BackupProfile ??= BackupProfiles.FirstOrDefault();
        ScriptProfile ??= AllProfiles.FirstOrDefault();
    }

    private void ReloadPresets()
    {
        var id = SelectedPreset?.Id;
        Presets.Clear();
        foreach (var p in PresetStore.LoadAll()) Presets.Add(p);
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == id) ?? Presets.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ReloadSchedulesAsync()
    {
        Schedules.Clear();
        foreach (var e in TaskSchedulerService.LoadAll()) Schedules.Add(new ScheduleRow { E = e });
        foreach (var row in Schedules.ToList())
        {
            var st = await TaskSchedulerService.QueryAsync(row.E);
            if (st == null) { row.NextRun = "(task missing)"; continue; }
            row.NextRun = st.NextRun; row.Status = st.Status; row.LastRun = st.LastRun; row.LastResult = st.LastResult;
        }
    }

    [RelayCommand]
    private async Task CreateScheduleAsync()
    {
        if (SelectedPreset == null) { StatusText = "Pick a preset to schedule."; return; }
        if (!TimeSpan.TryParse(Time, out _)) { StatusText = "Time must be HH:mm (24h)."; return; }
        var e = new ScheduleEntry
        {
            PresetId = SelectedPreset.Id, PresetName = SelectedPreset.Name, Frequency = Frequency, Time = Time.Trim(),
            Days = Days.Where(d => d.IsChecked).Select(d => d.Code).ToList(), EveryHours = EveryHours, Date = Date,
        };
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PgBackupManager.UI.exe");
        IsBusy = true;
        var (ok, msg) = await TaskSchedulerService.CreateAsync(e, exe);
        IsBusy = false;
        StatusText = ok ? msg : "Could not create the task: " + msg;
        await ReloadSchedulesAsync();
    }

    [RelayCommand]
    private async Task DeleteScheduleAsync(ScheduleRow? row)
    {
        row ??= SelectedSchedule;
        if (row == null) return;
        if (!ConfirmDialog.Confirm(Application.Current?.MainWindow, "Remove schedule", $"Remove the scheduled task for '{row.Preset}' ({row.When})?", confirmText: "Remove", danger: true)) return;
        var (_, msg) = await TaskSchedulerService.DeleteAsync(row.E);
        StatusText = msg;
        await ReloadSchedulesAsync();
    }

    [RelayCommand]
    private async Task TriggerScheduleAsync(ScheduleRow? row)
    {
        row ??= SelectedSchedule;
        if (row == null) return;
        var (_, msg) = await TaskSchedulerService.RunNowAsync(row.E);
        StatusText = msg;
    }

    // Runs the preset right here (not through Task Scheduler), with a live log.
    [RelayCommand]
    private async Task RunPresetNowAsync()
    {
        if (SelectedPreset == null) return;
        if (!ConfirmDialog.Confirm(Application.Current?.MainWindow, "Run preset", $"Run '{SelectedPreset.Name}' ({SelectedPreset.Kind}) now?", confirmText: "Run")) return;
        RunLog.Clear();
        IsBusy = true;
        StatusText = $"Running '{SelectedPreset.Name}'...";
        var (ok, summary) = await Task.Run(() => HeadlessJobRunner.RunAsync(SelectedPreset, l => Application.Current?.Dispatcher.BeginInvoke(() => RunLog.Add(l))));
        IsBusy = false;
        StatusText = (ok ? "✓ " : "✗ ") + summary;
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset == null) return;
        if (!ConfirmDialog.Confirm(Application.Current?.MainWindow, "Delete preset", $"Delete preset '{SelectedPreset.Name}'? Schedules using it will fail until removed.", confirmText: "Delete", danger: true)) return;
        PresetStore.Delete(SelectedPreset.Id);
    }

    [RelayCommand]
    private void SaveBackupPreset()
    {
        if (BackupProfile == null || string.IsNullOrWhiteSpace(BackupName)) { StatusText = "Pick a connection and name the preset."; return; }
        var schemas = SchemasCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        PresetStore.Upsert(new JobPreset
        {
            Name = BackupName.Trim(),
            Kind = PresetKind.Backup,
            Backup = new BackupPreset
            {
                ProfileId = BackupProfile.Id, Format = Format,
                Scope = schemas.Count > 0 ? BackupScope.SpecificSchemas : BackupScope.FullDatabase,
                Schemas = schemas, Content = SchemaOnly ? DumpContent.SchemaOnly : DumpContent.Both,
                DestinationRoot = string.IsNullOrWhiteSpace(DestinationRoot) ? null : DestinationRoot.Trim(),
                ApplyRetention = ApplyRetention,
            },
        });
        StatusText = $"Saved backup preset '{BackupName.Trim()}'.";
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dlg = new OpenFolderDialog { Title = "Backup destination root" };
        if (dlg.ShowDialog() == true) DestinationRoot = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseScript()
    {
        var dlg = new OpenFileDialog { Filter = "SQL (*.sql)|*.sql|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true) { ScriptPath = dlg.FileName; if (string.IsNullOrWhiteSpace(ScriptName)) ScriptName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName); }
    }

    [RelayCommand]
    private void SaveScriptPreset()
    {
        if (ScriptProfile == null || string.IsNullOrWhiteSpace(ScriptPath) || string.IsNullOrWhiteSpace(ScriptName)) { StatusText = "Pick a connection, a script file and a name."; return; }
        PresetStore.Upsert(new JobPreset
        {
            Name = ScriptName.Trim(), Kind = PresetKind.Script,
            Script = new ScriptPreset { ProfileId = ScriptProfile.Id, ScriptPath = ScriptPath, StopOnError = ScriptStopOnError },
        });
        StatusText = $"Saved script preset '{ScriptName.Trim()}'.";
    }
}
