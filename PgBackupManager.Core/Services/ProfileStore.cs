using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string FilePath { get; }

    public ProfileStore(string? overridePath = null)
    {
        var dir = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PgBackupManager");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "profiles.json");
    }

    public List<ConnectionProfile> LoadAll()
    {
        if (!File.Exists(FilePath)) return new();
        var json = File.ReadAllText(FilePath);
        if (string.IsNullOrWhiteSpace(json)) return new();
        return JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? new();
    }

    public void SaveAll(IEnumerable<ConnectionProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles.ToList(), JsonOpts);
        File.WriteAllText(FilePath, json);
    }

    public void Upsert(ConnectionProfile profile)
    {
        var list = LoadAll();
        var existing = list.FirstOrDefault(p => p.Id == profile.Id);
        profile.UpdatedUtc = DateTime.UtcNow;
        if (existing is null) list.Add(profile);
        else
        {
            list.Remove(existing);
            list.Add(profile);
        }
        SaveAll(list);
    }

    public void Delete(Guid id)
    {
        var list = LoadAll();
        list.RemoveAll(p => p.Id == id);
        SaveAll(list);
    }
}
