using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PgBackupManager.Core.Services;

public sealed class JobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime FinishedUtc { get; set; } = DateTime.UtcNow;
    public double DurationSeconds { get; set; }
    public string Kind { get; set; } = "";     // Backup, Restore, Transfer, Script, Import, Sync, ...
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Success { get; set; }
    public string? LogPath { get; set; }
    public bool Scheduled { get; set; }
}

// Append-only run log (one JSON object per line) so a crash mid-write can
// only ever lose the last record, never corrupt the whole history.
public static class JobHistory
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PgBackupManager", "history.jsonl");

    public static string LogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PgBackupManager", "logs");

    public static event EventHandler? Changed;

    private static readonly object Gate = new();

    public static void Add(JobRecord r)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(r) + Environment.NewLine);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }
        catch { /* history must never break the job itself */ }
    }

    public static void Add(string kind, string title, string summary, bool success, TimeSpan? duration = null, string? logPath = null, bool scheduled = false) =>
        Add(new JobRecord { Kind = kind, Title = title, Summary = summary, Success = success, DurationSeconds = duration?.TotalSeconds ?? 0, LogPath = logPath, Scheduled = scheduled });

    public static List<JobRecord> Load(int max = 1000)
    {
        if (!File.Exists(FilePath)) return new();
        var list = new List<JobRecord>();
        lock (Gate)
        {
            foreach (var line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { var r = JsonSerializer.Deserialize<JobRecord>(line); if (r != null) list.Add(r); } catch { /* skip a torn line */ }
            }
        }
        return list.OrderByDescending(r => r.FinishedUtc).Take(max).ToList();
    }

    public static void Clear()
    {
        lock (Gate) { if (File.Exists(FilePath)) File.Delete(FilePath); }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // Writes a run's log lines to its own file and returns the path.
    public static string? SaveLog(string name, IEnumerable<string> lines)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var path = Path.Combine(LogFolder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safe}.log");
            File.WriteAllLines(path, lines);
            return path;
        }
        catch { return null; }
    }
}
