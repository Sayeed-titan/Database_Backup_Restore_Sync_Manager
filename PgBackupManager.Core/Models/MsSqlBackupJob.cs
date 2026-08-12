using System;

namespace PgBackupManager.Core.Models;

// A SQL Server BACKUP DATABASE run. Deliberately much smaller than BackupJob —
// there is no pg_dump-style Format/Scope/Content/Jobs here: BACKUP DATABASE is
// always a full, single-file backup of the whole database.
public sealed class MsSqlBackupJob
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1433;
    public string Database { get; set; } = "";
    public bool IntegratedSecurity { get; set; } = true;
    public string Username { get; set; } = "";

    public string DestinationRoot { get; set; } = "";
    public bool UseAutoFolders { get; set; } = true;

    // Resolved by the caller (e.g. FilenameBuilder.BuildFolder + a timestamp)
    // before RunAsync — same "always a new file" convention pg_dump backups use.
    public string FullOutputPath { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
