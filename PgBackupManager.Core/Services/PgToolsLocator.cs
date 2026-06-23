using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            if (paths.IsComplete) return paths;
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
                var paths = BuildFromBin(bin) with { Version = v.Version, InstallRoot = v.Path };
                if (paths.IsComplete) return paths;
            }
        }

        return BuildFromPath();
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
}
