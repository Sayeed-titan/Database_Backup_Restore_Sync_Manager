using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public sealed record SizeEstimate(IReadOnlyDictionary<string, long> BytesByTable, long TotalBytes);

// Gives pg_dump progress tracking something real to weigh against: table row
// counts vary wildly (a lookup table next to a 200k-row history table), so
// counting "tables done" is a poor time proxy — on-disk bytes track wall-clock
// time much better. Keys are "schema.table" built the exact same way pg_dump's
// own verbose "dumping contents of table \"schema.table\"" message is (a plain
// dot-join), so a schema name that itself contains a dot still matches byte
// for byte without needing to split it back apart.
public static class TableSizeEstimator
{
    public static async Task<SizeEstimate> EstimateAsync(string connectionString, BackupJob job, CancellationToken ct = default)
    {
        var byTable = new Dictionary<string, long>(StringComparer.Ordinal);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        string sql;
        NpgsqlParameter[] parameters;
        if (job.Scope == BackupScope.FullDatabase)
        {
            sql = @"SELECT n.nspname, c.relname, pg_total_relation_size(c.oid)
                    FROM pg_class c JOIN pg_namespace n ON c.relnamespace = n.oid
                    WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog','information_schema','pg_toast')";
            parameters = Array.Empty<NpgsqlParameter>();
        }
        else
        {
            // SpecificTables scope still needs every table in the involved
            // schemas fetched once, then we filter down to the ticked ones in
            // C# below — simpler than parameterizing an array of (schema,table)
            // pairs, and the query itself is cheap (catalog + file stats only).
            var schemas = job.Scope == BackupScope.SpecificSchemas
                ? job.IncludeSchemas
                : job.IncludeTables.Select(SchemaOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (schemas.Count == 0) return new SizeEstimate(byTable, 0);

            sql = @"SELECT n.nspname, c.relname, pg_total_relation_size(c.oid)
                    FROM pg_class c JOIN pg_namespace n ON c.relnamespace = n.oid
                    WHERE c.relkind = 'r' AND n.nspname = ANY(@schemas)";
            parameters = new[] { new NpgsqlParameter("schemas", schemas.ToArray()) };
        }

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
            byTable[key] = reader.GetInt64(2);
        }

        if (job.Scope == BackupScope.SpecificTables)
        {
            var wanted = new HashSet<string>(job.IncludeTables, StringComparer.Ordinal);
            byTable = byTable.Where(kv => wanted.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        return new SizeEstimate(byTable, byTable.Values.Sum());
    }

    // "schema.table" -> "schema", splitting on the LAST dot so a schema name
    // that itself contains a dot (e.g. "singularity.membership") stays intact.
    private static string SchemaOf(string qualifiedName)
    {
        var idx = qualifiedName.LastIndexOf('.');
        return idx < 0 ? qualifiedName : qualifiedName[..idx];
    }
}
