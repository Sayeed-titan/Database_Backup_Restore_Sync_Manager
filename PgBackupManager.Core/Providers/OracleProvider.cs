using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Providers;

// Pure-managed ODP.NET: no Oracle Instant Client / tnsnames needed — the
// profile's host/port/service name are enough.
public sealed class OracleProvider : DbProviderBase
{
    public override DbEngine Engine => DbEngine.Oracle;
    public override string Name => "Oracle";
    public override ScriptDialect Dialect => ScriptDialect.Oracle;
    protected override string ParamPrefix => ":";

    public override DbConnection CreateConnection(string cs) => new OracleConnection(cs);
    public override string NormalizeName(string name) => name.ToUpperInvariant();

    public override async Task<List<string>> ListSchemasAsync(DbConnection c, CancellationToken ct = default)
    {
        try
        {
            return (await RowsAsync(c, "SELECT username FROM all_users WHERE oracle_maintained = 'N' ORDER BY username", ct)).Select(r => S(r[0])).ToList();
        }
        catch (OracleException)
        {
            // Pre-12c: no ORACLE_MAINTAINED column — filter the well-known system accounts instead.
            return (await RowsAsync(c, @"SELECT username FROM all_users WHERE username NOT IN
                ('SYS','SYSTEM','OUTLN','DBSNMP','APPQOSSYS','XDB','ANONYMOUS','CTXSYS','MDSYS','ORDSYS','ORDDATA','ORDPLUGINS',
                 'SI_INFORMTN_SCHEMA','WMSYS','EXFSYS','OLAPSYS','MGMT_VIEW','SYSMAN','FLOWS_FILES','APEX_PUBLIC_USER','ORACLE_OCM',
                 'DIP','XS$NULL','SPATIAL_CSW_ADMIN_USR','SPATIAL_WFS_ADMIN_USR','MDDATA','OWBSYS','AUDSYS','GSMADMIN_INTERNAL','LBACSYS')
                ORDER BY username", ct)).Select(r => S(r[0])).ToList();
        }
    }

    public override async Task<List<DbObjectInfo>> ListObjectsAsync(DbConnection c, string schema, CancellationToken ct = default)
    {
        const string sql = @"
SELECT object_name, object_type FROM all_objects
WHERE owner = :s AND object_type IN ('TABLE','VIEW','FUNCTION','PROCEDURE','PACKAGE','SEQUENCE','TRIGGER','TYPE')
  AND object_name NOT LIKE 'BIN$%' AND object_name NOT LIKE 'SYS\_%' ESCAPE '\'
  AND NOT (object_type = 'TABLE' AND object_name IN (SELECT mview_name FROM all_mviews WHERE owner = :s))
ORDER BY DECODE(object_type,'TABLE',0,'VIEW',1,'PACKAGE',2,'PROCEDURE',3,'FUNCTION',4,'SEQUENCE',5,'TRIGGER',6,7), object_name";
        return (await RowsAsync(c, sql, ct, ("s", schema))).Select(r => new DbObjectInfo(schema, S(r[0]), S(r[1]) switch
        {
            "VIEW" => DbObjectType.View,
            "FUNCTION" => DbObjectType.Function,
            "PROCEDURE" => DbObjectType.Procedure,
            "PACKAGE" => DbObjectType.Package,
            "SEQUENCE" => DbObjectType.Sequence,
            "TRIGGER" => DbObjectType.Trigger,
            "TYPE" => DbObjectType.Type,
            _ => DbObjectType.Table,
        })).ToList();
    }

    public override async Task<TableInfo?> GetTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        List<object?[]> rows;
        try
        {
            rows = await RowsAsync(c, @"SELECT column_name, data_type, char_length, data_precision, data_scale, nullable, identity_column
                FROM all_tab_columns WHERE owner = :s AND table_name = :t ORDER BY column_id", ct, ("s", schema), ("t", table));
        }
        catch (OracleException)
        {
            rows = await RowsAsync(c, @"SELECT column_name, data_type, char_length, data_precision, data_scale, nullable, 'NO'
                FROM all_tab_columns WHERE owner = :s AND table_name = :t ORDER BY column_id", ct, ("s", schema), ("t", table));
        }
        if (rows.Count == 0) return null;

        var pk = (await RowsAsync(c, @"SELECT cc.column_name FROM all_constraints k
            JOIN all_cons_columns cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name AND cc.table_name = k.table_name
            WHERE k.owner = :s AND k.table_name = :t AND k.constraint_type = 'P' ORDER BY cc.position", ct, ("s", schema), ("t", table)))
            .Select(r => S(r[0])).ToList();

        return new TableInfo
        {
            Schema = schema,
            Name = table,
            Columns = rows.Select(r => MapColumn(S(r[0]), S(r[1]), I(r[2]), I(r[3]), I(r[4]), S(r[5]) == "Y", S(r[6]) == "YES")).ToList(),
            PrimaryKey = pk,
        };
    }

    internal static ColumnInfo MapColumn(string name, string dataType, int? charLen, int? precision, int? scale, bool nullable, bool identity)
    {
        var t = dataType.ToUpperInvariant();
        var baseType = Regex.Replace(t, @"\(\d+\)", "");
        var native = baseType switch
        {
            "NUMBER" when precision.HasValue => $"NUMBER({precision},{scale ?? 0})",
            "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" when charLen is > 0 => $"{baseType}({charLen})",
            _ => t,
        };
        (CanonicalType, int?, int?, int?) m = baseType switch
        {
            "NUMBER" when scale is 0 && precision is > 0 and <= 4 => (CanonicalType.Int16, null, null, null),
            "NUMBER" when scale is 0 && precision is > 0 and <= 9 => (CanonicalType.Int32, null, null, null),
            "NUMBER" when scale is 0 && precision is > 0 and <= 18 => (CanonicalType.Int64, null, null, null),
            "NUMBER" when precision.HasValue => (CanonicalType.Decimal, null, precision, scale ?? 0),
            "NUMBER" when scale is 0 => (CanonicalType.Decimal, null, 38, 0), // INTEGER
            "NUMBER" => (CanonicalType.Decimal, null, null, null),
            "FLOAT" or "BINARY_DOUBLE" => (CanonicalType.Float64, null, null, null),
            "BINARY_FLOAT" => (CanonicalType.Float32, null, null, null),
            "VARCHAR2" or "NVARCHAR2" or "VARCHAR" => (CanonicalType.String, charLen, null, null),
            "CHAR" or "NCHAR" => (CanonicalType.FixedString, charLen is > 0 ? charLen : 1, null, null),
            "CLOB" or "NCLOB" or "LONG" => (CanonicalType.Text, null, null, null),
            "DATE" => (CanonicalType.DateTime, null, null, null), // Oracle DATE carries a time part
            "RAW" or "LONG RAW" or "BLOB" => (CanonicalType.Binary, null, null, null),
            "XMLTYPE" => (CanonicalType.Xml, null, null, null),
            "JSON" => (CanonicalType.Json, null, null, null),
            _ when baseType.StartsWith("TIMESTAMP") && baseType.EndsWith("WITH TIME ZONE") && !baseType.Contains("LOCAL") => (CanonicalType.DateTimeOffset, null, null, null),
            _ when baseType.StartsWith("TIMESTAMP") => (CanonicalType.DateTime, null, null, null),
            _ when baseType.StartsWith("INTERVAL DAY") => (CanonicalType.Time, null, null, null),
            _ => (CanonicalType.Text, null, null, null),
        };
        return new ColumnInfo(name, native, m.Item1, m.Item2, m.Item3, m.Item4, nullable, identity);
    }

    public override async Task<string?> GetObjectSourceAsync(DbConnection c, DbObjectInfo o, CancellationToken ct = default)
    {
        var type = o.Type switch
        {
            DbObjectType.View => "VIEW",
            DbObjectType.Function => "FUNCTION",
            DbObjectType.Procedure => "PROCEDURE",
            DbObjectType.Package => "PACKAGE",
            DbObjectType.Sequence => "SEQUENCE",
            DbObjectType.Trigger => "TRIGGER",
            DbObjectType.Type => "TYPE",
            _ => "TABLE",
        };
        try
        {
            var ddl = S(await ScalarAsync(c, "SELECT DBMS_METADATA.GET_DDL(:t, :n, :s) FROM dual", ct, ("t", type), ("n", o.Name), ("s", o.Schema)));
            if (!string.IsNullOrWhiteSpace(ddl)) return ddl.Trim();
        }
        catch (OracleException) { /* no SELECT_CATALOG_ROLE — fall back below */ }

        if (o.Type == DbObjectType.Table)
        {
            var t = await GetTableAsync(c, o.Schema, o.Name, ct);
            return t is null ? null : BuildCreateTable(o.Schema, t);
        }
        if (o.Type == DbObjectType.View)
        {
            await using var cmd = (OracleCommand)Cmd(c, "SELECT text FROM all_views WHERE owner = :s AND view_name = :n", ("s", o.Schema), ("n", o.Name));
            cmd.InitialLONGFetchSize = -1;
            var text = S(await cmd.ExecuteScalarAsync(ct));
            return $"CREATE OR REPLACE VIEW {Qualify(o.Schema, o.Name)} AS\n{text}";
        }

        // PL/SQL: stitch ALL_SOURCE back together (spec first, then body).
        var types = o.Type == DbObjectType.Package ? new[] { "PACKAGE", "PACKAGE BODY" } : o.Type == DbObjectType.Type ? new[] { "TYPE", "TYPE BODY" } : new[] { type };
        var sb = new StringBuilder();
        foreach (var t in types)
        {
            var lines = await RowsAsync(c, "SELECT text FROM all_source WHERE owner = :s AND name = :n AND type = :t ORDER BY line", ct, ("s", o.Schema), ("n", o.Name), ("t", t));
            if (lines.Count == 0) continue;
            sb.Append("CREATE OR REPLACE ");
            foreach (var l in lines) sb.Append(S(l[0]));
            sb.AppendLine().AppendLine("/");
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    public override async Task<bool> SchemaExistsAsync(DbConnection c, string schema, CancellationToken ct = default) =>
        await ScalarAsync(c, "SELECT 1 FROM all_users WHERE username = :s", ct, ("s", schema)) != null;

    // In Oracle a schema IS a user. NO AUTHENTICATION (18c+) makes a
    // schema-only account nobody can log into; it still needs quota to hold data.
    public override async Task CreateSchemaAsync(DbConnection c, string schema, CancellationToken ct = default)
    {
        try
        {
            await ExecAsync(c, $"CREATE USER {Quote(schema)} NO AUTHENTICATION", ct);
            await ExecAsync(c, $"ALTER USER {Quote(schema)} QUOTA UNLIMITED ON USERS", ct);
        }
        catch (OracleException ex)
        {
            throw new InvalidOperationException(
                $"Oracle schema '{schema}' doesn't exist and couldn't be created automatically ({ex.Message}). " +
                "In Oracle a schema is a user — ask a DBA to run CREATE USER + GRANT/QUOTA, or pick an existing schema.", ex);
        }
    }

    public override string NativeType(ColumnInfo col, bool isKey) => col.Type switch
    {
        CanonicalType.Bool => "NUMBER(1)",
        CanonicalType.Int16 => "NUMBER(5)",
        CanonicalType.Int32 => "NUMBER(10)",
        CanonicalType.Int64 => "NUMBER(19)",
        CanonicalType.Decimal => col.Precision is > 0 and <= 38 ? $"NUMBER({col.Precision},{Math.Clamp(col.Scale ?? 0, -84, 127)})" : "NUMBER",
        CanonicalType.Float32 => "BINARY_FLOAT",
        CanonicalType.Float64 => "BINARY_DOUBLE",
        CanonicalType.Date => "DATE",
        CanonicalType.DateTime => "TIMESTAMP",
        CanonicalType.DateTimeOffset => "TIMESTAMP WITH TIME ZONE",
        CanonicalType.Time => "INTERVAL DAY(0) TO SECOND(6)",
        CanonicalType.String => col.Length is > 0 and <= 4000 ? $"VARCHAR2({col.Length} CHAR)" : isKey ? "VARCHAR2(1000 CHAR)" : "CLOB",
        CanonicalType.FixedString => col.Length is > 0 and <= 2000 ? $"CHAR({col.Length} CHAR)" : isKey ? "VARCHAR2(1000 CHAR)" : "CLOB",
        CanonicalType.Binary => isKey ? "RAW(2000)" : "BLOB",
        CanonicalType.Guid => "VARCHAR2(36 CHAR)",
        _ => isKey ? "VARCHAR2(1000 CHAR)" : "CLOB",
    };

    public override string SelectAllSql(string schema, TableInfo t) =>
        "SELECT " + string.Join(", ", t.Columns.Select(c => c.NativeType == "XMLTYPE" ? $"x.{Quote(c.Name)}.getClobVal()" : $"x.{Quote(c.Name)}")) +
        $" FROM {Qualify(schema, t.Name)} x";

    public override string SelectTopSql(string schema, string table, int rows) => $"SELECT * FROM {Qualify(schema, table)} WHERE ROWNUM <= {rows}";

    public override Task DropTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default) =>
        ExecAsync(c, $"DROP TABLE {Qualify(schema, table)} CASCADE CONSTRAINTS PURGE", ct);

    public override int MaxBatchRows(int columnCount) => 2000;

    public override async Task<List<IndexInfo>> GetIndexesAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        // NORMAL / BITMAP indexes only (function-based ones are skipped); the PK's own index is excluded.
        const string sql = @"
SELECT i.index_name, i.uniqueness, LISTAGG(ic.column_name, CHR(1)) WITHIN GROUP (ORDER BY ic.column_position)
FROM all_indexes i
JOIN all_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
WHERE i.table_owner = :s AND i.table_name = :t AND i.index_type IN ('NORMAL', 'BITMAP')
  AND i.index_name NOT IN (SELECT NVL(index_name, '-') FROM all_constraints WHERE owner = :s AND table_name = :t AND constraint_type = 'P')
GROUP BY i.index_name, i.uniqueness
ORDER BY i.index_name";
        return (await RowsAsync(c, sql, ct, ("s", schema), ("t", table)))
            .Select(r => new IndexInfo(S(r[0]), Split1(r[2]), S(r[1]) == "UNIQUE")).ToList();
    }

    public override async Task<List<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        const string sql = @"
SELECT c.constraint_name,
       LISTAGG(cc.column_name, CHR(1)) WITHIN GROUP (ORDER BY cc.position),
       r.owner, r.table_name,
       (SELECT LISTAGG(rc.column_name, CHR(1)) WITHIN GROUP (ORDER BY rc.position)
          FROM all_cons_columns rc WHERE rc.owner = r.owner AND rc.constraint_name = r.constraint_name),
       c.delete_rule
FROM all_constraints c
JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
JOIN all_constraints r ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name
WHERE c.owner = :s AND c.table_name = :t AND c.constraint_type = 'R'
GROUP BY c.constraint_name, r.owner, r.table_name, r.constraint_name, c.delete_rule
ORDER BY c.constraint_name";
        return (await RowsAsync(c, sql, ct, ("s", schema), ("t", table)))
            .Select(r => new ForeignKeyInfo(S(r[0]), Split1(r[1]), S(r[2]), S(r[3]), Split1(r[4]), NormalizeAction(S(r[5])))).ToList();
    }

    public override string BuildAddColumn(string schema, string table, ColumnInfo col) =>
        $"ALTER TABLE {Qualify(schema, table)} ADD ({Quote(col.Name)} {NativeType(col, false)}{(col.Nullable ? "" : " NOT NULL")})";

    public override string? BuildAlterColumnType(string schema, string table, ColumnInfo col) =>
        $"ALTER TABLE {Qualify(schema, table)} MODIFY ({Quote(col.Name)} {NativeType(col, false)})";

    // Oracle: no ON UPDATE at all; ON DELETE only CASCADE / SET NULL.
    protected override string RefAction(string evt, string action) =>
        evt == "DELETE" && action is "CASCADE" or "SET NULL" ? $" ON DELETE {action}" : "";

    public override void PrepareReadCommand(DbCommand cmd)
    {
        cmd.CommandTimeout = 0;
        if (cmd is OracleCommand oc)
        {
            oc.InitialLOBFetchSize = -1;   // fetch whole CLOB/BLOB inline, not as locators
            oc.InitialLONGFetchSize = -1;
            oc.FetchSize = 4 * 1024 * 1024;
        }
    }

    // NUMBER holds up to 38 digits; .NET decimal only 28-29. Values beyond
    // that are rounded to 28 significant digits instead of failing the row.
    public override object? ReadValue(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        if (reader is OracleDataReader r && r.GetFieldType(ordinal) == typeof(decimal))
        {
            try { return r.GetDecimal(ordinal); }
            catch (InvalidCastException) { return OracleDecimal.SetPrecision(r.GetOracleDecimal(ordinal), 28).Value; }
            catch (OverflowException) { return OracleDecimal.SetPrecision(r.GetOracleDecimal(ordinal), 28).Value; }
        }
        return reader.GetValue(ordinal);
    }

    private static OracleDbType DbType(ColumnInfo c) => c.Type switch
    {
        CanonicalType.Bool or CanonicalType.Int16 => OracleDbType.Int16,
        CanonicalType.Int32 => OracleDbType.Int32,
        CanonicalType.Int64 => OracleDbType.Int64,
        CanonicalType.Decimal => OracleDbType.Decimal,
        CanonicalType.Float32 => OracleDbType.BinaryFloat,
        CanonicalType.Float64 => OracleDbType.BinaryDouble,
        CanonicalType.Date => OracleDbType.Date,
        CanonicalType.DateTime => OracleDbType.TimeStamp,
        CanonicalType.DateTimeOffset => OracleDbType.TimeStampTZ,
        CanonicalType.Time => OracleDbType.IntervalDS,
        CanonicalType.String or CanonicalType.FixedString or CanonicalType.Guid => OracleDbType.Varchar2,
        CanonicalType.Binary => c.NativeType.StartsWith("RAW") ? OracleDbType.Raw : OracleDbType.Blob,
        _ => c.NativeType is "NCLOB" ? OracleDbType.NClob : OracleDbType.Clob,
    };

    private static bool IsLob(ColumnInfo c) => DbType(c) is OracleDbType.Clob or OracleDbType.NClob or OracleDbType.Blob;

    private static object? Bindable(object? v, ColumnInfo c) => v switch
    {
        null => DBNull.Value,
        DateTimeOffset dto => new OracleTimeStampTZ(dto.UtcDateTime, "+00:00"),
        Guid g => g.ToString(),
        _ => v,
    };

    public override async Task WriteBatchAsync(DbConnection c, DbTransaction tx, string schema, TableInfo t, IReadOnlyList<object?[]> rows, WriteMode mode, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var conn = (OracleConnection)c;
        var binds = t.Columns.Select((_, i) => ":b" + i).ToList();
        string sql;
        if (mode == WriteMode.Upsert)
        {
            if (t.PrimaryKey.Count == 0) throw new InvalidOperationException($"Upsert needs a primary key on {t.Name}.");
            var src = string.Join(", ", t.Columns.Select((col, i) => $"{binds[i]} AS {Quote(col.Name)}"));
            var on = string.Join(" AND ", t.PrimaryKey.Select(k => $"T.{Quote(k)} = S.{Quote(k)}"));
            var nonKey = t.Columns.Where(x => !TypeFacts.IsKey(t, x)).ToList();
            sql = $"MERGE INTO {Qualify(schema, t.Name)} T USING (SELECT {src} FROM dual) S ON ({on}) " +
                  (nonKey.Count > 0 ? "WHEN MATCHED THEN UPDATE SET " + string.Join(", ", nonKey.Select(x => $"T.{Quote(x.Name)} = S.{Quote(x.Name)}")) + " " : "") +
                  $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", t.Columns.Select(x => Quote(x.Name)))}) VALUES ({string.Join(", ", t.Columns.Select(x => "S." + Quote(x.Name)))})";
        }
        else
        {
            sql = $"INSERT INTO {Qualify(schema, t.Name)} ({string.Join(", ", t.Columns.Select(x => Quote(x.Name)))}) VALUES ({string.Join(", ", binds)})";
        }

        // Array binding (one round trip per batch) unless LOB columns are
        // involved — those go row by row, which ODP.NET handles reliably.
        if (!t.Columns.Any(IsLob))
        {
            await using var cmd = new OracleCommand(sql, conn) { BindByName = false, ArrayBindCount = rows.Count, CommandTimeout = 0, Transaction = (OracleTransaction)tx };
            for (int i = 0; i < t.Columns.Count; i++)
            {
                var col = t.Columns[i];
                cmd.Parameters.Add(new OracleParameter { OracleDbType = DbType(col), Value = rows.Select(r => Bindable(r[i], col)).ToArray() });
            }
            await cmd.ExecuteNonQueryAsync(ct);
            return;
        }

        await using var single = new OracleCommand(sql, conn) { BindByName = false, CommandTimeout = 0, Transaction = (OracleTransaction)tx };
        var prms = t.Columns.Select(col => single.Parameters.Add(new OracleParameter { OracleDbType = DbType(col) })).ToList();
        foreach (var r in rows)
        {
            for (int i = 0; i < prms.Count; i++) prms[i].Value = Bindable(r[i], t.Columns[i]);
            await single.ExecuteNonQueryAsync(ct);
        }
    }
}
