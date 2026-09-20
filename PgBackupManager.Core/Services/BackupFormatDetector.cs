using System.IO;
using System.Text;

namespace PgBackupManager.Core.Services;

public enum RestoreFileFormat
{
    Custom,
    Directory,
    Tar,
    PlainSql,
    Unknown,
}

// pg_restore can only read the custom/directory/tar archive formats — a plain
// `pg_dump --format=plain` (or hand-written) .sql file has no TOC at all, so
// `pg_restore --list` fails immediately (BackupInspector surfaces that as an
// exception). Detecting the format up front lets the Restore screen route
// plain-text SQL through psql instead of dead-ending on that error.
public static class BackupFormatDetector
{
    public static RestoreFileFormat Detect(string path)
    {
        if (Directory.Exists(path))
            return File.Exists(Path.Combine(path, "toc.dat")) ? RestoreFileFormat.Directory : RestoreFileFormat.Unknown;

        if (!File.Exists(path)) return RestoreFileFormat.Unknown;

        using var fs = File.OpenRead(path);

        // Custom-format archives start with the "PGDMP" magic signature.
        var header = new byte[5];
        if (fs.Read(header, 0, 5) == 5 && Encoding.ASCII.GetString(header) == "PGDMP")
            return RestoreFileFormat.Custom;

        // Tar-format archives carry the POSIX ustar magic at offset 257.
        if (fs.Length > 262)
        {
            fs.Seek(257, SeekOrigin.Begin);
            var tarMagic = new byte[5];
            if (fs.Read(tarMagic, 0, 5) == 5 && Encoding.ASCII.GetString(tarMagic) == "ustar")
                return RestoreFileFormat.Tar;
        }

        // Neither binary signature matched — treat as a plain-text SQL script.
        return RestoreFileFormat.PlainSql;
    }
}
