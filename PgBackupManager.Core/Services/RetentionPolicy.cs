using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PgBackupManager.Core.Services;

public static class RetentionPolicy
{
    public static IReadOnlyList<FileInfo> FindExpired(string rootFolder, int retentionDays, DateTime now)
    {
        if (!Directory.Exists(rootFolder) || retentionDays <= 0) return Array.Empty<FileInfo>();
        var cutoff = now.AddDays(-retentionDays);
        var exts = new[] { ".dump", ".sql", ".tar", ".backup" };
        return Directory.EnumerateFiles(rootFolder, "*", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Where(f => exts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Where(f => f.LastWriteTime < cutoff)
            .OrderBy(f => f.LastWriteTime)
            .ToList();
    }

    public static int DeleteExpired(string rootFolder, int retentionDays, DateTime now)
    {
        var deleted = 0;
        foreach (var f in FindExpired(rootFolder, retentionDays, now))
        {
            try { f.Delete(); deleted++; } catch { }
        }
        return deleted;
    }
}
