using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PgBackupManager.Core.Services;

// One complete, usable PostgreSQL client-tools install found on this machine —
// powers the "quick switch" pickers in Settings and the version-mismatch
// dialog, so switching between e.g. a PG15 and a PG18 install is a single
// click instead of re-typing/browsing a folder path.
public sealed record PgInstall(string BinDir, string Version, int? MajorVersion)
{
    public string Label => $"PostgreSQL {Version}  —  {BinDir}";
}

public sealed record PgToolPaths
{
    public string? PgDump { get; init; }
    public string? PgRestore { get; init; }
    public string? Psql { get; init; }
    public string? Version { get; init; }
    public string? InstallRoot { get; init; }

    public bool IsComplete => !string.IsNullOrEmpty(PgDump)
        && !string.IsNullOrEmpty(PgRestore)
        && !string.IsNullOrEmpty(Psql);

    // First path segment of Version ("15.18" -> 15), used to compare against
    // a target server's major version. Null if Version couldn't be detected.
    public int? MajorVersion => int.TryParse((Version ?? "").Split('.')[0], out var v) ? v : null;
}

public static class PgToolsLocator
{
    private static readonly string[] CommonRoots =
    {
        @"C:\Program Files\PostgreSQL",
        @"C:\Program Files (x86)\PostgreSQL",
        // Where PgToolsDownloader installs a version fetched in-app (no admin
        // rights needed, so it can't land under Program Files) — same
        // "<root>\<version>\bin" layout as the two roots above, so it's
        // picked up by every scan here with no other changes.
        PgToolsDownloader.InstallRoot,
    };

    public static PgToolPaths Locate(string? manualBinDir = null)
    {
        if (!string.IsNullOrWhiteSpace(manualBinDir) && Directory.Exists(manualBinDir))
        {
            var paths = BuildFromBin(manualBinDir);
            if (paths.IsComplete) return WithDetectedVersion(paths);
        }

        foreach (var root in CommonRoots)
        {
            if (!Directory.Exists(root)) continue;

            var versionDirs = Directory.GetDirectories(root)
                .Select(d => new { Path = d, Version = Path.GetFileName(d) })
                .OrderByDescending(d => int.TryParse(d.Version, out var v) ? v : -1);

            foreach (var v in versionDirs)
            {
                var bin = Path.Combine(v.Path, "bin");
                if (!Directory.Exists(bin)) continue;
                var paths = BuildFromBin(bin) with { InstallRoot = v.Path };
                if (paths.IsComplete) return WithDetectedVersion(paths);
            }
        }

        var fromPath = BuildFromPath();
        return fromPath.IsComplete ? WithDetectedVersion(fromPath) : fromPath;
    }

    private static PgToolPaths BuildFromBin(string binDir) => new()
    {
        PgDump = ExistsOrNull(Path.Combine(binDir, "pg_dump.exe")),
        PgRestore = ExistsOrNull(Path.Combine(binDir, "pg_restore.exe")),
        Psql = ExistsOrNull(Path.Combine(binDir, "psql.exe")),
        InstallRoot = Path.GetDirectoryName(binDir),
    };

    private static PgToolPaths BuildFromPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathEnv.Split(Path.PathSeparator).Where(Directory.Exists);
        string? Find(string exe) => dirs
            .Select(d => Path.Combine(d, exe))
            .FirstOrDefault(File.Exists);

        return new PgToolPaths
        {
            PgDump = Find("pg_dump.exe"),
            PgRestore = Find("pg_restore.exe"),
            Psql = Find("psql.exe"),
        };
    }

    private static string? ExistsOrNull(string p) => File.Exists(p) ? p : null;

    // Scans the standard install roots for every COMPLETE PostgreSQL client
    // install on this machine, plus whatever bin folder is currently
    // configured (which may live outside those roots entirely, like a
    // hand-placed client-tools-only copy) so it never disappears from the
    // picker just because it's not under "Program Files".
    public static List<PgInstall> DetectAllInstalls(string? currentBinDir = null)
    {
        var results = new List<PgInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string binDir)
        {
            if (!Directory.Exists(binDir)) return;
            var full = Path.GetFullPath(binDir);
            if (!seen.Add(full)) return;

            var paths = BuildFromBin(full);
            if (!paths.IsComplete) return;

            var version = DetectVersion(paths.PgRestore) ?? "unknown";
            var major = int.TryParse(version.Split('.')[0], out var m) ? m : (int?)null;
            results.Add(new PgInstall(full, version, major));
        }

        foreach (var root in CommonRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
                TryAdd(Path.Combine(dir, "bin"));
        }

        if (!string.IsNullOrWhiteSpace(currentBinDir)) TryAdd(currentBinDir);

        return results.OrderByDescending(r => r.MajorVersion ?? -1).ToList();
    }

    // Runs the resolved pg_restore.exe --version rather than trusting the
    // enclosing folder name — the folder name is unavailable at all when the
    // tools came from a manual Bin Folder Override or the PATH, and even under
    // "C:\Program Files\PostgreSQL\<N>" it's only ever the major version, not
    // the exact build (e.g. "15" vs the real "15.18") that a version-mismatch
    // check against a live server needs to be precise.
    private static PgToolPaths WithDetectedVersion(PgToolPaths paths)
        => paths with { Version = DetectVersion(paths.PgRestore) };

    private static readonly Regex VersionRx = new(@"(\d+(?:\.\d+)+)", RegexOptions.Compiled);

    private static string? DetectVersion(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exePath, "--version")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            var m = VersionRx.Match(output);
            return m.Success ? m.Value : null;
        }
        catch
        {
            return null;
        }
    }
}
