using System;
using System.Collections.Generic;

namespace PgBackupManager.Core.Models;

public enum BackupFormat { Custom, Plain, Directory, Tar }
public enum BackupScope { FullDatabase, SpecificSchemas, SpecificTables }
public enum DumpContent { Both, SchemaOnly, DataOnly }

public sealed class BackupJob
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";

    public BackupFormat Format { get; set; } = BackupFormat.Custom;
    public BackupScope Scope { get; set; } = BackupScope.FullDatabase;
    public DumpContent Content { get; set; } = DumpContent.Both;

    public List<string> IncludeSchemas { get; set; } = new();
    public List<string> IncludeTables { get; set; } = new();

    public string DestinationRoot { get; set; } = "";
    public bool UseAutoFolders { get; set; } = true;
    public string FileName { get; set; } = "";

    // pg_dump --jobs=N. Only meaningful with Directory format — pg_dump refuses
    // parallel workers with any other archive format, since only Directory
    // format lays tables out as separate files workers can write concurrently.
    public int Jobs { get; set; } = 1;

    // When Scope == SpecificSchemas and more than one schema is ticked, dump
    // each schema with its own pg_dump run into its own file instead of
    // combining them into one archive — all files still land in the same
    // destination folder (same day-folder, same timestamp).
    public bool SeparateFilePerSchema { get; set; }

    public string FullOutputPath { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public string FormatChar => Format switch
    {
        BackupFormat.Custom => "c",
        BackupFormat.Plain => "p",
        BackupFormat.Directory => "d",
        BackupFormat.Tar => "t",
        _ => "c"
    };

    public string Extension => Format switch
    {
        BackupFormat.Custom => "dump",
        BackupFormat.Plain => "sql",
        BackupFormat.Directory => "",
        BackupFormat.Tar => "tar",
        _ => "dump"
    };
}
