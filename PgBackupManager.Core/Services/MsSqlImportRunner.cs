using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

// A SQL Server connection (not persisted like ConnectionProfile — this is
// meant for one-off/occasional migrations, not a repeatedly reused profile).
public sealed class MsSqlSource
{
    public required string Server { get; init; }
    public required string Database { get; init; }
    public bool IntegratedSecurity { get; init; } = true;
    public string? Username { get; init; }
    public string? Password { get; init; }

    public string BuildConnectionString()
    {
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        };
        if (IntegratedSecurity) csb.IntegratedSecurity = true;
        else
        {
            csb.UserID = Username ?? "";
            csb.Password = Password ?? "";
        }
        return csb.ConnectionString;
    }
}

public sealed class ImportOptions
{
    public required MsSqlSource Source { get; init; }
    public required ConnectionProfile TargetProfile { get; init; }
    public required string TargetPassword { get; init; }
    public required string TargetSchema { get; init; }
    public string SourceSchema { get; init; } = "dbo";
    public IReadOnlyList<string> Tables { get; init; } = Array.Empty<string>();
    public bool DryRun { get; init; } = true;
}

public sealed record ImportResult(bool Ok, string Summary);

// One-way bridge: SQL Server (read-only) -> a Postgres schema. This is a
// first-time import, not a repeatable diff-sync like SchemaSyncRunner — every
// selected table either doesn't exist yet in the target (CREATE TABLE from a
// generated type-mapped DDL, then bulk-insert) or already exists (rows are
// appended only; structure is left alone). SQL Server's proprietary .bak
// format can't be read directly by anything outside SQL Server itself, so the
// backup must already be restored to a live SQL Server the app can connect to
// — this runner picks up from there.
public sealed class MsSqlImportRunner
{
    public event EventHandler<string>? LogLine;

    public static async Task<List<string>> ListSchemasAsync(MsSqlSource source, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(source.BuildConnectionString());
        await conn.OpenAsync(ct);
        const string sql = "SELECT s.name FROM sys.schemas s JOIN sys.tables t ON t.schema_id=s.schema_id GROUP BY s.name ORDER BY s.name";
        await using var cmd = new SqlCommand(sql, conn);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    public static async Task<List<string>> ListTablesAsync(MsSqlSource source, string schema, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(source.BuildConnectionString());
        await conn.OpenAsync(ct);
        const string sql = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='BASE TABLE' AND TABLE_SCHEMA=@s ORDER BY TABLE_NAME";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@s", schema);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    public async Task<ImportResult> RunAsync(ImportOptions o, CancellationToken ct = default)
    {
        try
        {
            Log($">> import '{o.SourceSchema}' @ SQL Server '{o.Source.Server}'/{o.Source.Database} -> " +
                $"'{o.TargetSchema}' @ Postgres '{o.TargetProfile.Database}' @ {o.TargetProfile.Host}" +
                (o.DryRun ? "   [DRY RUN — previewing only, nothing will be changed]" : ""));

            await using var src = new SqlConnection(o.Source.BuildConnectionString());
            await using var tgt = new NpgsqlConnection(o.TargetProfile.BuildConnectionString(o.TargetPassword));
            await src.OpenAsync(ct);
            await tgt.OpenAsync(ct);

            await EnsureTargetSchemaAsync(tgt, o.TargetSchema, o.DryRun, ct);

            foreach (var table in o.Tables)
            {
                ct.ThrowIfCancellationRequested();
                await ImportTableAsync(src, tgt, o, table, ct);
            }

            var summary = o.DryRun ? "Dry run complete — no changes were made." : "Import complete.";
            Log(">> " + summary);
            return new ImportResult(true, summary);
        }
        catch (OperationCanceledException)
        {
            Log(">> Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log($">> ERROR: {ex.Message}");
            return new ImportResult(false, ex.Message);
        }
    }

    private async Task EnsureTargetSchemaAsync(NpgsqlConnection tgt, string schema, bool dryRun, CancellationToken ct)
    {
        const string existsSql = "SELECT 1 FROM information_schema.schemata WHERE schema_name=@s";
        await using (var check = new NpgsqlCommand(existsSql, tgt))
        {
            check.Parameters.AddWithValue("s", schema);
            if (await check.ExecuteScalarAsync(ct) != null) return;
        }

        Log($"  [schema] target schema '{schema}' does not exist -> " + (dryRun ? "would CREATE SCHEMA." : "creating..."));
        if (dryRun) return;

        var safe = schema.Replace("\"", "\"\"");
        await using var create = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS \"{safe}\"", tgt);
        await create.ExecuteNonQueryAsync(ct);
    }

    private async Task ImportTableAsync(SqlConnection src, NpgsqlConnection tgt, ImportOptions o, string table, CancellationToken ct)
    {
        var cols = await GetMsSqlColumnsAsync(src, o.SourceSchema, table, ct);
        if (cols.Count == 0)
        {
            Log($"  [table] '{table}' not found in source (or has no columns) — skipped.");
            return;
        }

        var pk = await GetMsSqlPrimaryKeyAsync(src, o.SourceSchema, table, ct);
        var unmapped = cols.Where(c => c.Mapping.PgType == "text" && !IsGenuinelyTextLike(c.SourceType)).Select(c => c.Name).ToList();
        if (unmapped.Count > 0)
            Log($"  [table] '{table}': non-standard SQL Server type(s) mapped to text as a fallback, double-check: {string.Join(", ", unmapped)}");

        var exists = await PgTableExistsAsync(tgt, o.TargetSchema, table, ct);
        if (!exists)
        {
            var ddl = BuildCreateTableSql(o.TargetSchema, table, cols, pk);
            Log((o.DryRun ? "  [DRY RUN] " : "  [applying] ") +
                $"CREATE TABLE \"{o.TargetSchema}\".\"{table}\" ({cols.Count} columns" + (pk.Count > 0 ? $", PK {string.Join(",", pk)}" : ", no PK") + ")");
            if (!o.DryRun)
            {
                await using var cmd = new NpgsqlCommand(ddl, tgt);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        else
        {
            Log($"  [table] '{table}' already exists in target — appending rows only (structure left alone).");
        }

        var estimate = await GetMsSqlRowCountEstimateAsync(src, o.SourceSchema, table, ct);
        Log($"  [data] '{table}': " + (o.DryRun ? $"would copy ~{estimate} row(s)." : $"copying ~{estimate} row(s)..."));
        if (o.DryRun) return;

        var copied = await CopyDataAsync(src, tgt, o, table, cols, ct);
        Log($"    copied {copied} row(s).");
    }

    private static async Task<long> CopyDataAsync(SqlConnection src, NpgsqlConnection tgt, ImportOptions o, string table, List<MsSqlColumn> cols, CancellationToken ct)
    {
        var colNames = cols.Select(c => c.Name).ToList();
        var dbTypes = cols.Select(c => c.Mapping.PgDbType).ToList();
        var quotedSrcCols = string.Join(", ", colNames.Select(c => $"[{c}]"));
        var selectSql = $"SELECT {quotedSrcCols} FROM [{o.SourceSchema}].[{table}]";

        const int batchSize = 200;
        var batch = new List<object?[]>(batchSize);
        long total = 0;

        // One transaction per table: if a later batch fails (e.g. a duplicate
        // key on a re-run against an already-populated table), every batch
        // already applied for THIS table gets rolled back too, instead of
        // leaving however-many-batches-got-through-first sitting there half-imported.
        await using var tx = await tgt.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = new SqlCommand(selectSql, src) { CommandTimeout = 300 };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                var row = new object?[colNames.Count];
                for (int i = 0; i < colNames.Count; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                batch.Add(row);
                total++;

                if (batch.Count >= batchSize)
                {
                    await FlushInsertBatchAsync(tgt, tx, o.TargetSchema, table, colNames, dbTypes, batch, ct);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                await FlushInsertBatchAsync(tgt, tx, o.TargetSchema, table, colNames, dbTypes, batch, ct);

            await tx.CommitAsync(ct);
            return total;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task FlushInsertBatchAsync(
        NpgsqlConnection tgt, NpgsqlTransaction tx, string schema, string table,
        List<string> cols, List<NpgsqlDbType> dbTypes, List<object?[]> rows, CancellationToken ct)
    {
        var colList = string.Join(", ", cols.Select(c => $"\"{c}\""));
        var values = new StringBuilder();
        var cmd = new NpgsqlCommand { Connection = tgt, Transaction = tx };
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
                cmd.Parameters.Add(new NpgsqlParameter(pname, dbTypes[c]) { Value = rows[r][c] ?? DBNull.Value });
            }
            values.Append(')');
        }
        cmd.CommandText = $"INSERT INTO \"{schema}\".\"{table}\" ({colList}) VALUES {values};";
        await using (cmd)
            await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------- SQL Server side

    private sealed record TypeMapping(string PgType, NpgsqlDbType PgDbType);
    private sealed record MsSqlColumn(string Name, string SourceType, bool IsNullable, TypeMapping Mapping);

    private static async Task<List<MsSqlColumn>> GetMsSqlColumnsAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA=@s AND TABLE_NAME=@t
ORDER BY ORDINAL_POSITION;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        var list = new List<MsSqlColumn>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var dataType = reader.GetString(1);
            int? maxLen = reader.IsDBNull(2) ? null : reader.GetInt32(2);
            int? precision = reader.IsDBNull(3) ? null : (int)reader.GetByte(3);
            int? scale = reader.IsDBNull(4) ? null : reader.GetInt32(4);
            var isNullable = reader.GetString(5) == "YES";
            list.Add(new MsSqlColumn(name, dataType, isNullable, MapType(dataType, maxLen, precision, scale)));
        }
        return list;
    }

    private static async Task<List<string>> GetMsSqlPrimaryKeyAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
SELECT c.name
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
JOIN sys.tables t ON t.object_id=i.object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE i.is_primary_key=1 AND s.name=@s AND t.name=@t
ORDER BY ic.key_ordinal;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    // Catalog-based estimate (like sys.partitions) rather than COUNT(*), so a
    // 380k-row accounting table doesn't get scanned twice just to log a number.
    private static async Task<long> GetMsSqlRowCountEstimateAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
SELECT SUM(p.rows) FROM sys.tables t
JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)
WHERE s.name=@s AND t.name=@t;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@s", schema);
        cmd.Parameters.AddWithValue("@t", table);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    private static bool IsGenuinelyTextLike(string sqlType) => sqlType.ToLowerInvariant() is "text" or "ntext" or "varchar" or "nvarchar" or "char" or "nchar" or "xml";

    private static TypeMapping MapType(string sqlType, int? maxLen, int? precision, int? scale) => sqlType.ToLowerInvariant() switch
    {
        "int" => new("integer", NpgsqlDbType.Integer),
        "bigint" => new("bigint", NpgsqlDbType.Bigint),
        "smallint" => new("smallint", NpgsqlDbType.Smallint),
        "tinyint" => new("smallint", NpgsqlDbType.Smallint),
        "bit" => new("boolean", NpgsqlDbType.Boolean),
        "decimal" or "numeric" => precision.HasValue && scale.HasValue ? new($"numeric({precision},{scale})", NpgsqlDbType.Numeric) : new("numeric", NpgsqlDbType.Numeric),
        "money" or "smallmoney" => new("numeric(19,4)", NpgsqlDbType.Numeric),
        "float" => new("double precision", NpgsqlDbType.Double),
        "real" => new("real", NpgsqlDbType.Real),
        "datetime" or "smalldatetime" or "datetime2" => new("timestamp", NpgsqlDbType.Timestamp),
        "date" => new("date", NpgsqlDbType.Date),
        "time" => new("time", NpgsqlDbType.Time),
        "datetimeoffset" => new("timestamptz", NpgsqlDbType.TimestampTz),
        "char" or "nchar" => maxLen is > 0 ? new($"char({maxLen})", NpgsqlDbType.Char) : new("char", NpgsqlDbType.Char),
        "varchar" or "nvarchar" => maxLen is null or -1 ? new("text", NpgsqlDbType.Text) : new($"varchar({maxLen})", NpgsqlDbType.Varchar),
        "text" or "ntext" => new("text", NpgsqlDbType.Text),
        "uniqueidentifier" => new("uuid", NpgsqlDbType.Uuid),
        // SQL Server's "timestamp"/"rowversion" is an 8-byte row-change stamp, NOT a date/time value.
        "varbinary" or "binary" or "image" or "timestamp" or "rowversion" => new("bytea", NpgsqlDbType.Bytea),
        "xml" => new("xml", NpgsqlDbType.Xml),
        _ => new("text", NpgsqlDbType.Text),
    };

    // -------------------------------------------------------------- Postgres side

    private static async Task<bool> PgTableExistsAsync(NpgsqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = "SELECT 1 FROM information_schema.tables WHERE table_schema=@s AND table_name=@t";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static string BuildCreateTableSql(string schema, string table, List<MsSqlColumn> cols, List<string> pk)
    {
        var colDefs = cols.Select(c => $"\"{c.Name}\" {c.Mapping.PgType}" + (c.IsNullable ? "" : " NOT NULL"));
        var pkClause = pk.Count > 0 ? $", PRIMARY KEY ({string.Join(", ", pk.Select(k => $"\"{k}\""))})" : "";
        return $"CREATE TABLE \"{schema}\".\"{table}\" ({string.Join(", ", colDefs)}{pkClause});";
    }

    private void Log(string line) => LogLine?.Invoke(this, line);
}
