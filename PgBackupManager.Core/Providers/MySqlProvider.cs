using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Providers;

// MySQL and MariaDB. In MySQL a "schema" and a "database" are the same thing.
public sealed class MySqlProvider : DbProviderBase
{
    public override DbEngine Engine => DbEngine.MySql;
    public override string Name => "MySQL / MariaDB";
    public override ScriptDialect Dialect => ScriptDialect.MySql;

    public override DbConnection CreateConnection(string cs) => new MySqlConnection(cs);
    public override string Quote(string id) => "`" + id.Replace("`", "``") + "`";

    public override async Task<List<string>> ListSchemasAsync(DbConnection c, CancellationToken ct = default) =>
        (await RowsAsync(c, "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('mysql','information_schema','performance_schema','sys') ORDER BY 1", ct))
        .Select(r => S(r[0])).ToList();

    public override async Task<List<DbObjectInfo>> ListObjectsAsync(DbConnection c, string schema, CancellationToken ct = default)
    {
        const string sql = @"
SELECT table_name, CASE table_type WHEN 'VIEW' THEN 'V' ELSE 'T' END, 0 FROM information_schema.tables WHERE table_schema=@s
UNION ALL SELECT routine_name, LEFT(routine_type,1), 1 FROM information_schema.routines WHERE routine_schema=@s
UNION ALL SELECT trigger_name, 'R', 2 FROM information_schema.triggers WHERE trigger_schema=@s
ORDER BY 3, 2, 1";
        return (await RowsAsync(c, sql, ct, ("@s", schema))).Select(r => new DbObjectInfo(schema, S(r[0]), S(r[1]) switch
        {
            "V" => DbObjectType.View,
            "F" => DbObjectType.Function,
            "P" => DbObjectType.Procedure,
            "R" => DbObjectType.Trigger,
            _ => DbObjectType.Table,
        })).ToList();
    }

    public override async Task<TableInfo?> GetTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        var rows = await RowsAsync(c, @"SELECT column_name, data_type, column_type, character_maximum_length, numeric_precision, numeric_scale, is_nullable, extra
            FROM information_schema.columns WHERE table_schema=@s AND table_name=@t ORDER BY ordinal_position", ct, ("@s", schema), ("@t", table));
        if (rows.Count == 0) return null;
        var pk = (await RowsAsync(c, @"SELECT column_name FROM information_schema.key_column_usage
            WHERE table_schema=@s AND table_name=@t AND constraint_name='PRIMARY' ORDER BY ordinal_position", ct, ("@s", schema), ("@t", table)))
            .Select(r => S(r[0])).ToList();
        return new TableInfo
        {
            Schema = schema,
            Name = table,
            Columns = rows.Select(r => MapColumn(S(r[0]), S(r[1]), S(r[2]), r[3] is null ? null : (long?)Convert.ToInt64(r[3]), I(r[4]), I(r[5]), S(r[6]) == "YES", S(r[7]).Contains("auto_increment"))).ToList(),
            PrimaryKey = pk,
        };
    }

    internal static ColumnInfo MapColumn(string name, string dataType, string columnType, long? maxLen, int? precision, int? scale, bool nullable, bool identity)
    {
        var t = dataType.ToLowerInvariant();
        var unsigned = columnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
        int? len = maxLen is > 0 and <= int.MaxValue ? (int)maxLen : null;
        (CanonicalType, int?, int?, int?) m = t switch
        {
            "tinyint" when columnType.StartsWith("tinyint(1)", StringComparison.OrdinalIgnoreCase) => (CanonicalType.Bool, null, null, null),
            "bit" when columnType is "bit(1)" => (CanonicalType.Bool, null, null, null),
            "tinyint" or "smallint" or "year" => unsigned && t == "smallint" ? (CanonicalType.Int32, null, null, null) : (CanonicalType.Int16, null, null, null),
            "mediumint" => (CanonicalType.Int32, null, null, null),
            "int" or "integer" => unsigned ? (CanonicalType.Int64, null, null, null) : (CanonicalType.Int32, null, null, null),
            "bigint" => unsigned ? (CanonicalType.Decimal, null, 20, 0) : (CanonicalType.Int64, null, null, null),
            "bit" => (CanonicalType.Int64, null, null, null),
            "decimal" or "numeric" => (CanonicalType.Decimal, null, precision, scale),
            "float" => (CanonicalType.Float32, null, null, null),
            "double" or "real" => (CanonicalType.Float64, null, null, null),
            "date" => (CanonicalType.Date, null, null, null),
            "time" => (CanonicalType.Time, null, null, null),
            "datetime" or "timestamp" => (CanonicalType.DateTime, null, null, null),
            "char" => (CanonicalType.FixedString, len ?? 1, null, null),
            "varchar" or "enum" or "set" => (CanonicalType.String, len, null, null),
            "tinytext" or "text" or "mediumtext" or "longtext" => (CanonicalType.Text, null, null, null),
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => (CanonicalType.Binary, null, null, null),
            "json" => (CanonicalType.Json, null, null, null),
            _ => (CanonicalType.Text, null, null, null),
        };
        return new ColumnInfo(name, columnType, m.Item1, m.Item2, m.Item3, m.Item4, nullable, identity);
    }

    public override async Task<string?> GetObjectSourceAsync(DbConnection c, DbObjectInfo o, CancellationToken ct = default)
    {
        var (kw, col) = o.Type switch
        {
            DbObjectType.View => ("VIEW", 1),
            DbObjectType.Procedure => ("PROCEDURE", 2),
            DbObjectType.Function => ("FUNCTION", 2),
            DbObjectType.Trigger => ("TRIGGER", 2),
            _ => ("TABLE", 1),
        };
        var rows = await RowsAsync(c, $"SHOW CREATE {kw} {Qualify(o.Schema, o.Name)}", ct);
        return rows.Count == 0 ? null : S(rows[0][col]);
    }

    public override async Task<bool> SchemaExistsAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        await ScalarAsync(c, "SELECT 1 FROM information_schema.schemata WHERE schema_name=@s", ct, ("@s", schema)) != null;

    public override Task CreateSchemaAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        ExecAsync(c, $"CREATE DATABASE IF NOT EXISTS {Quote(schema)}", ct);

    public override string NativeType(ColumnInfo col, bool isKey) => col.Type switch
    {
        CanonicalType.Bool => "tinyint(1)",
        CanonicalType.Int16 => "smallint",
        CanonicalType.Int32 => "int",
        CanonicalType.Int64 => "bigint",
        CanonicalType.Decimal => col.Precision is > 0 and <= 65 ? $"decimal({col.Precision},{Math.Clamp(col.Scale ?? 0, 0, Math.Min(30, col.Precision.Value))})" : "decimal(38,10)",
        CanonicalType.Float32 => "float",
        CanonicalType.Float64 => "double",
        CanonicalType.Date => "date",
        CanonicalType.Time => "time(6)",
        CanonicalType.DateTime or CanonicalType.DateTimeOffset => "datetime(6)",
        CanonicalType.String => col.Length is > 0 and <= 16383 ? $"varchar({(isKey ? Math.Min(col.Length.Value, 768) : col.Length)})" : isKey ? "varchar(255)" : "longtext",
        CanonicalType.FixedString => col.Length is > 0 and <= 255 ? $"char({col.Length})" : isKey ? "varchar(255)" : "longtext",
        CanonicalType.Binary => isKey ? "varbinary(255)" : "longblob",
        CanonicalType.Guid => "char(36)",
        CanonicalType.Json => isKey ? "varchar(255)" : "json",
        _ => isKey ? "varchar(255)" : "longtext",
    };

    public override int MaxBatchRows(int columnCount) => Math.Clamp(60000 / Math.Max(1, columnCount), 1, 1000);

    protected override string UpsertSuffix(TableInfo t)
    {
        var nonKey = t.Columns.Where(c => !TypeFacts.IsKey(t, c)).ToList();
        var set = nonKey.Count > 0 ? nonKey : t.Columns.Take(1).ToList(); // no-op update keeps the statement valid
        return " ON DUPLICATE KEY UPDATE " + string.Join(", ", set.Select(c => $"{Quote(c.Name)} = VALUES({Quote(c.Name)})"));
    }
}
