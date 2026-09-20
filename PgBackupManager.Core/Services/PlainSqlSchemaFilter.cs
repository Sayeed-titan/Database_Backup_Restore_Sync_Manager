using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

// Rewrites a plain-text SQL dump down to just the ticked schemas' objects, by
// re-using the exact same "-- Name: ...; Schema: ..." header comments
// PlainSqlInspector parses. Everything before the first header (SET
// statements, encoding/search_path preamble) is session setup, not tied to
// any one schema, and is always kept so the script still runs cleanly.
// Global objects (Schema: -, e.g. extensions) are dropped the same way
// pg_restore's own --schema filter drops them — CREATE SCHEMA statements for
// the ticked schemas are created separately via
// DatabaseAdmin.EnsureSchemasExistAsync, exactly like the pg_restore path.
public static class PlainSqlSchemaFilter
{
    private static readonly Regex HeaderRx = new(
        @"^-- (?:Data for )?Name:\s*.+?;\s*Type:\s*.+?;\s*Schema:\s*(?<schema>.+?);\s*Owner:\s*.+?\s*$",
        RegexOptions.Compiled);

    // pg_dump 17+ writes "SET transaction_timeout = 0;" into every plain-text
    // dump's preamble — a GUC that doesn't exist before PG17, so an older
    // target server (e.g. PG15) rejects it outright before a single object
    // restores. Dropping the line is always safe: it only ever resets a
    // session-level timeout, so skipping it changes nothing restore-relevant
    // on any server, old or new. Applied unconditionally (not just in
    // schema-filtered mode) since this sits in the always-kept preamble.
    private static readonly Regex IncompatiblePreambleRx = new(
        @"^SET\s+transaction_timeout\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // schemaTargets: ticked source schema name -> name to restore it as in the
    // target (same value as the key = no rename). Neither pg_restore nor psql
    // has a "restore this schema under a different name" flag, so a rename is
    // done by token-substituting the old schema name for the new one in every
    // kept line — safe here because "\bname\b" only matches the identifier as
    // a whole word, so it can't clip a longer name that happens to contain it
    // (e.g. ticking "live" never touches "live_archive"), and matches equally
    // well quoted ("live") or bare, since the quote itself is a non-word
    // boundary. The one real risk: a text/COPY data value that happens to
    // equal the schema name verbatim would also get rewritten — an accepted,
    // disclosed trade-off of this being a best-effort, non-TOC restore path.
    public static async Task<string> FilterToTempFileAsync(string sourceFile, IReadOnlyDictionary<string, string> schemaTargets, CancellationToken ct = default)
    {
        var included = new HashSet<string>(schemaTargets.Keys, StringComparer.OrdinalIgnoreCase);
        var renames = schemaTargets
            .Where(kv => !string.Equals(kv.Key, kv.Value, StringComparison.Ordinal))
            .Select(kv => (Pattern: new Regex(@"\b" + Regex.Escape(kv.Key) + @"\b", RegexOptions.IgnoreCase), kv.Value))
            .ToList();

        var tempFile = Path.Combine(Path.GetTempPath(), $"pgbm_plainsql_{Guid.NewGuid():N}.sql");

        using var reader = new StreamReader(sourceFile);
        await using var writer = new StreamWriter(tempFile, false);

        var hasSchemaFilter = schemaTargets.Count > 0;
        var keep = true; // preamble, before the first header, is always kept
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (IncompatiblePreambleRx.IsMatch(line)) continue;

            var m = HeaderRx.Match(line);
            if (m.Success && hasSchemaFilter)
            {
                var schema = m.Groups["schema"].Value.Trim();
                keep = schema != "-" && included.Contains(schema);
            }
            if (!keep) continue;

            var outLine = line;
            foreach (var (pattern, target) in renames)
                outLine = pattern.Replace(outLine, target);

            await writer.WriteLineAsync(outLine);
        }

        return tempFile;
    }
}
