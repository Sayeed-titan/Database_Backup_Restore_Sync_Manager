using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PgBackupManager.Core.Services;

namespace PgBackupManager.UI.Views;

// Downloads + installs a matching-major-version PostgreSQL client tools set
// via PgToolsDownloader, with a live progress bar. Opened as a nested modal
// from ConfirmDialog.ShowVersionMismatch when no already-installed version
// matches the target server.
public partial class PgToolsDownloadDialog : Window
{
    public bool Result { get; private set; }
    public string? InstalledBinDir { get; private set; }

    private readonly int _majorVersion;
    private CancellationTokenSource? _cts;
    // True once the download+extract has actually finished — a second click
    // on StartBtn (now relabelled "CONTINUE") just closes the dialog instead
    // of starting another download. Without this the dialog previously
    // closed itself the instant the download finished, with nothing on
    // screen confirming it had actually worked.
    private bool _downloadComplete;

    public PgToolsDownloadDialog(int majorVersion)
    {
        InitializeComponent();
        _majorVersion = majorVersion;

        TitleText.Text = $"Download PostgreSQL {majorVersion} Client Tools";
        MessageText.Text =
            $"Downloads PostgreSQL {majorVersion}'s official Windows binaries from EnterpriseDB " +
            "(the packager postgresql.org itself points to) — about 330 MB temporarily, but only the " +
            "~70 MB command-line tools (pg_dump, pg_restore, psql) are kept afterward; everything else " +
            "in that archive is discarded. No install or admin rights needed — it's unpacked straight " +
            "into your local app data folder.";
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        // Second click, after a successful download — just close and hand
        // InstalledBinDir back to the caller.
        if (_downloadComplete)
        {
            Result = true;
            Close();
            return;
        }

        StartBtn.IsEnabled = false;
        CloseXBtn.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();

        var progress = new Progress<PgToolsDownloader.DownloadProgress>(p =>
        {
            PhaseText.Text = p.Phase;
            if (p.BytesTotal > 0)
            {
                ProgressBarCtl.Value = Math.Clamp((double)p.BytesDone / p.BytesTotal * 100, 0, 100);
                SizeText.Text = $"{p.BytesDone / 1024.0 / 1024.0:F0} / {p.BytesTotal / 1024.0 / 1024.0:F0} MB";
            }
        });

        try
        {
            InstalledBinDir = await PgToolsDownloader.DownloadAndInstallAsync(_majorVersion, progress, _cts.Token);

            _downloadComplete = true;
            PhaseText.Text = $"✓ Installed — {InstalledBinDir}";
            SizeText.Text = "";
            ProgressBarCtl.Value = 100;
            StartBtnText.Text = "CONTINUE";
            StartBtn.IsEnabled = true;
            CloseXBtn.IsEnabled = true;
            CancelBtn.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            PhaseText.Text = "Cancelled.";
            StartBtn.IsEnabled = true;
            CloseXBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            PhaseText.Text = $"ERROR: {ex.Message}";
            StartBtn.IsEnabled = true;
            CloseXBtn.IsEnabled = true;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            return;
        }
        Result = false;
        Close();
    }
}
