namespace PgBackupManager.Core.Models;

public sealed class AppSettings
{
    public string? PgBinDirOverride { get; set; }
    public string DefaultBackupRoot { get; set; } = @"D:\Backups";
    public string DefaultRestoreSource { get; set; } = @"D:\Backups";
    public bool UseAutoFolders { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public string Theme { get; set; } = "Light.Blue";

    // Completion notifications (snackbar-style toast + taskbar flash) for
    // backup/restore. Toast shows regardless of whether the main window is
    // minimized — it's an independent top-level window, not a child of it.
    public bool NotifyOnCompletion { get; set; } = true;
    public int NotificationDurationSeconds { get; set; } = 6;
    public bool FlashTaskbarOnCompletion { get; set; } = true;
}
