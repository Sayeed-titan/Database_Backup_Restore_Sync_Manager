using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Providers;

public sealed class SqlServerProvider : DbProviderBase
{
    public override DbEngine Engine => DbEngine.SqlServer;
    public override string Name => "SQL Server";
    public override ScriptDialect Dialect => ScriptDialect.SqlServer;

    public override DbConnection CreateConnection(string cs) => new SqlConnection(cs);
    public override string Quote(string id) => "[" + id.Replace("]", "]]") + "]";

    public override async Task<List<string>> ListDatabasesAsync(DbConnection c, CancellationToken ct = default) =>
        (await RowsAsync(c, "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name", ct)).Select(r => S(r[0])).ToList();

    public override async Task<List<string>> ListSchemasAsync(DbConnection c, CancellationToken ct = default) =>
        (await RowsAsync(c, "SELECT name FROM sys.schemas WHERE schema_id < 16384 AND name NOT IN ('sys','INFORMATION_SCHEMA','guest') ORDER BY name", ct))
        .Select(r => S(r[0])).ToList();

    public override async Task<List<DbObjectInfo>> ListObjectsAsync(DbConnection c, string schema, CancellationToken ct = default)
    {
        const string sql = @"
SELECT o.name, RTRIM(o.type) FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE s.name=@s AND o.type IN ('U','V','P','FN','IF','TF','SO','TR') AND o.is_ms_shipped=0
ORDER BY CASE RTRIM(o.type) WHEN 'U' THEN 0 WHEN 'V' THEN 1 WHEN 'P' THEN 2 WHEN 'SO' THEN 4 WHEN 'TR' THEN 5 ELSE 3 END, o.name";
        return (await RowsAsync(c, sql, ct, ("@s", schema))).Select(r => new DbObjectInfo(schema, S(r[0]), S(r[1]) switch
        {
            "U" => DbObjectType.Table,
            "V" => DbObjectType.View,
            "P" => DbObjectType.Procedure,
            "SO" => DbObjectType.Sequence,
            "TR" => DbObjectType.Trigger,
            _ => DbObjectType.Function,
        })).ToList();
    }

    public override async Task<TableInfo?> GetTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        const string colSql = @"
SELECT c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.IS_NULLABLE,
       COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity')
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_SCHEMA=@s AND c.TABLE_NAME=@t
ORDER BY c.ORDINAL_POSITION";
        var rows = await RowsAsync(c, colSql, ct, ("@s", schema), ("@t", table));
        if (rows.Count == 0) return null;

        const string pkSql = @"
SELECT col.name FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns col ON col.object_id=ic.object_id AND col.column_id=ic.column_id
JOIN sys.tables t ON t.object_id=i.object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE i.is_primary_key=1 AND s.name=@s AND t.name=@t
ORDER BY ic.key_ordinal";
        var pk = (await RowsAsync(c, pkSql, ct, ("@s", schema), ("@t", table))).Select(r => S(r[0])).ToList();

        return new TableInfo
        {
            Schema = schema,
            Name = table,
            Columns = rows.Select(r => MapColumn(S(r[0]), S(r[1]), I(r[2]), I(r[3]), I(r[4]), S(r[5]) == "YES", I(r[6]) == 1)).ToList(),
            PrimaryKey = pk,
        };
    }

    internal static ColumnInfo MapColumn(string name, string type, int? maxLen, int? precision, int? scale, bool nullable, bool identity)
    {
        var t = type.ToLowerInvariant();
        var native = t switch
        {
            "varchar" or "nvarchar" or "char" or "nchar" or "varbinary" or "binary" => $"{t}({(maxLen is -1 ? "max" : maxLen?.ToString())})",
            "decimal" or "numeric" => $"{t}({precision},{scale})",
            _ => t,
        };
        var (ct, len, p, s) = t switch
        {
            "int" => (CanonicalType.Int32, (int?)null, (int?)null, (int?)null),
            "bigint" => (CanonicalType.Int64, null, null, null),
            "smallint" or "tinyint" => (CanonicalType.Int16, null, null, null),
            "bit" => (CanonicalType.Bool, null, null, null),
            "decimal" or "numeric" => (CanonicalType.Decimal, null, precision, scale),
            "money" or "smallmoney" => (CanonicalType.Decimal, null, 19, 4),
            "float" => (CanonicalType.Float64, null, null, null),
            "real" => (CanonicalType.Float32, null, null, null),
            "datetime" or "smalldatetime" or "datetime2" => (CanonicalType.DateTime, null, null, null),
            "date" => (CanonicalType.Date, null, null, null),
            "time" => (CanonicalType.Time, null, null, null),
            "datetimeoffset" => (CanonicalType.DateTimeOffset, null, null, null),
            "char" or "nchar" => (CanonicalType.FixedString, maxLen is > 0 ? maxLen : 1, null, null),
            "varchar" or "nvarchar" => maxLen is null or -1 ? (CanonicalType.Text, null, null, null) : (CanonicalType.String, maxLen, null, null),
            "text" or "ntext" or "sysname" or "hierarchyid" or "sql_variant" or "geography" or "geometry" => (CanonicalType.Text, null, null, null),
            "uniqueidentifier" => (CanonicalType.Guid, null, null, null),
            // SQL Server's "timestamp"/"rowversion" is an 8-byte row-change stamp, NOT a date/time value.
            "varbinary" or "binary" or "image" or "timestamp" or "rowversion" => (CanonicalType.Binary, null, null, null),
            "xml" => (CanonicalType.Xml, null, null, null),
            _ => (CanonicalType.Text, null, null, null),
        };
        return new ColumnInfo(name, native, ct, len, p, s, nullable, identity);
    }

    public override async Task<string?> GetObjectSourceAsync(DbConnection c, DbObjectInfo o, CancellationToken ct = default)
    {
        if (o.Type == DbObjectType.Table)
        {
            var t = await GetTableAsync(c, o.Schema, o.Name, ct);
            return t is null ? null : BuildCreateTable(o.Schema, t) + ";";
        }
        return S(await ScalarAsync(c, "SELECT OBJECT_DEFINITION(OBJECT_ID(@n))", ct, ("@n", Qualify(o.Schema, o.Name))));
    }

    public override async Task<bool> SchemaExistsAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        await ScalarAsync(c, "SELECT 1 FROM sys.schemas WHERE name=@s", ct, ("@s", schema)) != null;

    public override Task CreateSchemaAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        ExecAsync(c, $"EXEC('CREATE SCHEMA {Quote(schema).Replace("'", "''")}')", ct);

    public override string NativeType(ColumnInfo col, bool isKey) => col.Type switch
    {
        CanonicalType.Bool => "bit",
        CanonicalType.Int16 => "smallint",
        CanonicalType.Int32 => "int",
        CanonicalType.Int64 => "bigint",
        CanonicalType.Decimal => col.Precision is > 0 and <= 38 ? $"decimal({col.Precision},{Math.Clamp(col.Scale ?? 0, 0, col.Precision.Value)})" : "decimal(38,10)",
        CanonicalType.Float32 => "real",
        CanonicalType.Float64 => "float",
        CanonicalType.Date => "date",
        CanonicalType.Time => "time",
        CanonicalType.DateTime => "datetime2",
        CanonicalType.DateTimeOffset => "datetimeoffset",
        CanonicalType.String => col.Length is > 0 and <= 4000 ? $"nvarchar({col.Length})" : isKey ? "nvarchar(450)" : "nvarchar(max)",
        CanonicalType.FixedString => col.Length is > 0 and <= 4000 ? $"nchar({col.Length})" : isKey ? "nvarchar(450)" : "nvarchar(max)",
        CanonicalType.Binary => isKey ? "varbinary(900)" : "varbinary(max)",
        CanonicalType.Guid => "uniqueidentifier",
        CanonicalType.Xml => isKey ? "nvarchar(450)" : "xml",
        _ => isKey ? "nvarchar(450)" : "nvarchar(max)",
    };

    public override string SelectTopSql(string schema, string table, int rows) => $"SELECT TOP ({rows}) * FROM {Qualify(schema, table)}";

    public override Task DropTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default) =>
        ExecAsync(c, $"IF OBJECT_ID(N'{Qualify(schema, table).Replace("'", "''")}', N'U') IS NOT NULL DROP TABLE {Qualify(schema, table)}", ct);

    public override int MaxBatchRows(int columnCount) => 5000;

    public override async Task<List<IndexInfo>> GetIndexesAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        // Plain rowstore indexes; filtered, columnstore, XML and spatial ones are skipped.
        const string sql = @"
SELECT i.name, i.is_unique, STRING_AGG(col.name, CHAR(1)) WITHIN GROUP (ORDER BY ic.key_ordinal)
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
JOIN sys.tables t ON t.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @s AND t.name = @t AND i.is_primary_key = 0 AND i.type IN (1, 2) AND i.has_filter = 0 AND i.is_hypothetical = 0
GROUP BY i.name, i.is_unique
ORDER BY i.name";
        return (await RowsAsync(c, sql, ct, ("@s", schema), ("@t", table)))
            .Select(r => new IndexInfo(S(r[0]), Split1(r[2]), Convert.ToBoolean(r[1]))).ToList();
    }

    public override async Task<List<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
SELECT fk.name,
       STRING_AGG(pc.name, CHAR(1)) WITHIN GROUP (ORDER BY fkc.constraint_column_id),
       rs.name, rt.name,
       STRING_AGG(rc.name, CHAR(1)) WITHIN GROUP (ORDER BY fkc.constraint_column_id),
       fk.delete_referential_action_desc, fk.update_referential_action_desc
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
JOIN sys.tables t ON t.object_id = fk.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
WHERE s.name = @s AND t.name = @t
GROUP BY fk.name, rs.name, rt.name, fk.delete_referential_action_desc, fk.update_referential_action_desc
ORDER BY fk.name";
        return (await RowsAsync(c, sql, ct, ("@s", schema), ("@t", table)))
            .Select(r => new ForeignKeyInfo(S(r[0]), Split1(r[1]), S(r[2]), S(r[3]), Split1(r[4]), NormalizeAction(S(r[5])), NormalizeAction(S(r[6])))).ToList();
    }

    public override string? BuildAlterColumnType(string schema, string table, ColumnInfo col) =>
        $"ALTER TABLE {Qualify(schema, table)} ALTER COLUMN {Quote(col.Name)} {NativeType(col, false)}{(col.Nullable ? " NULL" : " NOT NULL")}";

    // SQL Server has no RESTRICT (NO ACTION behaves the same way).
    protected override string RefAction(string evt, string action) => action == "RESTRICT" ? "" : base.RefAction(evt, action);

    private static Type ClrType(CanonicalType t) => t switch
    {
        CanonicalType.Bool => typeof(bool),
        CanonicalType.Int16 => typeof(short),
        CanonicalType.Int32 => typeof(int),
        CanonicalType.Int64 => typeof(long),
        CanonicalType.Decimal => typeof(decimal),
        CanonicalType.Float32 => typeof(float),
        CanonicalType.Float64 => typeof(double),
        CanonicalType.Date or CanonicalType.DateTime => typeof(DateTime),
        CanonicalType.Time => typeof(TimeSpan),
        CanonicalType.DateTimeOffset => typeof(DateTimeOffset),
        CanonicalType.Binary => typeof(byte[]),
        CanonicalType.Guid => typeof(Guid),
        _ => typeof(string),
    };

    public override async Task WriteBatchAsync(DbConnection c, DbTransaction tx, string schema, TableInfo t, IReadOnlyList<object?[]> rows, WriteMode mode, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var conn = (SqlConnection)c;
        var sqlTx = (SqlTransaction)tx;

        var dt = new DataTable();
        foreach (var col in t.Columns) dt.Columns.Add(col.Name, ClrType(col.Type));
        foreach (var r in rows) dt.Rows.Add(r.Select(v => v ?? DBNull.Value).ToArray());

        var dest = Qualify(schema, t.Name);
        string? stage = null;
        if (mode == WriteMode.Upsert)
        {
            if (t.PrimaryKey.Count == 0) throw new InvalidOperationException($"Upsert needs a primary key on {t.Name}.");
            stage = "[#pgbm_stage]";
            await ExecAsync(c, $"IF OBJECT_ID('tempdb..#pgbm_stage') IS NOT NULL DROP TABLE #pgbm_stage; SELECT TOP 0 * INTO #pgbm_stage FROM {dest};", ct, tx);
            dest = stage;
        }

        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, sqlTx)
               { DestinationTableName = dest, BulkCopyTimeout = 0, BatchSize = rows.Count })
        {
            foreach (var col in t.Columns) bulk.ColumnMappings.Add(col.Name, col.Name);
            await bulk.WriteToServerAsync(dt, ct);
        }

        if (stage != null)
        {
            var on = string.Join(" AND ", t.PrimaryKey.Select(k => $"T.{Quote(k)} = S.{Quote(k)}"));
            var nonKey = t.Columns.Where(x => !TypeFacts.IsKey(t, x) && !x.IsIdentity).ToList();
            var cols = string.Join(", ", t.Columns.Select(x => Quote(x.Name)));
            var vals = string.Join(", ", t.Columns.Select(x => "S." + Quote(x.Name)));
            var hasIdentity = t.Columns.Any(x => x.IsIdentity);
            var sql =
                (hasIdentity ? $"SET IDENTITY_INSERT {Qualify(schema, t.Name)} ON; " : "") +
                $"MERGE {Qualify(schema, t.Name)} AS T USING #pgbm_stage AS S ON ({on}) " +
                (nonKey.Count > 0 ? "WHEN MATCHED THEN UPDATE SET " + string.Join(", ", nonKey.Select(x => $"T.{Quote(x.Name)} = S.{Quote(x.Name)}")) + " " : "") +
                $"WHEN NOT MATCHED THEN INSERT ({cols}) VALUES ({vals});" +
                (hasIdentity ? $" SET IDENTITY_INSERT {Qualify(schema, t.Name)} OFF;" : "") +
                " DROP TABLE #pgbm_stage;";
            await ExecAsync(c, sql, ct, tx);
        }
    }
}
