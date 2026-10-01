using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;

namespace PgBackupManager.Core.Providers;

public enum WriteMode { Insert, Upsert }

public enum ScriptDialect { Postgres, SqlServer, Oracle, MySql, Sqlite }

// One engine's catalog reading, DDL generation and bulk writing. The Transfer
// runner, SQL editor and object explorer only ever talk to this interface.
public interface IDbProvider
{
    DbEngine Engine { get; }
    string Name { get; }
    ScriptDialect Dialect { get; }

    DbConnection CreateConnection(string connectionString);
    Task<DbConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct = default);

    string Quote(string identifier);
    string Qualify(string? schema, string name);
    // How an unquoted identifier would be stored by this engine (PG lowercases,
    // Oracle uppercases, the rest preserve) — used for the "target default" name case.
    string NormalizeName(string name);

    Task<List<string>> ListSchemasAsync(DbConnection c, CancellationToken ct = default);
    // Other databases on the same server this login can open (empty when the engine has no such notion).
    Task<List<string>> ListDatabasesAsync(DbConnection c, CancellationToken ct = default);
    Task<List<DbObjectInfo>> ListObjectsAsync(DbConnection c, string schema, CancellationToken ct = default);
    Task<TableInfo?> GetTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default);
    Task<string?> GetObjectSourceAsync(DbConnection c, DbObjectInfo obj, CancellationToken ct = default);
    Task<long> CountRowsAsync(DbConnection c, string schema, string table, CancellationToken ct = default);

    Task<bool> SchemaExistsAsync(DbConnection c, string schema, CancellationToken ct = default);
    Task CreateSchemaAsync(DbConnection c, string schema, CancellationToken ct = default);

    string NativeType(ColumnInfo col, bool isKey);
    string BuildCreateTable(string schema, TableInfo table);
    string SelectAllSql(string schema, TableInfo table);
    string SelectTopSql(string schema, string table, int rows);

    Task TruncateAsync(DbConnection c, DbTransaction? tx, string schema, string table, CancellationToken ct = default);
    Task DropTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default);

    // Rows arrive already coerced (ValueCoercer) and ordered to match table.Columns.
    Task WriteBatchAsync(DbConnection c, DbTransaction tx, string schema, TableInfo table, IReadOnlyList<object?[]> rows, WriteMode mode, CancellationToken ct = default);

    // Largest batch that stays within this engine's bind-parameter limits.
    int MaxBatchRows(int columnCount);

    // Secondary indexes + foreign keys (second pass of a transfer, after the data).
    Task<List<IndexInfo>> GetIndexesAsync(DbConnection c, string schema, string table, CancellationToken ct = default);
    Task<List<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection c, string schema, string table, CancellationToken ct = default);
    string BuildCreateIndex(string schema, TableInfo table, IndexInfo index);
    // null = this engine can't add a foreign key to an existing table (SQLite).
    string? BuildAddForeignKey(string schema, string table, ForeignKeyInfo fk);
    int MaxIdentifierLength { get; }

    // Schema-compare sync scripts. null = not supported by this engine.
    string BuildAddColumn(string schema, string table, ColumnInfo col);
    string? BuildAlterColumnType(string schema, string table, ColumnInfo col);

    // Reader hooks for streaming a table out (LOB fetch sizes, oversized numbers...).
    void PrepareReadCommand(DbCommand cmd);
    object? ReadValue(DbDataReader reader, int ordinal);
}

public abstract class DbProviderBase : IDbProvider
{
    public abstract DbEngine Engine { get; }
    public abstract string Name { get; }
    public abstract ScriptDialect Dialect { get; }
    protected virtual string ParamPrefix => "@";

    public abstract DbConnection CreateConnection(string connectionString);

    public virtual async Task<DbConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var conn = CreateConnection(profile.BuildConnectionString(SecretProtector.Unprotect(profile.EncryptedPasswordBase64)));
        try { await conn.OpenAsync(ct); }
        catch { await conn.DisposeAsync(); throw; }
        return conn;
    }

    public virtual string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    public virtual string Qualify(string? schema, string name) => string.IsNullOrEmpty(schema) ? Quote(name) : Quote(schema) + "." + Quote(name);
    public virtual string NormalizeName(string name) => name;

    public abstract Task<List<string>> ListSchemasAsync(DbConnection c, CancellationToken ct = default);
    public virtual Task<List<string>> ListDatabasesAsync(DbConnection c, CancellationToken ct = default) => Task.FromResult(new List<string>());
    public abstract Task<List<DbObjectInfo>> ListObjectsAsync(DbConnection c, string schema, CancellationToken ct = default);
    public abstract Task<TableInfo?> GetTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default);
    public abstract Task<string?> GetObjectSourceAsync(DbConnection c, DbObjectInfo obj, CancellationToken ct = default);
    public abstract Task<bool> SchemaExistsAsync(DbConnection c, string schema, CancellationToken ct = default);
    public abstract Task CreateSchemaAsync(DbConnection c, string schema, CancellationToken ct = default);
    public abstract string NativeType(ColumnInfo col, bool isKey);

    public virtual async Task<long> CountRowsAsync(DbConnection c, string schema, string table, CancellationToken ct = default)
    {
        var v = await ScalarAsync(c, $"SELECT COUNT(*) FROM {Qualify(schema, table)}", ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    public virtual string BuildCreateTable(string schema, TableInfo t)
    {
        var cols = t.Columns.Select(c => $"{Quote(c.Name)} {NativeType(c, TypeFacts.IsKey(t, c))}" + (c.Nullable && !TypeFacts.IsKey(t, c) ? "" : " NOT NULL"));
        var pk = t.PrimaryKey.Count > 0 ? $",\n  PRIMARY KEY ({string.Join(", ", t.PrimaryKey.Select(Quote))})" : "";
        return $"CREATE TABLE {Qualify(schema, t.Name)} (\n  {string.Join(",\n  ", cols)}{pk}\n)";
    }

    public virtual string SelectAllSql(string schema, TableInfo t) =>
        $"SELECT {string.Join(", ", t.Columns.Select(c => Quote(c.Name)))} FROM {Qualify(schema, t.Name)}";

    public virtual string SelectTopSql(string schema, string table, int rows) => $"SELECT * FROM {Qualify(schema, table)} LIMIT {rows}";

    public virtual async Task TruncateAsync(DbConnection c, DbTransaction? tx, string schema, string table, CancellationToken ct = default) =>
        await ExecAsync(c, $"DELETE FROM {Qualify(schema, table)}", ct, tx);

    public virtual async Task DropTableAsync(DbConnection c, string schema, string table, CancellationToken ct = default) =>
        await ExecAsync(c, $"DROP TABLE {Qualify(schema, table)}", ct);

    public virtual int MaxBatchRows(int columnCount) => Math.Clamp(2000 / Math.Max(1, columnCount), 1, 500);

    public virtual void PrepareReadCommand(DbCommand cmd) => cmd.CommandTimeout = 0;
    public virtual object? ReadValue(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);

    // Generic multi-row INSERT (... VALUES (...),(...)). Engines with a real
    // bulk path (COPY, SqlBulkCopy, array binding) override this.
    public virtual async Task WriteBatchAsync(DbConnection c, DbTransaction tx, string schema, TableInfo t, IReadOnlyList<object?[]> rows, WriteMode mode, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        var sb = new System.Text.StringBuilder();
        sb.Append("INSERT INTO ").Append(Qualify(schema, t.Name)).Append(" (")
          .Append(string.Join(", ", t.Columns.Select(col => Quote(col.Name)))).Append(") VALUES ");
        int p = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0) sb.Append(',');
            sb.Append('(');
            for (int i = 0; i < t.Columns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var name = "p" + p++;
                sb.Append(ParamPrefix).Append(name);
                var prm = cmd.CreateParameter();
                prm.ParameterName = ParamPrefix == ":" ? name : ParamPrefix + name;
                prm.Value = rows[r][i] ?? DBNull.Value;
                cmd.Parameters.Add(prm);
            }
            sb.Append(')');
        }
        if (mode == WriteMode.Upsert) sb.Append(UpsertSuffix(t));
        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public virtual int MaxIdentifierLength => 128;

    public virtual string BuildAddColumn(string schema, string table, ColumnInfo col) =>
        $"ALTER TABLE {Qualify(schema, table)} ADD {Quote(col.Name)} {NativeType(col, false)}{(col.Nullable ? "" : " NOT NULL")}";

    public virtual string? BuildAlterColumnType(string schema, string table, ColumnInfo col) =>
        $"ALTER TABLE {Qualify(schema, table)} ALTER COLUMN {Quote(col.Name)} TYPE {NativeType(col, false)}";
    public virtual Task<List<IndexInfo>> GetIndexesAsync(DbConnection c, string schema, string table, CancellationToken ct = default) => Task.FromResult(new List<IndexInfo>());
    public virtual Task<List<ForeignKeyInfo>> GetForeignKeysAsync(DbConnection c, string schema, string table, CancellationToken ct = default) => Task.FromResult(new List<ForeignKeyInfo>());

    public virtual string BuildCreateIndex(string schema, TableInfo table, IndexInfo ix) =>
        $"CREATE {(ix.Unique ? "UNIQUE " : "")}INDEX {Quote(ix.Name)} ON {Qualify(schema, table.Name)} ({string.Join(", ", ix.Columns.Select(Quote))})";

    public virtual string? BuildAddForeignKey(string schema, string table, ForeignKeyInfo fk) =>
        $"ALTER TABLE {Qualify(schema, table)} ADD CONSTRAINT {Quote(fk.Name)} FOREIGN KEY ({string.Join(", ", fk.Columns.Select(Quote))}) " +
        $"REFERENCES {Qualify(fk.RefSchema, fk.RefTable)} ({string.Join(", ", fk.RefColumns.Select(Quote))})" +
        RefAction("DELETE", fk.OnDelete) + RefAction("UPDATE", fk.OnUpdate);

    protected virtual string RefAction(string evt, string action) =>
        string.IsNullOrEmpty(action) || action == "NO ACTION" ? "" : $" ON {evt} {action}";

    // Engines spell referential actions differently ("SET_NULL", "n", "SET NULL"...).
    public static string NormalizeAction(string? a) => (a ?? "").Trim().ToUpperInvariant().Replace('_', ' ') switch
    {
        "C" or "CASCADE" => "CASCADE",
        "N" or "SET NULL" => "SET NULL",
        "D" or "SET DEFAULT" => "SET DEFAULT",
        "R" or "RESTRICT" => "RESTRICT",
        _ => "NO ACTION",
    };

    protected static List<string> Split1(object? v) =>
        v switch
        {
            null or DBNull => new List<string>(),
            string[] arr => arr.ToList(),
            _ => S(v).Split('\u0001', StringSplitOptions.RemoveEmptyEntries).ToList(),
        };

    protected virtual string UpsertSuffix(TableInfo t) => throw new NotSupportedException($"{Name} does not support upsert.");

    // ------------------------------------------------------------ helpers

    protected static async Task<object?> ScalarAsync(DbConnection c, string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var cmd = Cmd(c, sql, args);
        return await cmd.ExecuteScalarAsync(ct);
    }

    protected static async Task ExecAsync(DbConnection c, string sql, CancellationToken ct, DbTransaction? tx = null, params (string, object?)[] args)
    {
        await using var cmd = Cmd(c, sql, args);
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    protected static async Task<List<object?[]>> RowsAsync(DbConnection c, string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var cmd = Cmd(c, sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<object?[]>();
        while (await r.ReadAsync(ct))
        {
            var row = new object?[r.FieldCount];
            for (int i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    protected static DbCommand Cmd(DbConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        // ODP.NET binds by POSITION unless told otherwise.
        if (cmd is Oracle.ManagedDataAccess.Client.OracleCommand oc) oc.BindByName = true;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    protected static string S(object? v) => v?.ToString() ?? "";
    protected static int? I(object? v) => v is null or DBNull ? null : Convert.ToInt32(v);
}

public static class DbProviders
{
    private static readonly Dictionary<DbEngine, IDbProvider> All = new()
    {
        [DbEngine.PostgreSql] = new PostgresProvider(),
        [DbEngine.SqlServer] = new SqlServerProvider(),
        [DbEngine.Oracle] = new OracleProvider(),
        [DbEngine.MySql] = new MySqlProvider(),
        [DbEngine.Sqlite] = new SqliteProvider(),
    };

    public static IDbProvider For(DbEngine engine) => All[engine];
    public static IDbProvider For(ConnectionProfile p) => All[p.Engine];
}
