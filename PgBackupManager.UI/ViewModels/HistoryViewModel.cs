using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;
using PgBackupManager.UI.Views;

namespace PgBackupManager.UI.ViewModels;

public sealed class JobRow
{
    public JobRecord R { get; init; } = new();
    public string When => R.FinishedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Kind => R.Kind + (R.Scheduled ? " ⏱" : "");
    public string Title => R.Title;
    public string Summary => R.Summary;
    public string Duration => R.DurationSeconds <= 0 ? "" : R.DurationSeconds < 60 ? $"{R.DurationSeconds:N1} s" : TimeSpan.FromSeconds(R.DurationSeconds).ToString(@"h\:mm\:ss");
    public bool Success => R.Success;
    public string Result => R.Success ? "OK" : "FAILED";
    public bool HasLog => R.LogPath != null && File.Exists(R.LogPath);
}

public partial class HistoryViewModel : ObservableObject
{
    private List<JobRow> _all = new();
    public ObservableCollection<JobRow> Rows { get; } = new();
    [ObservableProperty] private JobRow? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyFailures;
    [ObservableProperty] private string _summary = "";
    public IReadOnlyList<string> Kinds { get; private set; } = new[] { "All" };
    [ObservableProperty] private string _kind = "All";

    public HistoryViewModel()
    {
        Refresh();
        JobHistory.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Refresh);
    }

    partial void OnSearchChanged(string value) => Apply();
    partial void OnOnlyFailuresChanged(bool value) => Apply();
    partial void OnKindChanged(string value) => Apply();

    [RelayCommand]
    private void Refresh()
    {
        _all = JobHistory.Load().Select(r => new JobRow { R = r }).ToList();
        Kinds = new[] { "All" }.Concat(_all.Select(r => r.R.Kind).Distinct().OrderBy(k => k)).ToList();
        OnPropertyChanged(nameof(Kinds));
        Apply();
    }

    private void Apply()
    {
        Rows.Clear();
        var term = Search.Trim();
        foreach (var r in _all)
        {
            if (OnlyFailures && r.Success) continue;
            if (Kind != "All" && r.R.Kind != Kind) continue;
            if (term.Length > 0 && !(r.Title + " " + r.Summary).Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(r);
        }
        var today = _all.Where(r => r.R.FinishedUtc.ToLocalTime().Date == DateTime.Today).ToList();
        Summary = $"{_all.Count} run(s) recorded · today {today.Count} ({today.Count(r => !r.Success)} failed) · {_all.Count(r => !r.Success)} failure(s) overall";
    }

    [RelayCommand]
    private void OpenLog(JobRow? row)
    {
        row ??= Selected;
        if (row?.HasLog != true) return;
        Navigator.OpenSql(Path.GetFileName(row.R.LogPath!), File.ReadAllText(row.R.LogPath!));
    }

    [RelayCommand] private void OpenLogFolder() { Directory.CreateDirectory(JobHistory.LogFolder); System.Diagnostics.Process.Start("explorer.exe", JobHistory.LogFolder); }

    [RelayCommand]
    private void Clear()
    {
        if (!ConfirmDialog.Confirm(Application.Current?.MainWindow, "Clear history", "Delete the whole run history? (Log files are kept.)", confirmText: "Clear", danger: true)) return;
        JobHistory.Clear();
    }
}
