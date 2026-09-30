using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Services;

public enum PresetKind { Transfer, Backup, Script }

public sealed class TransferPreset
{
    public Guid SourceProfileId { get; set; }
    public Guid TargetProfileId { get; set; }
    public string SourceSchema { get; set; } = "";
    public string TargetSchema { get; set; } = "";
    // Empty = every table in the source schema at run time.
    public List<string> Tables { get; set; } = new();
    public List<DbObjectInfo> CodeObjects { get; set; } = new();
    public TableLoadMode Mode { get; set; } = TableLoadMode.CreateOrAppend;
    public NameCase NameCase { get; set; } = NameCase.TargetDefault;
    public bool ApplyCode { get; set; }
    public string? RowFilter { get; set; }
    public long CommitEveryRows { get; set; }
}

public sealed class BackupPreset
{
    public Guid ProfileId { get; set; }
    public BackupFormat Format { get; set; } = BackupFormat.Custom;
    public BackupScope Scope { get; set; } = BackupScope.FullDatabase;
    public DumpContent Content { get; set; } = DumpContent.Both;
    public List<string> Schemas { get; set; } = new();
    public List<string> Tables { get; set; } = new();
    public int Jobs { get; set; } = 1;
    // Null = Settings' default backup root.
    public string? DestinationRoot { get; set; }
    public bool UseAutoFolders { get; set; } = true;
    public bool ApplyRetention { get; set; }
}

public sealed class ScriptPreset
{
    public Guid ProfileId { get; set; }
    public string ScriptPath { get; set; } = "";
    public bool StopOnError { get; set; } = true;
}

public sealed class JobPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public PresetKind Kind { get; set; }
    public TransferPreset? Transfer { get; set; }
    public BackupPreset? Backup { get; set; }
    public ScriptPreset? Script { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string Label => $"{Name}  ({Kind})";
    public override string ToString() => Name;
}

// Named, reusable job definitions — what the Scheduler and the headless CLI
// (--run-preset) execute, and what Transfer/Backup can save & reload.
public static class PresetStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PgBackupManager", "presets.json");

    public static event EventHandler? Changed;

    public static List<JobPreset> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<List<JobPreset>>(File.ReadAllText(FilePath), Opts) ?? new();
        }
        catch { return new(); }
    }

    public static JobPreset? Find(string idOrName)
    {
        var all = LoadAll();
        return Guid.TryParse(idOrName, out var id)
            ? all.FirstOrDefault(p => p.Id == id)
            : all.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public static void Upsert(JobPreset p)
    {
        var all = LoadAll();
        var existing = all.FindIndex(x => x.Id == p.Id || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        p.UpdatedUtc = DateTime.UtcNow;
        if (existing >= 0) { p.Id = all[existing].Id; all[existing] = p; } else all.Add(p);
        Save(all);
    }

    public static void Delete(Guid id)
    {
        var all = LoadAll();
        all.RemoveAll(p => p.Id == id);
        Save(all);
    }

    private static void Save(List<JobPreset> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all.OrderBy(p => p.Name).ToList(), Opts));
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
