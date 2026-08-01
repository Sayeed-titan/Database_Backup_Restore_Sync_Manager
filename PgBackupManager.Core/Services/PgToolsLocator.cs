using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PgBackupManager.Core.Services;

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
