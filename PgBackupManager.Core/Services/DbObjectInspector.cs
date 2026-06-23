using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public static class DbObjectInspector
{
    private const string Sql = @"
SELECT 'schema'::text AS kind, schema_name AS schema, NULL::text AS name, NULL::text AS signature
FROM information_schema.schemata
WHERE schema_name NOT IN ('pg_catalog','information_schema','pg_toast')
UNION ALL
SELECT 'table', table_schema, table_name, NULL
FROM information_schema.tables
WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema')
UNION ALL
SELECT 'view', table_schema, table_name, NULL
FROM information_schema.views
WHERE table_schema NOT IN ('pg_catalog','information_schema')
UNION ALL
SELECT 'function', n.nspname, p.proname, pg_get_function_identity_arguments(p.oid)
FROM pg_proc p
JOIN pg_namespace n ON p.pronamespace=n.oid
WHERE n.nspname NOT IN ('pg_catalog','information_schema')
  AND p.prokind <> 'a'
UNION ALL
SELECT 'type', n.nspname, t.typname, NULL
FROM pg_type t
JOIN pg_namespace n ON t.typnamespace=n.oid
WHERE t.typtype IN ('c','e','d')
  AND n.nspname NOT IN ('pg_catalog','information_schema')
  AND NOT EXISTS (SELECT 1 FROM pg_class c WHERE c.reltype=t.oid AND c.relkind <> 'c')
UNION ALL
SELECT 'sequence', sequence_schema, sequence_name, NULL
FROM information_schema.sequences
WHERE sequence_schema NOT IN ('pg_catalog','information_schema')
ORDER BY 1, 2 NULLS FIRST, 3 NULLS FIRST;
";

    public static async Task<IReadOnlyList<DbObject>> InspectAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DbObject>();
        while (await reader.ReadAsync(ct))
        {
            var kindStr = reader.GetString(0);
            var schema = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var name = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var sig = reader.IsDBNull(3) ? null : reader.GetString(3);
            var kind = kindStr switch
            {
                "schema" => DbObjectKind.Schema,
                "table" => DbObjectKind.Table,
                "view" => DbObjectKind.View,
                "function" => DbObjectKind.Function,
                "type" => DbObjectKind.Type,
                "sequence" => DbObjectKind.Sequence,
                _ => DbObjectKind.Table
            };
            if (kind == DbObjectKind.Schema)
                list.Add(new DbObject(schema, schema, kind, null));
            else
                list.Add(new DbObject(schema, name, kind, sig));
        }
        return list;
    }
}
