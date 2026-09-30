namespace PgBackupManager.Core.Models;

public sealed class AppSettings
{
    public string? PgBinDirOverride { get; set; }
    public string DefaultBackupRoot { get; set; } = @"D:\Backups";
    public string DefaultRestoreSource { get; set; } = @"D:\Backups";
    public bool UseAutoFolders { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    // Legacy, never read — kept only so older settings.json files still round-trip.
    public string Theme { get; set; } = "Light.Blue";

    // "Light" | "Dark" | "System" (follows Windows' app theme).
    public string ThemeMode { get; set; } = "Light";
    // Accent palette name — see ThemeService.Accents.
    public string AccentName { get; set; } = "Teal";

    // SQL editor preferences.
    public double EditorFontSize { get; set; } = 13;
    public int EditorMaxRows { get; set; } = 5000;

    // Sidebar page to reopen on next launch.
    public string? LastPage { get; set; }

    // Completion notifications (snackbar-style toast + taskbar flash) for
    // backup/restore. Toast shows regardless of whether the main window is
    // minimized — it's an independent top-level window, not a child of it.
    public bool NotifyOnCompletion { get; set; } = true;
    public int NotificationDurationSeconds { get; set; } = 6;
    public bool FlashTaskbarOnCompletion { get; set; } = true;
}
