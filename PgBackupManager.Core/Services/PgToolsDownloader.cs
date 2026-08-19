using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

// Fetches a matching-major-version PostgreSQL client tools set (pg_dump,
// pg_restore, psql) straight from EnterpriseDB's official Windows binaries,
// for when PgToolsLocator can't find one already installed. No admin rights
// or installer needed — everything lands under %LocalAppData%.
public static class PgToolsDownloader
{
    // EDB's per-major-version "latest binaries" file id, from
    // https://www.enterprisedb.com/download-postgresql-binaries — each id
    // always resolves (via a redirect) to that major version's CURRENT
    // latest patch release, so this table does NOT need updating for patch
    // releases — only when EDB adds a new major (~yearly) or retires an EOL
    // one. Verified working 2026-08-19.
    private static readonly Dictionary<int, long> MajorVersionFileIds = new()
    {
        [13] = 1259854,
        [14] = 1260406,
        [15] = 1260415,
        [16] = 1260422,
        [17] = 1260427,
        [18] = 1260435,
    };

    public static IReadOnlyList<int> SupportedMajorVersions { get; } =
        MajorVersionFileIds.Keys.OrderByDescending(v => v).ToList();

    public static bool IsVersionSupported(int majorVersion) => MajorVersionFileIds.ContainsKey(majorVersion);

    public static string InstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PgBackupManager", "PgTools");

    public sealed record DownloadProgress(long BytesDone, long BytesTotal, string Phase);

    // Returns the resulting bin folder (ready to hand straight to
    // AppSettings.PgBinDirOverride / PgToolsLocator).
    public static async Task<string> DownloadAndInstallAsync(
        int majorVersion, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (!MajorVersionFileIds.TryGetValue(majorVersion, out var fileId))
            throw new NotSupportedException(
                $"No known download for PostgreSQL {majorVersion} — supported majors: {string.Join(", ", SupportedMajorVersions)}.");

        var destBin = Path.Combine(InstallRoot, majorVersion.ToString(), "bin");
        Directory.CreateDirectory(destBin);

        var tempZip = Path.Combine(Path.GetTempPath(), $"pgtools_{majorVersion}_{Guid.NewGuid():N}.zip");
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PgBackupManager-ToolsDownloader");

            progress?.Report(new DownloadProgress(0, 0, "Connecting..."));

            using var resp = await http.GetAsync(
                $"https://sbp.enterprisedb.com/getfile.jsp?fileid={fileId}",
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? 0;

            await using (var httpStream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(new DownloadProgress(done, total, "Downloading..."));
                }
            }

            progress?.Report(new DownloadProgress(total, total, "Extracting client tools..."));
            ExtractBinOnly(tempZip, destBin, ct);

            progress?.Report(new DownloadProgress(total, total, "Done."));
            return destBin;
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { /* best effort cleanup */ }
            }
        }
    }

    // Extracts ONLY pgsql/bin/* from the archive. The ZIP's full payload
    // (server binaries, headers, docs, and a bundled pgAdmin4 + Python
    // distribution) blows past the Windows MAX_PATH limit when extracted
    // whole — confirmed empirically: a pgAdmin4\python\Lib\site-packages\
    // azure\...\ path aborts a full extraction with
    // DirectoryNotFoundException. bin/ alone is flat, ~70MB regardless of
    // major version, and is the only thing this app ever uses.
    private static void ExtractBinOnly(string zipPath, string destBin, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith("pgsql/bin/", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry, nothing to extract

            var target = Path.Combine(destBin, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
