namespace PgBackupManager.Core.Models;

// A SQL Server RESTORE DATABASE run. Target database name is whatever the
// original backup's DatabaseName was (read via MsSqlRestoreRunner.InspectAsync)
// unless the caller overrides it with TargetDatabaseOverride.
public sealed class MsSqlRestoreOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1433;
    public bool IntegratedSecurity { get; set; } = true;
    public string Username { get; set; } = "";

    public string BackupFile { get; set; } = "";

    // Database name to restore INTO. Callers resolve this up front (usually the
    // backup's own original DatabaseName from RESTORE HEADERONLY) rather than
    // MsSqlRestoreRunner guessing it mid-run.
    public string TargetDatabase { get; set; } = "";

    // Maps to WITH REPLACE. Required whenever TargetDatabase already exists —
    // SQL Server refuses to restore over an existing, differently-originated
    // database otherwise (see: the "backup set holds a backup of a database
    // other than the existing X database" error hit manually this session).
    public bool OverwriteExisting { get; set; }
}
