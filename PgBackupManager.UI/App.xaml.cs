using System;
using System.Linq;
using System.Windows;
using PgBackupManager.Core.Services;
using PgBackupManager.UI.Services;

namespace PgBackupManager.UI;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless mode for Windows Task Scheduler / scripts:
        //   PgBackupManager.exe --run-preset "<name or id>"
        // No window; the result lands in History with a log file.
        // Exit code: 0 = success, 1 = job failed, 2 = preset not found.
        var idx = Array.FindIndex(e.Args, a => a.Equals("--run-preset", StringComparison.OrdinalIgnoreCase) || a.Equals("/run-preset", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var key = idx + 1 < e.Args.Length ? e.Args[idx + 1] : "";
            var preset = PresetStore.Find(key);
            int code;
            if (preset == null)
            {
                JobHistory.Add("Job", $"--run-preset {key}", $"preset '{key}' not found", false, scheduled: true);
                code = 2;
            }
            else
            {
                var (ok, _) = await HeadlessJobRunner.RunAsync(preset, scheduled: true);
                code = ok ? 0 : 1;
            }
            Shutdown(code);
            return;
        }

        // Keep the app alive through unexpected UI exceptions — show them instead of crashing.
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            MessageBox.Show(ex.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        // Before MainWindow is created, so the first paint is already themed.
        ThemeService.ApplySaved();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Files passed on the command line (e.g. "Open with") open in the SQL editor.
        foreach (var f in e.Args.Where(a => System.IO.File.Exists(a)))
            Navigator.OpenSql(System.IO.Path.GetFileName(f), System.IO.File.ReadAllText(f));
    }
}
