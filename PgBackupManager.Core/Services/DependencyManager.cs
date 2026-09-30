using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

public sealed record ToolStatus(string Key, string Name, string Purpose, string? Path, bool Builtin, bool CanDownload, string? InfoUrl)
{
    public bool Found => Builtin || Path != null;
    public string StatusText => Builtin ? "built-in" : Path != null ? "found" : "not found";
}

// Everything the app can use beyond its own bundled .NET drivers: where it
// is on this machine (PATH, standard install roots, our own download folder)
// and — where the vendor publishes a stable "latest" URL — a one-click
// download that needs no admin rights (extracted under %LocalAppData%).
public static class DependencyManager
{
    public static string Root { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PgBackupManager");
    public static string OracleRoot => System.IO.Path.Combine(Root, "oracle");

    private static readonly string[] OracleZips =
    {
        "https://download.oracle.com/otn_software/nt/instantclient/instantclient-basic-windows.zip",
        "https://download.oracle.com/otn_software/nt/instantclient/instantclient-tools-windows.zip",
        "https://download.oracle.com/otn_software/nt/instantclient/instantclient-sqlplus-windows.zip",
    };

    public static List<ToolStatus> Scan(string? pgBinOverride = null)
    {
        var pg = PgToolsLocator.Locate(pgBinOverride);
        return new List<ToolStatus>
        {
            new("drivers", ".NET drivers (PostgreSQL, SQL Server, Oracle, MySQL/MariaDB, SQLite)", "Connections, SQL editor, transfer, import, compare", null, true, false, null),
            new("pg", $"PostgreSQL client tools{(pg.Version != null ? " " + pg.Version : "")}", "pg_dump / pg_restore / psql for PG backup & restore", pg.PgDump, false, true, "https://www.enterprisedb.com/download-postgresql-binaries"),
            new("oracle", "Oracle Instant Client (basic + tools + sqlplus)", "expdp / impdp (Data Pump .dmp backup & restore), sqlplus", Find("expdp.exe", OracleSearchDirs()), false, true, "https://www.oracle.com/database/technologies/instant-client/winx64-64-downloads.html"),
            new("mysql", "MySQL / MariaDB client (mysqldump)", "MySQL/MariaDB logical backups (restore works without it)", Find("mysqldump.exe", MySqlSearchDirs()), false, false, "https://dev.mysql.com/downloads/mysql/"),
            new("sqlcmd", "SQL Server sqlcmd", "optional — scripts run in-app without it", Find("sqlcmd.exe", SqlServerSearchDirs()), false, false, "https://learn.microsoft.com/sql/tools/sqlcmd/sqlcmd-utility"),
            new("localdb", "SQL Server Express LocalDB", "restore a .bak locally with no full SQL Server install", Find("SqlLocalDB.exe", SqlServerSearchDirs()), false, false, "https://learn.microsoft.com/sql/database-engine/configure-windows/sql-server-express-localdb"),
        };
    }

    public static string? Find(string exe, IEnumerable<string> extraDirs)
    {
        var dirs = extraDirs.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var d in dirs)
        {
            try
            {
                var p = System.IO.Path.Combine(d.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    public static IEnumerable<string> OracleSearchDirs()
    {
        if (Directory.Exists(OracleRoot))
            foreach (var d in Directory.GetDirectories(OracleRoot, "instantclient*").OrderByDescending(x => x)) yield return d;
        var home = Environment.GetEnvironmentVariable("ORACLE_HOME");
        if (!string.IsNullOrEmpty(home)) yield return System.IO.Path.Combine(home, "bin");
    }

    private static IEnumerable<string> MySqlSearchDirs()
    {
        foreach (var root in new[] { @"C:\Program Files\MySQL", @"C:\Program Files\MariaDB", @"C:\Program Files (x86)\MySQL" })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(x => x)) yield return System.IO.Path.Combine(d, "bin");
        }
        foreach (var d in Directory.Exists(@"C:\Program Files") ? Directory.GetDirectories(@"C:\Program Files", "MariaDB*") : Array.Empty<string>())
            yield return System.IO.Path.Combine(d, "bin");
    }

    private static IEnumerable<string> SqlServerSearchDirs()
    {
        foreach (var root in new[] { @"C:\Program Files\Microsoft SQL Server", @"C:\Program Files (x86)\Microsoft SQL Server" })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(x => x))
            {
                yield return System.IO.Path.Combine(d, "Tools", "Binn");
                yield return System.IO.Path.Combine(d, "Tools", "Binn", "");
            }
            var odbc = System.IO.Path.Combine(root, "Client SDK", "ODBC");
            if (Directory.Exists(odbc))
                foreach (var d in Directory.GetDirectories(odbc)) yield return System.IO.Path.Combine(d, "Tools", "Binn");
        }
    }

    // Downloads the three Instant Client zips and extracts them into one
    // instantclient_XX_Y folder (they're designed to be unzipped together).
    public static async Task<string> DownloadOracleInstantClientAsync(IProgress<(string Stage, double Percent)>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(OracleRoot);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PgBackupManager");
        string? target = null;
        for (int i = 0; i < OracleZips.Length; i++)
        {
            var url = OracleZips[i];
            var name = System.IO.Path.GetFileName(url);
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pgbm_" + name);
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                long read = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) progress?.Report(($"Downloading {name}", (i + (double)read / total) / OracleZips.Length * 100));
                }
            }
            progress?.Report(($"Extracting {name}", (i + 1.0) / OracleZips.Length * 100));
            using (var zip = ZipFile.OpenRead(tmp))
            {
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(OracleRoot, e.FullName));
                    if (!dest.StartsWith(System.IO.Path.GetFullPath(OracleRoot), StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: true);
                    target ??= System.IO.Path.Combine(OracleRoot, e.FullName.Split('/')[0]);
                }
            }
            try { File.Delete(tmp); } catch { }
        }
        return target ?? OracleRoot;
    }
}
