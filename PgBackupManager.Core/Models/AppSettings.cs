namespace PgBackupManager.Core.Models;

public sealed class AppSettings
{
    public string? PgBinDirOverride { get; set; }
    public string DefaultBackupRoot { get; set; } = @"D:\Backups";
    public string DefaultRestoreSource { get; set; } = @"D:\Backups";
    public bool UseAutoFolders { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public string Theme { get; set; } = "Light.Blue";
}
