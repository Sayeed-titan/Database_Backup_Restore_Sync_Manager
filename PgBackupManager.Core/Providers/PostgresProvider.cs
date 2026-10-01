using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Providers;

public sealed class PostgresProvider : DbProviderBase
{
    public override DbEngine Engine => DbEngine.PostgreSql;
    public override string Name => "PostgreSQL";
    public override ScriptDialect Dialect => ScriptDialect.Postgres;

    public override DbConnection CreateConnection(string cs) => new NpgsqlConnection(cs);
    public override string NormalizeName(string name) => name.ToLowerInvariant();

    public override async Task<List<string>> ListDatabasesAsync(DbConnection c, CancellationToken ct = default) =>
        (await RowsAsync(c, "SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn ORDER BY 1", ct)).Select(r => S(r[0])).ToList();

    public override async Task<List<string>> ListSchemasAsync(DbConnection c, CancellationToken ct = default) =>
        (await RowsAsync(c, @"SELECT nspname FROM pg_namespace
            WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' ORDER BY 1", ct))
        .Select(r => S(r[0])).ToList();

    public override async Task<List<DbObjectInfo>> ListObjectsAsync(DbConnection c, string schema, CancellationToken ct = default)
    {
        const string sql = @"
SELECT c.relname, CASE c.relkind WHEN 'v' THEN 'V' WHEN 'm' THEN 'V' WHEN 'S' THEN 'S' ELSE 'T' END
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname=@s AND c.relkind IN ('r','p','v','m','S')
UNION ALL
SELECT p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')', CASE p.prokind WHEN 'p' THEN 'P' ELSE 'F' END
FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname=@s AND p.prokind IN ('f','p')
ORDER BY 2, 1";
        return (await RowsAsync(c, sql, ct, ("s", schema))).Select(r => new DbObjectInfo(schema, S(r[0]), S(r[1]) switch
        {
            "V" => DbObjectType.View,
            "S" => DbObjectType.Sequence,
            "P" => DbObjectType.Procedure,
            "F" => DbObjectType.Function,
            _ => DbObjectType.Table,
        })).ToList();
    }

    public override async Task<TableInfo?> GetTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        const string colSql = @"
SELECT a.attname, format_type(a.atttypid, a.atttypmod), t.typname, t.typcategory, NOT a.attnotnull,
       (a.attidentity <> '' OR COALESCE(pg_get_expr(d.adbin, d.adrelid), '') LIKE 'nextval(%')
FROM pg_attribute a
JOIN pg_class c ON c.oid=a.attrelid
JOIN pg_namespace n ON n.oid=c.relnamespace
JOIN pg_type t ON t.oid=a.atttypid
LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
WHERE n.nspname=@s AND c.relname=@t AND a.attnum>0 AND NOT a.attisdropped
ORDER BY a.attnum";
        var rows = await RowsAsync(c, colSql, ct, ("s", schema), ("t", table));
        if (rows.Count == 0) return null;

        const string pkSql = @"
SELECT a.attname
FROM pg_index i
JOIN pg_class c ON c.oid=i.indrelid
JOIN pg_namespace n ON n.oid=c.relnamespace
JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum, ord) ON true
JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=k.attnum
WHERE i.indisprimary AND n.nspname=@s AND c.relname=@t
ORDER BY k.ord";
        var pk = (await RowsAsync(c, pkSql, ct, ("s", schema), ("t", table))).Select(r => S(r[0])).ToList();

        return new TableInfo
        {
            Schema = schema,
            Name = table,
            Columns = rows.Select(r => MapColumn(S(r[0]), S(r[1]), S(r[2]), S(r[3]), (bool)r[4]!, (bool)r[5]!)).ToList(),
            PrimaryKey = pk,
        };
    }

    private static readonly Regex TypeArgs = new(@"\((\d+)(?:\s*,\s*(\d+))?\)", RegexOptions.Compiled);

    internal static ColumnInfo MapColumn(string name, string fmt, string typname, string category, bool nullable, bool identity)
    {
        var m = TypeArgs.Match(fmt);
        int? a1 = m.Success ? int.Parse(m.Groups[1].Value) : null;
        int? a2 = m.Success && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null;
        var (type, len, prec, scale) = category == "A" ? (CanonicalType.Text, (int?)null, (int?)null, (int?)null) : typname switch
        {
            "int2" => (CanonicalType.Int16, null, null, null),
            "int4" => (CanonicalType.Int32, null, null, null),
            "int8" => (CanonicalType.Int64, null, null, null),
            "float4" => (CanonicalType.Float32, null, null, null),
            "float8" => (CanonicalType.Float64, null, null, null),
            "numeric" => (CanonicalType.Decimal, null, a1, a2 ?? (a1.HasValue ? 0 : null)),
            "money" => (CanonicalType.Decimal, null, 19, 2),
            "bool" => (CanonicalType.Bool, null, null, null),
            "date" => (CanonicalType.Date, null, null, null),
            "time" => (CanonicalType.Time, null, null, null),
            "timestamp" => (CanonicalType.DateTime, null, null, null),
            "timestamptz" => (CanonicalType.DateTimeOffset, null, null, null),
            "varchar" => (CanonicalType.String, a1, null, null),
            "bpchar" => (CanonicalType.FixedString, a1 ?? 1, null, null),
            "text" or "name" or "citext" => (CanonicalType.Text, null, null, null),
            "bytea" => (CanonicalType.Binary, null, null, null),
            "uuid" => (CanonicalType.Guid, null, null, null),
            "json" or "jsonb" => (CanonicalType.Json, null, null, null),
            "xml" => (CanonicalType.Xml, null, null, null),
            _ => (CanonicalType.Text, null, null, null),
        };
        return new ColumnInfo(name, fmt, type, len, prec, scale, nullable, identity);
    }

    public override async Task<string?> GetObjectSourceAsync(DbConnection c, DbObjectInfo o, CancellationToken ct = default)
    {
        switch (o.Type)
        {
            case DbObjectType.View:
                return S(await ScalarAsync(c, @"SELECT 'CREATE OR REPLACE VIEW ' || quote_ident(n.nspname) || '.' || quote_ident(c.relname) || ' AS' || E'\n' || pg_get_viewdef(c.oid, true)
                    FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@s AND c.relname=@n", ct, ("s", o.Schema), ("n", o.Name)));
            case DbObjectType.Function or DbObjectType.Procedure:
                return S(await ScalarAsync(c, @"SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname=@s AND p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')' = @n", ct, ("s", o.Schema), ("n", o.Name)));
            case DbObjectType.Sequence:
                return S(await ScalarAsync(c, @"SELECT format('CREATE SEQUENCE %I.%I INCREMENT %s MINVALUE %s MAXVALUE %s START %s;', schemaname, sequencename, increment_by, min_value, max_value, COALESCE(last_value, start_value))
                    FROM pg_sequences WHERE schemaname=@s AND sequencename=@n", ct, ("s", o.Schema), ("n", o.Name)));
            default:
                var t = await GetTableAsync(c, o.Schema, o.Name, ct);
                return t is null ? null : BuildCreateTable(o.Schema, t) + ";";
        }
    }

    public override async Task<bool> SchemaExistsAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        await ScalarAsync(c, "SELECT 1 FROM pg_namespace WHERE nspname=@s", ct, ("s", schema)) != null;

    public override Task CreateSchemaAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        ExecAsync(c, $"CREATE SCHEMA IF NOT EXISTS {Quote(schema)}", ct);

    public override string NativeType(ColumnInfo col, bool isKey) => col.Type switch
    {
        CanonicalType.Bool => "boolean",
        CanonicalType.Int16 => "smallint",
        CanonicalType.Int32 => "integer",
        CanonicalType.Int64 => "bigint",
        CanonicalType.Decimal => col.Precision is > 0 and <= 1000 ? $"numeric({col.Precision},{Math.Clamp(col.Scale ?? 0, 0, col.Precision.Value)})" : "numeric",
        CanonicalType.Float32 => "real",
        CanonicalType.Float64 => "double precision",
        CanonicalType.Date => "date",
        CanonicalType.Time => "time",
        CanonicalType.DateTime => "timestamp",
        CanonicalType.DateTimeOffset => "timestamptz",
        CanonicalType.String => col.Length is > 0 and <= 10485760 ? $"varchar({col.Length})" : "text",
        CanonicalType.FixedString => col.Length is > 0 and <= 10485760 ? $"char({col.Length})" : "text",
        CanonicalType.Binary => "bytea",
        CanonicalType.Guid => "uuid",
        CanonicalType.Json => "jsonb",
        CanonicalType.Xml => "xml",
        _ => "text",
    };

    public override async Task TruncateAsync(DbConnection c, DbTransaction? tx, string schema, string table, CancellationToken ct = default) =>
        await ExecAsync(c, $"TRUNCATE TABLE {Qualify(schema, table)}", ct, tx);

    public override Task DropTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default) =>
        ExecAsync(c, $"DROP TABLE IF EXISTS {Qualify(schema, table)} CASCADE", ct);

    public override int MaxBatchRows(int columnCount) => 5000;

    public override int MaxIdentifierLength => 63;

    public override async Task<List<IndexInfo>> GetIndexesAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        // Key columns only (indnkeyatts); expression (attnum 0) and partial indexes are skipped.
        const string sql = @"
SELECT i.relname, ix.indisunique,
       array_agg(a.attname ORDER BY k.ord) FILTER (WHERE a.attname IS NOT NULL),
       bool_or(k.attnum = 0)
FROM pg_index ix
JOIN pg_class t ON t.oid = ix.indrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
JOIN pg_class i ON i.oid = ix.indexrelid
JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY k(attnum, ord) ON k.ord <= ix.indnkeyatts
LEFT JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
WHERE n.nspname = @s AND t.relname = @t AND NOT ix.indisprimary AND ix.indpred IS NULL
GROUP BY i.relname, ix.indisunique
ORDER BY 1";
        return (await RowsAsync(c, sql, ct, ("s", schema), ("t", table)))
            .Where(r => !(bool)r[3]! && r[2] != null)
            .Select(r => new IndexInfo(S(r[0]), Split1(r[2]), (bool)r[1]!)).ToList();
    }

    public override async Task<List<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
SELECT c.conname,
       (SELECT array_agg(a.attname ORDER BY k.ord) FROM unnest(c.conkey) WITH ORDINALITY k(n, ord) JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.n),
       rn.nspname, rt.relname,
       (SELECT array_agg(a.attname ORDER BY k.ord) FROM unnest(c.confkey) WITH ORDINALITY k(n, ord) JOIN pg_attribute a ON a.attrelid = c.confrelid AND a.attnum = k.n),
       c.confdeltype::text, c.confupdtype::text
FROM pg_constraint c
JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace
JOIN pg_class rt ON rt.oid = c.confrelid JOIN pg_namespace rn ON rn.oid = rt.relnamespace
WHERE c.contype = 'f' AND n.nspname = @s AND t.relname = @t
ORDER BY 1";
        return (await RowsAsync(c, sql, ct, ("s", schema), ("t", table)))
            .Select(r => new ForeignKeyInfo(S(r[0]), Split1(r[1]), S(r[2]), S(r[3]), Split1(r[4]), NormalizeAction(S(r[5])), NormalizeAction(S(r[6])))).ToList();
    }

    // Native types the binary COPY protocol can write straight from the
    // coerced .NET value. Anything else (enums, arrays, interval, money,
    // timetz, domains...) goes through INSERT with an explicit text cast.
    private static readonly Regex BinarySafeType = new(
        @"^(smallint|integer|bigint|real|double precision|boolean|date|text|bytea|uuid|json|jsonb|xml|" +
        @"numeric(\(\d+(,\d+)?\))?|character varying(\(\d+\))?|character(\(\d+\))?|" +
        @"time(\(\d+\))? without time zone|timestamp(\(\d+\))? without time zone|timestamp(\(\d+\))? with time zone)$",
        RegexOptions.Compiled);

    private static bool IsBinarySafe(ColumnInfo c) => BinarySafeType.IsMatch(c.NativeType);
    private static NpgsqlDbType DbType(CanonicalType t) => t switch
    {
        CanonicalType.Bool => NpgsqlDbType.Boolean,
        CanonicalType.Int16 => NpgsqlDbType.Smallint,
        CanonicalType.Int32 => NpgsqlDbType.Integer,
        CanonicalType.Int64 => NpgsqlDbType.Bigint,
        CanonicalType.Decimal => NpgsqlDbType.Numeric,
        CanonicalType.Float32 => NpgsqlDbType.Real,
        CanonicalType.Float64 => NpgsqlDbType.Double,
        CanonicalType.Date => NpgsqlDbType.Date,
        CanonicalType.Time => NpgsqlDbType.Time,
        CanonicalType.DateTime => NpgsqlDbType.Timestamp,
        CanonicalType.DateTimeOffset => NpgsqlDbType.TimestampTz,
        CanonicalType.String => NpgsqlDbType.Varchar,
        CanonicalType.FixedString => NpgsqlDbType.Char,
        CanonicalType.Binary => NpgsqlDbType.Bytea,
        CanonicalType.Guid => NpgsqlDbType.Uuid,
        CanonicalType.Json => NpgsqlDbType.Jsonb,
        CanonicalType.Xml => NpgsqlDbType.Xml,
        _ => NpgsqlDbType.Text,
    };

    // A json column fed through binary COPY must be sent as Json, not Jsonb.
    private static NpgsqlDbType DbTypeFor(ColumnInfo c) =>
        c.Type == CanonicalType.Json && c.NativeType == "json" ? NpgsqlDbType.Json : DbType(c.Type);

    public override async Task WriteBatchAsync(DbConnection c, DbTransaction tx, string schema, TableInfo t, IReadOnlyList<object?[]> rows, WriteMode mode, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var conn = (NpgsqlConnection)c;
        var colList = string.Join(", ", t.Columns.Select(x => Quote(x.Name)));

        if (!t.Columns.All(IsBinarySafe))
        {
            await InsertWithCastsAsync(conn, (NpgsqlTransaction)tx, schema, t, rows, mode, ct);
            return;
        }

        var dest = Qualify(schema, t.Name);
        string? stage = null;
        if (mode == WriteMode.Upsert)
        {
            if (t.PrimaryKey.Count == 0) throw new InvalidOperationException($"Upsert needs a primary key on {t.Name}.");
            stage = Quote("_pgbm_stage_" + Math.Abs(t.Name.GetHashCode()));
            await ExecAsync(c, $"CREATE TEMP TABLE IF NOT EXISTS {stage} (LIKE {dest}) ON COMMIT DROP", ct, tx);
            await ExecAsync(c, $"TRUNCATE {stage}", ct, tx);
            dest = stage;
        }

        var types = t.Columns.Select(DbTypeFor).ToArray();
        await using (var importer = await conn.BeginBinaryImportAsync($"COPY {dest} ({colList}) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var row in rows)
            {
                await importer.StartRowAsync(ct);
                for (int i = 0; i < types.Length; i++)
                {
                    if (row[i] is null) await importer.WriteNullAsync(ct);
                    else await importer.WriteAsync(row[i], types[i], ct);
                }
            }
            await importer.CompleteAsync(ct);
        }

        if (stage != null)
            await ExecAsync(c, $"INSERT INTO {Qualify(schema, t.Name)} ({colList}) SELECT {colList} FROM {stage}{UpsertSuffix(t)}", ct, tx);
    }

    private async Task InsertWithCastsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string schema, TableInfo t, IReadOnlyList<object?[]> rows, WriteMode mode, CancellationToken ct)
    {
        const int chunk = 500;
        for (int start = 0; start < rows.Count; start += chunk)
        {
            var slice = rows.Skip(start).Take(chunk).ToList();
            await using var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx, CommandTimeout = 0 };
            var sb = new StringBuilder($"INSERT INTO {Qualify(schema, t.Name)} ({string.Join(", ", t.Columns.Select(x => Quote(x.Name)))}) VALUES ");
            int p = 0;
            for (int r = 0; r < slice.Count; r++)
            {
                sb.Append(r > 0 ? ",(" : "(");
                for (int i = 0; i < t.Columns.Count; i++)
                {
                    var col = t.Columns[i];
                    if (i > 0) sb.Append(',');
                    var name = "p" + p++;
                    var v = slice[r][i];
                    if (IsBinarySafe(col))
                    {
                        sb.Append('@').Append(name);
                        cmd.Parameters.Add(new NpgsqlParameter(name, DbTypeFor(col)) { Value = v ?? DBNull.Value });
                    }
                    else
                    {
                        sb.Append("CAST(@").Append(name).Append(" AS ").Append(col.NativeType).Append(')');
                        cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = v is null ? DBNull.Value : ValueCoercer.ToText(v) });
                    }
                }
                sb.Append(')');
            }
            if (mode == WriteMode.Upsert) sb.Append(UpsertSuffix(t));
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    protected override string UpsertSuffix(TableInfo t)
    {
        var nonKey = t.Columns.Where(c => !TypeFacts.IsKey(t, c)).ToList();
        var conflict = $" ON CONFLICT ({string.Join(", ", t.PrimaryKey.Select(Quote))}) ";
        return nonKey.Count == 0 ? conflict + "DO NOTHING"
            : conflict + "DO UPDATE SET " + string.Join(", ", nonKey.Select(c => $"{Quote(c.Name)} = EXCLUDED.{Quote(c.Name)}"));
    }
}
