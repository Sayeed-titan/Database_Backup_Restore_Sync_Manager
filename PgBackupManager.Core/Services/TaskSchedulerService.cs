using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

public enum ScheduleFrequency { Daily, Weekly, Hourly, Once }

public sealed class ScheduleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PresetId { get; set; }
    public string PresetName { get; set; } = "";
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Daily;
    public string Time { get; set; } = "02:00";            // HH:mm
    public List<string> Days { get; set; } = new();         // MON..SUN for Weekly
    public int EveryHours { get; set; } = 6;                // Hourly
    public string? Date { get; set; }                       // yyyy-MM-dd for Once
    public string TaskName => $@"PgBackupManager\{Sanitize(PresetName)}_{Id.ToString()[..8]}";

    public string Describe() => Frequency switch
    {
        ScheduleFrequency.Daily => $"Daily at {Time}",
        ScheduleFrequency.Weekly => $"Weekly on {string.Join(", ", Days)} at {Time}",
        ScheduleFrequency.Hourly => $"Every {EveryHours} hour(s) from {Time}",
        _ => $"Once on {Date} at {Time}",
    };

    private static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}

public sealed record ScheduleStatus(string NextRun, string Status, string LastRun, string LastResult);

// Scheduled jobs are real Windows Task Scheduler tasks (so they run even when
// the app is closed) that launch "PgBackupManager.exe --run-preset <id>".
// They run as the current user while logged on — no password is stored.
public static class TaskSchedulerService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PgBackupManager", "schedules.json");

    public static List<ScheduleEntry> LoadAll()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<ScheduleEntry>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void SaveAll(List<ScheduleEntry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Opts));
    }

    public static async Task<(bool Ok, string Message)> CreateAsync(ScheduleEntry e, string exePath)
    {
        var tr = $"\"{exePath}\" --run-preset {e.PresetId}";
        var args = new List<string> { "/Create", "/TN", e.TaskName, "/TR", tr, "/F" };
        switch (e.Frequency)
        {
            case ScheduleFrequency.Daily: args.AddRange(new[] { "/SC", "DAILY", "/ST", e.Time }); break;
            case ScheduleFrequency.Weekly: args.AddRange(new[] { "/SC", "WEEKLY", "/D", string.Join(",", e.Days.DefaultIfEmpty("MON")), "/ST", e.Time }); break;
            case ScheduleFrequency.Hourly: args.AddRange(new[] { "/SC", "HOURLY", "/MO", Math.Clamp(e.EveryHours, 1, 23).ToString(), "/ST", e.Time }); break;
            case ScheduleFrequency.Once:
                var d = DateTime.TryParse(e.Date, out var dt) ? dt : DateTime.Today;
                // schtasks wants the date in the machine's short-date format.
                args.AddRange(new[] { "/SC", "ONCE", "/SD", d.ToString(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern), "/ST", e.Time });
                break;
        }
        var (code, output) = await RunAsync(args);
        if (code != 0) return (false, output.Trim());
        var all = LoadAll();
        all.RemoveAll(x => x.Id == e.Id);
        all.Add(e);
        SaveAll(all);
        return (true, $"Scheduled '{e.PresetName}' — {e.Describe()}.");
    }

    public static async Task<(bool Ok, string Message)> DeleteAsync(ScheduleEntry e)
    {
        var (code, output) = await RunAsync(new[] { "/Delete", "/TN", e.TaskName, "/F" });
        var all = LoadAll();
        all.RemoveAll(x => x.Id == e.Id);
        SaveAll(all);
        return (code == 0, code == 0 ? "Schedule removed." : output.Trim() + " (removed from the list anyway)");
    }

    public static async Task<(bool Ok, string Message)> RunNowAsync(ScheduleEntry e)
    {
        var (code, output) = await RunAsync(new[] { "/Run", "/TN", e.TaskName });
        return (code == 0, code == 0 ? "Started — see History for the result." : output.Trim());
    }

    // /V /FO CSV column order is fixed (only the header text is localized):
    // HostName, TaskName, Next Run Time, Status, Logon Mode, Last Run Time, Last Result, ...
    public static async Task<ScheduleStatus?> QueryAsync(ScheduleEntry e)
    {
        var (code, output) = await RunAsync(new[] { "/Query", "/TN", e.TaskName, "/V", "/FO", "CSV", "/NH" });
        if (code != 0) return null;
        var line = output.Split('\n').FirstOrDefault(l => l.Contains(e.TaskName.Split('\\').Last()));
        if (line == null) return null;
        var f = SplitCsv(line);
        return f.Count >= 7 ? new ScheduleStatus(f[2], f[3], f[5], f[6] == "0" ? "OK" : f[6]) : null;
    }

    private static List<string> SplitCsv(string line)
    {
        var list = new List<string>(); var sb = new StringBuilder(); bool q = false;
        foreach (var c in line.Trim())
        {
            if (c == '"') { q = !q; continue; }
            if (c == ',' && !q) { list.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        list.Add(sb.ToString());
        return list;
    }

    private static async Task<(int Code, string Output)> RunAsync(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = await p.StandardOutput.ReadToEndAsync();
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, o + err);
    }
}
