using System;
using System.IO;
using System.Text.Json;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    public string FilePath { get; }

    public SettingsStore(string? overridePath = null)
    {
        var dir = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PgBackupManager");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        File.WriteAllText(FilePath, json);
    }
}
