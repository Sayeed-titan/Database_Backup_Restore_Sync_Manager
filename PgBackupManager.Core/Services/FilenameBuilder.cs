using System;
using System.IO;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public static class FilenameBuilder
{
    public static string BuildFolder(string root, string database, bool useAutoFolders, DateTime now)
    {
        if (!useAutoFolders) return root;
        return Path.Combine(root,
            database,
            now.Year.ToString("0000"),
            now.Month.ToString("00"),
            now.Day.ToString("00"));
    }

    public static string BuildFileName(BackupJob job, DateTime now)
    {
        var scope = job.Scope switch
        {
            BackupScope.FullDatabase => "full",
            BackupScope.SpecificSchemas => "schemas",
            BackupScope.SpecificTables => "tables",
            _ => "backup"
        };
        var content = job.Content switch
        {
            DumpContent.SchemaOnly => "_schemaonly",
            DumpContent.DataOnly => "_dataonly",
            _ => ""
        };
        var stamp = now.ToString("yyyyMMdd_HHmmss");
        var ext = string.IsNullOrEmpty(job.Extension) ? "" : "." + job.Extension;
        return $"{job.Database}_{scope}{content}_{stamp}{ext}";
    }

    public static string BuildFullPath(BackupJob job, DateTime now)
    {
        var folder = BuildFolder(job.DestinationRoot, job.Database, job.UseAutoFolders, now);
        var name = BuildFileName(job, now);
        return Path.Combine(folder, name);
    }
}
