using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

// A plain-text SQL dump has no binary TOC, but pg_dump still writes a stable
// "-- Name: X; Type: Y; Schema: Z; Owner: W" comment immediately before every
// object it emits (and "-- Data for Name: ..." before each table's COPY
// block) — the same metadata pg_restore's binary TOC carries, just spelled
// out as a text comment instead of a listing row. Scanning for these lines
// gives BackupSchemas/the diff table the same picture BackupInspector gets
// from `pg_restore --list` on a real archive, without needing pg_restore to
// be able to read the file at all.
public static class PlainSqlInspector
{
    private static readonly Regex HeaderRx = new(
        @"^-- (?:Data for )?Name:\s*(?<name>.+?);\s*Type:\s*(?<type>.+?);\s*Schema:\s*(?<schema>.+?);\s*Owner:\s*(?<owner>.+?)\s*$",
        RegexOptions.Compiled);

    public static async Task<IReadOnlyList<TocEntry>> InspectAsync(string sqlFile, CancellationToken ct = default)
    {
        var list = new List<TocEntry>();
        var id = 0;
        using var reader = new StreamReader(sqlFile);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            var m = HeaderRx.Match(line);
            if (!m.Success) continue;

            var schema = m.Groups["schema"].Value.Trim();
            // "-" means the object isn't schema-scoped (extensions, database-level
            // ACLs/comments, the CREATE SCHEMA statement itself) — nothing a
            // per-schema restore can pick or drop, so it can't drive the picker.
            if (schema == "-") continue;

            list.Add(new TocEntry(id++, 0, 0,
                m.Groups["type"].Value.Trim(), schema, m.Groups["name"].Value.Trim(), m.Groups["owner"].Value.Trim(), line));
        }
        return list;
    }
}
