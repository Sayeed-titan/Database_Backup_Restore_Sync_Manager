using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Views;

namespace PgBackupManager.UI.Services;

// Ties UpdateChecker's result to the app's own dialogs. "Notify + open
// download link" by design — we never download or run anything ourselves;
// opening the release page and letting the user run the installer manually
// sidesteps all the ways a self-replacing running .exe can go wrong (locked
// files, AV flags on a silent installer, etc.). Inno Setup upgrades the
// existing install in place (same AppId) once they run it.
public static class UpdateService
{
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <param name="silent">
    /// True for the automatic startup check — stays completely quiet unless an
    /// update is actually found. False for the manual "Check for Updates"
    /// button — always reports back, including "you're up to date" or errors,
    /// since the user explicitly asked and expects an answer either way.
    /// </param>
    public static async Task CheckAsync(bool silent)
    {
        var result = await UpdateChecker.CheckAsync(CurrentVersion);
        var current = $"v{CurrentVersion.Major}.{CurrentVersion.Minor}";

        if (result.IsAvailable)
        {
            var proceed = ConfirmDialog.Confirm(
                Application.Current?.MainWindow,
                "Update available",
                $"A newer version is available: v{result.LatestVersion} (you have {current}).\n\n" +
                "Open the release page to download it? The installer upgrades your existing install in place — nothing else to configure.",
                confirmText: "Open download page");
            if (proceed && result.ReleaseUrl != null)
                Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true });
            return;
        }

        if (silent) return;

        var failed = result.LatestVersion is null;
        ConfirmDialog.Alert(
            Application.Current?.MainWindow,
            failed ? "Couldn't check for updates" : "You're up to date",
            failed
                ? "Couldn't reach GitHub to check for a newer version — check your internet connection and try again."
                : $"You're running the latest version ({current}).",
            danger: failed);
    }
}
