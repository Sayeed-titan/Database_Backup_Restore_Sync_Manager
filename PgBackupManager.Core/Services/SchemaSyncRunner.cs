using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public enum DataSyncMode { FullRefresh, Upsert, Mirror }

public sealed class SyncOptions
{
    public required ConnectionProfile SourceProfile { get; init; }
    public required string SourcePassword { get; init; }
    public required ConnectionProfile TargetProfile { get; init; }
    public required string TargetPassword { get; init; }
    public required string SourceSchema { get; init; }
    public required string TargetSchema { get; init; }

    public bool SyncSchema { get; init; } = true;
    public bool SyncData { get; init; } = true;
    public DataSyncMode DataMode { get; init; } = DataSyncMode.Upsert;

    // Table names (no schema prefix).
    public IReadOnlyList<string> Tables { get; init; } = Array.Empty<string>();
    // "name(identity_args)" — matches DbObject.DisplayName for functions/procedures.
    public IReadOnlyList<string> RoutineSignatures { get; init; } = Array.Empty<string>();

    public bool DryRun { get; init; } = true;

    // Only required when a missing table needs full DDL, or Data Mode is
    // FullRefresh — both go through pg_dump/psql. Column-diff ALTERs, routine
    // sync, and Upsert/Mirror data sync work over plain Npgsql and don't need
    // either tool.
    public string? PgDumpExe { get; init; }
    public string? PsqlExe { get; init; }
}

public sealed record SyncResult(bool Ok, string Summary);

// Syncs a schema (or a chosen subset of its tables/functions/procedures) from
// a source connection into a target connection — same server or a completely
// different one, same schema name or a different one. Unlike Copy Schema
// (which always creates a brand-new, empty target), Sync assumes the target
// may already have its own structure and data, so every step is diff-based:
// only what's missing or different is touched, and existing target-only rows
// are left alone unless Mirror mode is explicitly chosen.
public sealed class SchemaSyncRunner
{
    public event EventHandler<string>? LogLine;

    public async Task<SyncResult> RunAsync(SyncOptions o, CancellationToken ct = default)
    {
        try
        {
            Log($">> sync '{o.SourceProfile.Database}'.{o.SourceSchema} @ {o.SourceProfile.Host} -> " +
                $"'{o.TargetProfile.Database}'.{o.TargetSchema} @ {o.TargetProfile.Host}" +
                (o.DryRun ? "   [DRY RUN — previewing only, nothing will be changed]" : ""));

            await using var src = new NpgsqlConnection(o.SourceProfile.BuildConnectionString(o.SourcePassword));
            await using var tgt = new NpgsqlConnection(o.TargetProfile.BuildConnectionString(o.TargetPassword));
            await src.OpenAsync(ct);
            await tgt.OpenAsync(ct);

            if (o.SyncSchema)
            {
                await EnsureTargetSchemaAsync(tgt, o, ct);


                foreach (var table in o.Tables)
                {
                    ct.ThrowIfCancellationRequested();
                    await SyncTableSchemaAsync(src, tgt, o, table, ct);
                }
                foreach (var routine in o.RoutineSignatures)
                {
                    ct.ThrowIfCancellationRequested();
                    await SyncRoutineAsync(src, tgt, o, routine, ct);
                }
            }

            if (o.SyncData)
            {
                foreach (var table in o.Tables)
                {
                    ct.ThrowIfCancellationRequested();
                    await SyncTableDataAsync(src, tgt, o, table, ct);
                }
            }

            var summary = o.DryRun ? "Dry run complete — no changes were made." : "Sync complete.";
            Log(">> " + summary);
            return new SyncResult(true, summary);
        }
        catch (OperationCanceledException)
        {
            Log(">> Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log($">> ERROR: {ex.Message}");
            return new SyncResult(false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- schema

    // A CREATE TABLE / CREATE OR REPLACE FUNCTION replay fails immediately if
    // the target schema itself doesn't exist yet — unlike Copy Schema (which
    // always starts from an empty target), Sync's target schema can be a
    // brand-new name the user just typed, so this can't be assumed away.
    private async Task EnsureTargetSchemaAsync(NpgsqlConnection tgt, SyncOptions o, CancellationToken ct)
    {
        const string existsSql = "SELECT 1 FROM information_schema.schemata WHERE schema_name=@s";
        await using (var check = new NpgsqlCommand(existsSql, tgt))
        {
            check.Parameters.AddWithValue("s", o.TargetSchema);
            if (await check.ExecuteScalarAsync(ct) != null) return;
        }

        Log($"  [schema] target schema '{o.TargetSchema}' does not exist -> " + (o.DryRun ? "would CREATE SCHEMA." : "creating..."));
        if (o.DryRun) return;

        var safeName = o.TargetSchema.Replace("\"", "\"\"");
        await using var create = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS \"{safeName}\"", tgt);
        await create.ExecuteNonQueryAsync(ct);
    }

    private async Task SyncTableSchemaAsync(NpgsqlConnection src, NpgsqlConnection tgt, SyncOptions o, string table, CancellationToken ct)
    {
        if (!await TableExistsAsync(tgt, o.TargetSchema, table, ct))
        {
            Log($"  [table] '{table}' missing in target -> " + (o.DryRun ? "would CREATE (full DDL via pg_dump)." : "creating..."));
            if (!o.DryRun) await CreateTableViaDumpAsync(o, table, ct);
            return;
        }

        var srcCols = await GetColumnsAsync(src, o.SourceSchema, table, ct);
        var tgtCols = await GetColumnsAsync(tgt, o.TargetSchema, table, ct);
        var tgtNames = new HashSet<string>(tgtCols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var srcNames = new HashSet<string>(srcCols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        var statements = new List<string>();
        foreach (var c in srcCols.Where(c => !tgtNames.Contains(c.Name)))
            statements.Add($"ALTER TABLE \"{o.TargetSchema}\".\"{table}\" ADD COLUMN \"{c.Name}\" {c.SqlType};");

        foreach (var c in srcCols)
        {
            var match = tgtCols.FirstOrDefault(t => string.Equals(t.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null && !string.Equals(match.SqlType, c.SqlType, StringComparison.OrdinalIgnoreCase))
                statements.Add($"ALTER TABLE \"{o.TargetSchema}\".\"{table}\" ALTER COLUMN \"{c.Name}\" TYPE {c.SqlType} USING \"{c.Name}\"::{c.SqlType};");
        }

        var extra = tgtCols.Where(c => !srcNames.Contains(c.Name)).Select(c => c.Name).ToList();
        if (extra.Count > 0)
            Log($"  [table] '{table}': target has column(s) not in source — left alone (Sync never drops columns): {string.Join(", ", extra)}");

        if (statements.Count == 0)
        {
            Log($"  [table] '{table}': structure already matches.");
            return;
        }

        foreach (var sql in statements)
        {
            Log((o.DryRun ? "  [DRY RUN] " : "  [applying] ") + sql);
            if (!o.DryRun)
            {
                await using var cmd = new NpgsqlCommand(sql, tgt);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private async Task SyncRoutineAsync(NpgsqlConnection src, NpgsqlConnection tgt, SyncOptions o, string signature, CancellationToken ct)
    {
        var (name, args) = SplitSignature(signature);
        const string sql = @"
SELECT pg_get_functiondef(p.oid)
FROM pg_proc p
JOIN pg_namespace n ON p.pronamespace = n.oid
WHERE n.nspname=@s AND p.proname=@n AND pg_get_function_identity_arguments(p.oid)=@a";
        await using var cmd = new NpgsqlCommand(sql, src);
        cmd.Parameters.AddWithValue("s", o.SourceSchema);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("a", args);
        var def = (string?)await cmd.ExecuteScalarAsync(ct);
        if (def is null)
        {
            Log($"  [routine] '{signature}' not found in source — skipped.");
            return;
        }

        if (!string.Equals(o.SourceSchema, o.TargetSchema, StringComparison.OrdinalIgnoreCase))
            def = RenameSchemaInText(def, o.SourceSchema, o.TargetSchema);

        Log((o.DryRun ? "  [DRY RUN] would CREATE OR REPLACE routine " : "  [applying] CREATE OR REPLACE routine ") + signature);
        if (!o.DryRun)
        {
            await using var apply = new NpgsqlCommand(def, tgt);
            await apply.ExecuteNonQueryAsync(ct);
        }
    }

    // A brand-new table has no ALTER-based equivalent worth hand-rolling —
    // pg_dump already knows how to emit the full CREATE TABLE plus its
    // indexes, defaults, sequences and constraints correctly. Reuses the same
    // word-boundary schema rename SchemaCopyRunner verified against real COPY
    // data, even though a schema-only dump never contains a COPY block.
    private async Task CreateTableViaDumpAsync(SyncOptions o, string table, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(o.PgDumpExe) || string.IsNullOrEmpty(o.PsqlExe))
            throw new InvalidOperationException("pg_dump.exe / psql.exe not configured — set them in Settings to create missing tables.");

        var dumpFile = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.sql");
        var renamedFile = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}_renamed.sql");
        var srcEnv = new Dictionary<string, string> { ["PGPASSWORD"] = o.SourcePassword };
        var tgtEnv = new Dictionary<string, string> { ["PGPASSWORD"] = o.TargetPassword };
        try
        {
            var dumpArgs = new List<string>
            {
                $"--host={o.SourceProfile.Host}", $"--port={o.SourceProfile.Port}",
                $"--username={o.SourceProfile.Username}", $"--dbname={o.SourceProfile.Database}",
                $"--file={dumpFile}", "--format=plain", "--schema-only",
                $"--table=\"{o.SourceSchema}\".\"{table}\"", "--no-owner", "--no-privileges", "--no-password",
            };
            var dumpExit = await RunLogged(o.PgDumpExe!, dumpArgs, srcEnv, ct);
            if (dumpExit != 0) throw new InvalidOperationException($"pg_dump failed (exit {dumpExit}) creating '{table}'.");

            RenameOrCopy(dumpFile, renamedFile, o.SourceSchema, o.TargetSchema);

            var restoreArgs = new List<string>
            {
                $"--host={o.TargetProfile.Host}", $"--port={o.TargetProfile.Port}",
                $"--username={o.TargetProfile.Username}", $"--dbname={o.TargetProfile.Database}",
                "--no-password", "--no-psqlrc", "--single-transaction", "-v", "ON_ERROR_STOP=1", $"--file={renamedFile}",
            };
            var restoreExit = await RunLogged(o.PsqlExe!, restoreArgs, tgtEnv, ct);
            if (restoreExit != 0) throw new InvalidOperationException($"psql failed (exit {restoreExit}) creating '{table}'.");
        }
        finally
        {
            TryDelete(dumpFile);
            TryDelete(renamedFile);
        }
    }

    // ------------------------------------------------------------------ data

    private async Task SyncTableDataAsync(NpgsqlConnection src, NpgsqlConnection tgt, SyncOptions o, string table, CancellationToken ct)
    {
        if (!await TableExistsAsync(tgt, o.TargetSchema, table, ct))
        {
            Log($"  [data] '{table}': target table doesn't exist yet — skipping data sync (enable Schema Sync to create it first).");
            return;
        }

        switch (o.DataMode)
        {
            case DataSyncMode.FullRefresh:
                await FullRefreshAsync(o, table, ct);
                break;
            case DataSyncMode.Upsert:
                await UpsertAsync(src, tgt, o, table, mirror: false, ct);
                break;
            case DataSyncMode.Mirror:
                await UpsertAsync(src, tgt, o, table, mirror: true, ct);
                break;
        }
    }

    private async Task FullRefreshAsync(SyncOptions o, string table, CancellationToken ct)
    {
        Log($"  [data] '{table}': FULL REFRESH" + (o.DryRun
            ? " (dry run — would TRUNCATE target then reload every row from source)"
            : " — truncating target and reloading..."));
        if (o.DryRun) return;

        if (string.IsNullOrEmpty(o.PgDumpExe) || string.IsNullOrEmpty(o.PsqlExe))
            throw new InvalidOperationException("pg_dump.exe / psql.exe not configured — set them in Settings for Full Refresh.");

        await using (var tgtConn = new NpgsqlConnection(o.TargetProfile.BuildConnectionString(o.TargetPassword)))
        {
            await tgtConn.OpenAsync(ct);
            await using var trunc = new NpgsqlCommand($"TRUNCATE TABLE \"{o.TargetSchema}\".\"{table}\";", tgtConn);
            await trunc.ExecuteNonQueryAsync(ct);
        }

        var dumpFile = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.sql");
        var renamedFile = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}_renamed.sql");
        var srcEnv = new Dictionary<string, string> { ["PGPASSWORD"] = o.SourcePassword };
        var tgtEnv = new Dictionary<string, string> { ["PGPASSWORD"] = o.TargetPassword };
        try
        {
            var dumpArgs = new List<string>
            {
                $"--host={o.SourceProfile.Host}", $"--port={o.SourceProfile.Port}",
                $"--username={o.SourceProfile.Username}", $"--dbname={o.SourceProfile.Database}",
                $"--file={dumpFile}", "--format=plain", "--data-only",
                $"--table=\"{o.SourceSchema}\".\"{table}\"", "--no-owner", "--no-privileges", "--no-password",
            };
            var dumpExit = await RunLogged(o.PgDumpExe!, dumpArgs, srcEnv, ct);
            if (dumpExit != 0) throw new InvalidOperationException($"pg_dump failed (exit {dumpExit}) loading data for '{table}'.");

            RenameOrCopy(dumpFile, renamedFile, o.SourceSchema, o.TargetSchema);

            var restoreArgs = new List<string>
            {
                $"--host={o.TargetProfile.Host}", $"--port={o.TargetProfile.Port}",
                $"--username={o.TargetProfile.Username}", $"--dbname={o.TargetProfile.Database}",
                "--no-password", "--no-psqlrc", "--single-transaction", "-v", "ON_ERROR_STOP=1", $"--file={renamedFile}",
            };
            var restoreExit = await RunLogged(o.PsqlExe!, restoreArgs, tgtEnv, ct);
            if (restoreExit != 0) throw new InvalidOperationException($"psql failed (exit {restoreExit}) loading data for '{table}'.");
        }
        finally
        {
            TryDelete(dumpFile);
            TryDelete(renamedFile);
        }
    }

    private async Task UpsertAsync(NpgsqlConnection src, NpgsqlConnection tgt, SyncOptions o, string table, bool mirror, CancellationToken ct)
    {
        var pk = await GetPrimaryKeyColumnsAsync(tgt, o.TargetSchema, table, ct);
        if (pk.Count == 0)
        {
            Log($"  [data] '{table}': target has no primary key — cannot {(mirror ? "mirror" : "upsert")}. Use Full Refresh instead. Skipped.");
            return;
        }

        var colInfos = await GetColumnsAsync(src, o.SourceSchema, table, ct);
        var cols = colInfos.Select(c => c.Name).ToList();
        var dbTypes = colInfos.Select(c => MapDbType(c.DataType)).ToList();
        var unknownCols = colInfos.Where((c, i) => dbTypes[i] == NpgsqlDbType.Unknown).Select(c => c.Name).ToList();
        if (unknownCols.Count > 0)
            Log($"  [data] '{table}': non-standard column type(s) sent as text, double-check results for: {string.Join(", ", unknownCols)}");

        var updateCols = cols.Where(c => !pk.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

        Log($"  [data] '{table}': {(mirror ? "MIRROR" : "UPSERT")} by key ({string.Join(", ", pk)})" + (o.DryRun ? " (dry run)" : ""));

        var quotedCols = string.Join(", ", cols.Select(c => $"\"{c}\""));
        var selectSql = $"SELECT {quotedCols} FROM \"{o.SourceSchema}\".\"{table}\";";

        const int batchSize = 200;
        var batch = new List<object?[]>(batchSize);
        var keysSeen = mirror ? new List<object?[]>() : null;
        var pkIndexes = pk.Select(k => cols.FindIndex(c => string.Equals(c, k, StringComparison.OrdinalIgnoreCase))).ToArray();
        long total = 0;

        await using (var readCmd = new NpgsqlCommand(selectSql, src))
        await using (var reader = await readCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                var row = new object?[cols.Count];
                for (int i = 0; i < cols.Count; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                batch.Add(row);
                keysSeen?.Add(pkIndexes.Select(i => row[i]).ToArray());
                total++;

                if (batch.Count >= batchSize)
                {
                    if (!o.DryRun) await FlushUpsertBatchAsync(tgt, o.TargetSchema, table, cols, dbTypes, pk, updateCols, batch, ct);
                    batch.Clear();
                }
            }
        }
        if (batch.Count > 0 && !o.DryRun)
            await FlushUpsertBatchAsync(tgt, o.TargetSchema, table, cols, dbTypes, pk, updateCols, batch, ct);

        Log($"    {(o.DryRun ? "would upsert" : "upserted")} {total} row(s).");

        if (mirror)
        {
            if (o.DryRun)
            {
                Log("    (dry run) would delete target rows not present in source (by key).");
            }
            else
            {
                var deleted = await DeleteExtraRowsAsync(tgt, o.TargetSchema, table, pk, keysSeen!, ct);
                Log($"    deleted {deleted} row(s) from target not present in source.");
            }
        }
    }

    private static async Task FlushUpsertBatchAsync(
        NpgsqlConnection tgt, string schema, string table,
        List<string> cols, List<NpgsqlDbType> dbTypes, List<string> pk, List<string> updateCols,
        List<object?[]> rows, CancellationToken ct)
    {
        var colList = string.Join(", ", cols.Select(c => $"\"{c}\""));
        var pkList = string.Join(", ", pk.Select(c => $"\"{c}\""));
        var updateSet = updateCols.Count > 0
            ? string.Join(", ", updateCols.Select(c => $"\"{c}\"=EXCLUDED.\"{c}\""))
            : null;

        var values = new StringBuilder();
        var cmd = new NpgsqlCommand { Connection = tgt };
        int p = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0) values.Append(',');
            values.Append('(');
            for (int c = 0; c < cols.Count; c++)
            {
                if (c > 0) values.Append(',');
                var pname = $"p{p++}";
                values.Append('@').Append(pname);
                var type = dbTypes[c] == NpgsqlDbType.Unknown ? NpgsqlDbType.Text : dbTypes[c];
                cmd.Parameters.Add(new NpgsqlParameter(pname, type) { Value = rows[r][c] ?? DBNull.Value });
            }
            values.Append(')');
        }

        cmd.CommandText = $"INSERT INTO \"{schema}\".\"{table}\" ({colList}) VALUES {values} " +
                           $"ON CONFLICT ({pkList}) DO " +
                           (updateSet is null ? "NOTHING;" : $"UPDATE SET {updateSet};");
        await using (cmd)
            await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> DeleteExtraRowsAsync(
        NpgsqlConnection tgt, string schema, string table, List<string> pk, List<object?[]> keepKeys, CancellationToken ct)
    {
        var tempCols = pk.Select((_, i) => $"k{i}").ToList();
        var createSql = $"CREATE TEMP TABLE sync_keep_keys ({string.Join(", ", tempCols.Select(c => $"{c} text"))});";
        await using (var create = new NpgsqlCommand(createSql, tgt))
            await create.ExecuteNonQueryAsync(ct);

        try
        {
            const int batchSize = 500;
            for (int i = 0; i < keepKeys.Count; i += batchSize)
            {
                var slice = keepKeys.Skip(i).Take(batchSize).ToList();
                var cmd = new NpgsqlCommand { Connection = tgt };
                var values = new StringBuilder();
                int p = 0;
                for (int r = 0; r < slice.Count; r++)
                {
                    if (r > 0) values.Append(',');
                    values.Append('(');
                    for (int c = 0; c < pk.Count; c++)
                    {
                        if (c > 0) values.Append(',');
                        var pname = $"k{p++}";
                        values.Append('@').Append(pname);
                        cmd.Parameters.Add(new NpgsqlParameter(pname, NpgsqlDbType.Text)
                        {
                            Value = (object?)slice[r][c]?.ToString() ?? DBNull.Value
                        });
                    }
                    values.Append(')');
                }
                cmd.CommandText = $"INSERT INTO sync_keep_keys ({string.Join(",", tempCols)}) VALUES {values};";
                await using (cmd) await cmd.ExecuteNonQueryAsync(ct);
            }

            var joinCond = string.Join(" AND ", pk.Select((c, i) => $"t.\"{c}\"::text = k.k{i}"));
            var deleteSql = $"DELETE FROM \"{schema}\".\"{table}\" t WHERE NOT EXISTS (SELECT 1 FROM sync_keep_keys k WHERE {joinCond});";
            await using var del = new NpgsqlCommand(deleteSql, tgt);
            return await del.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP TABLE IF EXISTS sync_keep_keys;", tgt);
            try { await drop.ExecuteNonQueryAsync(ct); } catch { }
        }
    }

    // --------------------------------------------------------------- catalog

    private sealed record ColumnInfo(string Name, string SqlType, string DataType);

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = "SELECT 1 FROM information_schema.tables WHERE table_schema=@s AND table_name=@t";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static async Task<List<ColumnInfo>> GetColumnsAsync(NpgsqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
SELECT column_name, data_type, udt_name, character_maximum_length, numeric_precision, numeric_scale
FROM information_schema.columns
WHERE table_schema=@s AND table_name=@t
ORDER BY ordinal_position;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        var list = new List<ColumnInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var dataType = reader.GetString(1);
            var udt = reader.GetString(2);
            int? maxLen = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            int? precision = reader.IsDBNull(4) ? null : reader.GetInt32(4);
            int? scale = reader.IsDBNull(5) ? null : reader.GetInt32(5);
            list.Add(new ColumnInfo(reader.GetString(0), BuildSqlType(dataType, udt, maxLen, precision, scale), dataType));
        }
        return list;
    }

    private static async Task<List<string>> GetPrimaryKeyColumnsAsync(NpgsqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
SELECT a.attname
FROM pg_index i
JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
JOIN pg_class c ON c.oid = i.indrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE i.indisprimary AND n.nspname=@s AND c.relname=@t
ORDER BY array_position(i.indkey, a.attnum);";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    private static string BuildSqlType(string dataType, string udtName, int? maxLen, int? precision, int? scale) => dataType switch
    {
        "character varying" => maxLen.HasValue ? $"varchar({maxLen})" : "varchar",
        "character" => maxLen.HasValue ? $"char({maxLen})" : "char",
        "numeric" => precision.HasValue && scale.HasValue ? $"numeric({precision},{scale})" : "numeric",
        "USER-DEFINED" => udtName,
        "ARRAY" => udtName.StartsWith("_") ? udtName[1..] + "[]" : udtName + "[]",
        _ => dataType
    };

    private static NpgsqlDbType MapDbType(string dataType) => dataType switch
    {
        "integer" => NpgsqlDbType.Integer,
        "bigint" => NpgsqlDbType.Bigint,
        "smallint" => NpgsqlDbType.Smallint,
        "numeric" => NpgsqlDbType.Numeric,
        "real" => NpgsqlDbType.Real,
        "double precision" => NpgsqlDbType.Double,
        "boolean" => NpgsqlDbType.Boolean,
        "text" => NpgsqlDbType.Text,
        "character varying" => NpgsqlDbType.Varchar,
        "character" => NpgsqlDbType.Char,
        "date" => NpgsqlDbType.Date,
        "timestamp without time zone" => NpgsqlDbType.Timestamp,
        "timestamp with time zone" => NpgsqlDbType.TimestampTz,
        "time without time zone" => NpgsqlDbType.Time,
        "uuid" => NpgsqlDbType.Uuid,
        "jsonb" => NpgsqlDbType.Jsonb,
        "json" => NpgsqlDbType.Json,
        "bytea" => NpgsqlDbType.Bytea,
        _ => NpgsqlDbType.Unknown,
    };

    private static (string Name, string Args) SplitSignature(string signature)
    {
        var i = signature.IndexOf('(');
        if (i < 0) return (signature, "");
        return (signature[..i], signature[(i + 1)..^1]);
    }

    private static string RenameSchemaInText(string sql, string from, string to)
        => new Regex($@"\b{Regex.Escape(from)}\b").Replace(sql, to);

    private static void RenameOrCopy(string inputPath, string outputPath, string sourceSchema, string targetSchema)
    {
        if (string.Equals(sourceSchema, targetSchema, StringComparison.OrdinalIgnoreCase))
            File.Copy(inputPath, outputPath, true);
        else
            SchemaCopyRunner.RenameSchema(inputPath, outputPath, sourceSchema, targetSchema);
    }

    private async Task<int> RunLogged(string exe, List<string> args, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var runner = new ProcessRunner();
        runner.StdoutLine += (_, l) => Log("    " + l);
        runner.StderrLine += (_, l) => Log("    " + l);
        return await runner.RunAsync(exe, args, env, ct: ct);
    }

    private void Log(string line) => LogLine?.Invoke(this, line);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
