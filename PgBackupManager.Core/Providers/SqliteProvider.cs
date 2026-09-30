using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Providers;

// SQLite file databases. Profile.Database holds the file path; "schema" is
// the attached database name — normally just "main".
public sealed class SqliteProvider : DbProviderBase
{
    public override DbEngine Engine => DbEngine.Sqlite;
    public override string Name => "SQLite";
    public override ScriptDialect Dialect => ScriptDialect.Sqlite;

    public override DbConnection CreateConnection(string cs) => new SqliteConnection(cs);

    public override async Task<List<string>> ListSchemasAsync(DbConnection c, CancellationToken ct = default) =>
        (await RowsAsync(c, "PRAGMA database_list", ct)).Select(r => S(r[1])).Where(n => n != "temp").ToList();

    public override async Task<List<DbObjectInfo>> ListObjectsAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        (await RowsAsync(c, $"SELECT name, type FROM {Quote(schema)}.sqlite_master WHERE type IN ('table','view','trigger') AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 1 ELSE 2 END, name", ct))
        .Select(r => new DbObjectInfo(schema, S(r[0]), S(r[1]) switch
        {
            "view" => DbObjectType.View,
            "trigger" => DbObjectType.Trigger,
            _ => DbObjectType.Table,
        })).ToList();

    public override async Task<TableInfo?> GetTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        var rows = await RowsAsync(c, $"PRAGMA {Quote(schema)}.table_info({Quote(table)})", ct);
        if (rows.Count == 0) return null;
        var cols = rows.Select(r => MapColumn(S(r[1]), S(r[2]), Convert.ToInt64(r[3]) == 0 && Convert.ToInt64(r[5]) == 0)).ToList();
        var pk = rows.Where(r => Convert.ToInt64(r[5]) > 0).OrderBy(r => Convert.ToInt64(r[5])).Select(r => S(r[1])).ToList();
        return new TableInfo { Schema = schema, Name = table, Columns = cols, PrimaryKey = pk };
    }

    private static readonly Regex LenArg = new(@"\(\s*(\d+)(?:\s*,\s*(\d+))?\s*\)", RegexOptions.Compiled);

    // SQLite "type affinity" rules (https://sqlite.org/datatype3.html §3.1),
    // refined with the common declared names so dates/bools survive a transfer.
    internal static ColumnInfo MapColumn(string name, string declared, bool nullable)
    {
        var d = declared.ToUpperInvariant();
        var m = LenArg.Match(d);
        int? a1 = m.Success ? int.Parse(m.Groups[1].Value) : null;
        int? a2 = m.Success && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null;
        (CanonicalType, int?, int?, int?) t =
            d.Contains("BOOL") ? (CanonicalType.Bool, null, null, null) :
            d.Contains("INT") ? (CanonicalType.Int64, null, null, null) :
            d.Contains("DATETIME") || d.Contains("TIMESTAMP") ? (CanonicalType.DateTime, null, null, null) :
            d.StartsWith("DATE") ? (CanonicalType.Date, null, null, null) :
            d.StartsWith("TIME") ? (CanonicalType.Time, null, null, null) :
            d.Contains("CHAR") && a1.HasValue ? (CanonicalType.String, a1, null, null) :
            d.Contains("CHAR") || d.Contains("CLOB") || d.Contains("TEXT") ? (CanonicalType.Text, null, null, null) :
            d.Contains("BLOB") ? (CanonicalType.Binary, null, null, null) :
            d.Contains("REAL") || d.Contains("FLOA") || d.Contains("DOUB") ? (CanonicalType.Float64, null, null, null) :
            d.Contains("DEC") || d.Contains("NUM") ? (CanonicalType.Decimal, null, a1, a2 ?? (a1.HasValue ? 0 : null)) :
            d.Contains("JSON") ? (CanonicalType.Json, null, null, null) :
            (CanonicalType.Text, null, null, null);
        return new ColumnInfo(name, declared, t.Item1, t.Item2, t.Item3, t.Item4, nullable);
    }

    public override async Task<string?> GetObjectSourceAsync(DbConnection c, DbObjectInfo o, CancellationToken ct = default) =>
        S(await ScalarAsync(c, $"SELECT sql FROM {Quote(o.Schema)}.sqlite_master WHERE name=@n", ct, ("@n", o.Name)));

    public override async Task<bool> SchemaExistsAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        (await ListSchemasAsync(c, ct)).Contains(schema, StringComparer.OrdinalIgnoreCase);

    public override Task CreateSchemaAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        throw new InvalidOperationException($"SQLite has no CREATE SCHEMA — use 'main' (or ATTACH another file as '{schema}' in the SQL editor first).");

    public override string NativeType(ColumnInfo col, bool isKey) => col.Type switch
    {
        CanonicalType.Bool => "BOOLEAN",
        CanonicalType.Int16 or CanonicalType.Int32 or CanonicalType.Int64 => "INTEGER",
        CanonicalType.Decimal => col.Precision is > 0 ? $"NUMERIC({col.Precision},{col.Scale ?? 0})" : "NUMERIC",
        CanonicalType.Float32 or CanonicalType.Float64 => "REAL",
        CanonicalType.Date => "DATE",
        CanonicalType.Time => "TIME",
        CanonicalType.DateTime => "DATETIME",
        CanonicalType.DateTimeOffset => "TIMESTAMP",
        CanonicalType.String or CanonicalType.FixedString => col.Length is > 0 ? $"VARCHAR({col.Length})" : "TEXT",
        CanonicalType.Binary => "BLOB",
        CanonicalType.Json => "JSON",
        _ => "TEXT",
    };

    public override int MaxBatchRows(int columnCount) => Math.Clamp(30000 / Math.Max(1, columnCount), 1, 500);

    protected override string UpsertSuffix(TableInfo t)
    {
        var nonKey = t.Columns.Where(c => !TypeFacts.IsKey(t, c)).ToList();
        var conflict = $" ON CONFLICT ({string.Join(", ", t.PrimaryKey.Select(Quote))}) ";
        return nonKey.Count == 0 ? conflict + "DO NOTHING"
            : conflict + "DO UPDATE SET " + string.Join(", ", nonKey.Select(c => $"{Quote(c.Name)} = excluded.{Quote(c.Name)}"));
    }
}
