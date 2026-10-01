using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Sql;

namespace PgBackupManager.Core.Services;

public enum TableLoadMode
{
    CreateOrAppend,   // create when missing, append rows when it exists
    TruncateAndLoad,  // create when missing, empty it first when it exists
    DropAndRecreate,  // always rebuild structure from the source
    Upsert,           // create when missing, insert-or-update by primary key
    StructureOnly,    // create missing tables, copy no rows
}

public sealed class TransferOptions
{
    public required ConnectionProfile Source { get; init; }
    public required ConnectionProfile Target { get; init; }
    public required string SourceSchema { get; init; }
    public required string TargetSchema { get; init; }
    public IReadOnlyList<string> Tables { get; init; } = Array.Empty<string>();
    // Views / functions / procedures / packages / sequences / triggers.
    public IReadOnlyList<DbObjectInfo> CodeObjects { get; init; } = Array.Empty<DbObjectInfo>();
    public TableLoadMode Mode { get; init; } = TableLoadMode.CreateOrAppend;
    public NameCase NameCase { get; init; } = NameCase.TargetDefault;
    public bool DryRun { get; init; } = true;
    public bool ContinueOnError { get; init; } = true;
    // Second pass after the data: secondary indexes, then foreign keys — only
    // for tables this run CREATED (existing target tables are never altered).
    public bool CopyIndexes { get; init; } = true;
    public bool CopyForeignKeys { get; init; } = true;
    // Apply converted code to the target (otherwise it's only written to the script file).
    public bool ApplyCode { get; init; }
    // 0 = one transaction per table (all-or-nothing). N = commit every N rows (huge tables).
    public long CommitEveryRows { get; init; }
    // Optional WHERE clause (source dialect) applied to every selected table.
    public string? RowFilter { get; init; }
    public string? ScriptFolder { get; init; }
}

public sealed record TableTransferResult(string Table, bool Ok, long Rows, TimeSpan Elapsed, string? Error);

public sealed record TransferResult(bool Ok, string Summary, IReadOnlyList<TableTransferResult> Tables, string? ScriptPath);

public sealed record TransferProgress(string Table, int TableIndex, int TableCount, long Rows, long? EstimatedRows);

public sealed record CompareRow(string Table, int? SourceColumns, int? TargetColumns, long? SourceRows, long? TargetRows)
{
    public bool ExistsInSource => SourceColumns.HasValue;
    public bool ExistsInTarget => TargetColumns.HasValue;
    public bool Matched => ExistsInSource && ExistsInTarget && SourceColumns == TargetColumns && SourceRows == TargetRows;
}

// Any engine -> any engine: table structure (type-mapped through
// CanonicalType), data (streamed, batched, bulk-written), and — where a
// converter exists — views/routines/packages as code. Source is only ever read.
public sealed class TransferRunner
{
    public event EventHandler<string>? LogLine;
    public event EventHandler<TransferProgress>? Progress;

    public async Task<TransferResult> RunAsync(TransferOptions o, CancellationToken ct = default)
    {
        var sp = DbProviders.For(o.Source);
        var tp = DbProviders.For(o.Target);
        var results = new List<TableTransferResult>();
        _created.Clear();
        var script = new StringBuilder();
        script.AppendLine($"-- Transfer script generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        script.AppendLine($"-- {sp.Name} {o.Source.Database}.{o.SourceSchema}  ->  {tp.Name} {o.Target.Database}.{o.TargetSchema}");
        script.AppendLine();

        Log($">> transfer {o.Tables.Count} table(s) + {o.CodeObjects.Count} code object(s): " +
            $"[{sp.Name}] {o.Source}.{o.SourceSchema}  ->  [{tp.Name}] {o.Target}.{o.TargetSchema}   mode={o.Mode}, names={o.NameCase}" +
            (o.DryRun ? "   [DRY RUN — nothing will be changed]" : ""));

        try
        {
            await using var src = await sp.OpenAsync(o.Source, ct);
            await using var tgt = await tp.OpenAsync(o.Target, ct);

            if (!await tp.SchemaExistsAsync(tgt, o.TargetSchema, ct))
            {
                Log($"  [schema] '{o.TargetSchema}' missing in target -> " + (o.DryRun ? "would create." : "creating..."));
                script.AppendLine($"-- create schema {o.TargetSchema}");
                if (!o.DryRun) await tp.CreateSchemaAsync(tgt, o.TargetSchema, ct);
            }

            for (int i = 0; i < o.Tables.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var table = o.Tables[i];
                var sw = Stopwatch.StartNew();
                try
                {
                    var rows = await TransferTableAsync(sp, tp, src, tgt, o, table, i, script, ct);
                    results.Add(new TableTransferResult(table, true, rows, sw.Elapsed, null));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"  [ERROR] {table}: {ex.Message}");
                    results.Add(new TableTransferResult(table, false, 0, sw.Elapsed, ex.Message));
                    if (!o.ContinueOnError) throw;
                }
            }

            if ((o.CopyIndexes || o.CopyForeignKeys) && _created.Count > 0)
                await CopyConstraintsAsync(sp, tp, src, tgt, o, script, ct);

            if (o.CodeObjects.Count > 0)
                await TransferCodeAsync(sp, tp, src, tgt, o, script, ct);
        }
        catch (OperationCanceledException)
        {
            Log(">> Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log($">> ERROR: {ex.Message}");
            var p = SaveScript(o, script);
            return new TransferResult(false, ex.Message, results, p);
        }

        var scriptPath = SaveScript(o, script);
        var failed = results.Count(r => !r.Ok);
        var total = results.Sum(r => r.Rows);
        var summary = o.DryRun
            ? $"Dry run complete — {o.Tables.Count} table(s) planned, nothing changed."
            : failed == 0 ? $"Transfer complete — {results.Count} table(s), {total:N0} row(s)."
            : $"Transfer finished with {failed} failed table(s) of {results.Count} — {total:N0} row(s) copied.";
        Log(">> " + summary);
        if (scriptPath != null) Log($">> script saved: {scriptPath}");
        return new TransferResult(failed == 0, summary, results, scriptPath);
    }

    private async Task<long> TransferTableAsync(IDbProvider sp, IDbProvider tp, DbConnection src, DbConnection tgt,
        TransferOptions o, string table, int index, StringBuilder script, CancellationToken ct)
    {
        var s = await sp.GetTableAsync(src, o.SourceSchema, table, ct)
                ?? throw new InvalidOperationException($"table '{table}' not found in source schema '{o.SourceSchema}'.");
        string Map(string n) => NameCasing.Apply(n, o.NameCase, tp.NormalizeName);
        var targetName = Map(s.Name);

        var odd = s.Columns.Where(c => c.Type == CanonicalType.Text && !LooksTextual(c.NativeType)).Select(c => $"{c.Name} ({c.NativeType})").ToList();
        if (odd.Count > 0) Log($"  [{table}] non-standard type(s) carried as text — double-check: {string.Join(", ", odd)}");

        var t = await tp.GetTableAsync(tgt, o.TargetSchema, targetName, ct);
        if (t != null && o.Mode == TableLoadMode.DropAndRecreate)
        {
            Log($"  [{table}] dropping existing target table (DropAndRecreate)" + (o.DryRun ? " [dry run]" : ""));
            script.AppendLine($"DROP TABLE {tp.Qualify(o.TargetSchema, targetName)};");
            if (!o.DryRun) await tp.DropTableAsync(tgt, o.TargetSchema, targetName, ct);
            t = null;
        }

        if (t == null)
        {
            var design = new TableInfo
            {
                Schema = o.TargetSchema,
                Name = targetName,
                Columns = s.Columns.Select(c => c.WithName(Map(c.Name))).ToList(),
                PrimaryKey = s.PrimaryKey.Select(Map).ToList(),
            };
            var ddl = tp.BuildCreateTable(o.TargetSchema, design);
            if (o.CopyForeignKeys && tp is SqliteProvider)
                ddl = await InlineSqliteForeignKeysAsync(sp, src, o, s.Name, ddl, Map, ct);
            script.AppendLine(ddl + ";").AppendLine();
            Log($"  [{table}] " + (o.DryRun ? "would CREATE" : "CREATE") + $" {tp.Qualify(o.TargetSchema, targetName)} ({design.Columns.Count} cols" +
                (design.PrimaryKey.Count > 0 ? $", PK {string.Join(",", design.PrimaryKey)}" : ", no PK") + ")");
            if (o.DryRun)
            {
                t = design; // plan against the design
            }
            else
            {
                await using (var cmd = tgt.CreateCommand()) { cmd.CommandText = ddl; cmd.CommandTimeout = 0; await cmd.ExecuteNonQueryAsync(ct); }
                // Re-read so value binding follows the target's REAL column types.
                t = await tp.GetTableAsync(tgt, o.TargetSchema, targetName, ct) ?? design;
            }
            _created.Add((s.Name, t));
        }
        else
        {
            Log($"  [{table}] target table exists -> " + o.Mode switch
            {
                TableLoadMode.TruncateAndLoad => "truncate + load",
                TableLoadMode.Upsert => "upsert by primary key",
                TableLoadMode.StructureOnly => "left alone (structure only)",
                _ => "append rows (structure left alone)",
            });
        }

        if (o.Mode == TableLoadMode.StructureOnly) return 0;

        // Column pairing: target column <- source column with the same (mapped) name.
        var pairs = new List<(ColumnInfo Tgt, int SrcIndex)>();
        foreach (var tc in t.Columns)
        {
            var si = s.Columns.FindIndex(sc => string.Equals(Map(sc.Name), tc.Name, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(sc.Name, tc.Name, StringComparison.OrdinalIgnoreCase));
            if (si >= 0) pairs.Add((tc, si));
        }
        var unused = s.Columns.Where((sc, i) => pairs.All(p => p.SrcIndex != i)).Select(c => c.Name).ToList();
        if (unused.Count > 0) Log($"  [{table}] source column(s) with no match in target, skipped: {string.Join(", ", unused)}");
        if (pairs.Count == 0) throw new InvalidOperationException("no matching columns between source and target.");

        var writeTable = new TableInfo { Schema = t.Schema, Name = t.Name, Columns = pairs.Select(p => p.Tgt).ToList(), PrimaryKey = t.PrimaryKey };
        var mode = o.Mode == TableLoadMode.Upsert ? WriteMode.Upsert : WriteMode.Insert;
        if (mode == WriteMode.Upsert && writeTable.PrimaryKey.Count == 0)
            throw new InvalidOperationException("Upsert needs a primary key on the target table — it has none.");

        long? estimate = null;
        try { estimate = await sp.CountRowsAsync(src, o.SourceSchema, table, ct); } catch { /* best effort */ }
        Log($"  [{table}] " + (o.DryRun ? $"would copy {estimate?.ToString("N0") ?? "?"} row(s)." : $"copying {estimate?.ToString("N0") ?? "?"} row(s)..."));
        if (o.DryRun) return 0;

        var readTable = new TableInfo { Schema = s.Schema, Name = s.Name, Columns = pairs.Select(p => s.Columns[p.SrcIndex]).ToList() };
        var selectSql = sp.SelectAllSql(o.SourceSchema, readTable);
        if (!string.IsNullOrWhiteSpace(o.RowFilter)) selectSql += " WHERE " + o.RowFilter;

        var batchSize = tp.MaxBatchRows(writeTable.Columns.Count);
        var batch = new List<object?[]>(batchSize);
        long total = 0, sinceCommit = 0;
        var tx = await tgt.BeginTransactionAsync(ct);
        try
        {
            if (o.Mode == TableLoadMode.TruncateAndLoad) await tp.TruncateAsync(tgt, tx, o.TargetSchema, t.Name, ct);

            await using var cmd = src.CreateCommand();
            cmd.CommandText = selectSql;
            sp.PrepareReadCommand(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var types = writeTable.Columns.Select(c => c.Type).ToArray();
            var lastReport = Stopwatch.StartNew();
            while (await r.ReadAsync(ct))
            {
                var row = new object?[types.Length];
                for (int i = 0; i < types.Length; i++) row[i] = ValueCoercer.Coerce(sp.ReadValue(r, i), types[i]);
                batch.Add(row);
                if (batch.Count >= batchSize)
                {
                    await tp.WriteBatchAsync(tgt, tx, o.TargetSchema, writeTable, batch, mode, ct);
                    total += batch.Count; sinceCommit += batch.Count;
                    batch.Clear();
                    if (o.CommitEveryRows > 0 && sinceCommit >= o.CommitEveryRows)
                    {
                        await tx.CommitAsync(ct); await tx.DisposeAsync();
                        tx = await tgt.BeginTransactionAsync(ct);
                        sinceCommit = 0;
                    }
                    if (lastReport.ElapsedMilliseconds > 400)
                    {
                        Progress?.Invoke(this, new TransferProgress(table, index, o.Tables.Count, total, estimate));
                        lastReport.Restart();
                    }
                }
            }
            if (batch.Count > 0)
            {
                await tp.WriteBatchAsync(tgt, tx, o.TargetSchema, writeTable, batch, mode, ct);
                total += batch.Count;
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { /* connection may be gone */ }
            throw;
        }
        finally
        {
            await tx.DisposeAsync();
        }

        Progress?.Invoke(this, new TransferProgress(table, index, o.Tables.Count, total, estimate));
        Log($"    copied {total:N0} row(s).");
        if (tp is PostgresProvider) await ResyncPgSequencesAsync(tgt, o.TargetSchema, t.Name, Log, ct);
        return total;
    }

    // After loading explicit ids into a PG table whose columns default to a
    // sequence, move the sequence past MAX(id) — otherwise the next app INSERT
    // collides with an imported row.
    internal static async Task ResyncPgSequencesAsync(DbConnection c, string schema, string table, Action<string> Log, CancellationToken ct)
    {
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = @"
SELECT a.attname, pg_get_serial_sequence(quote_ident(n.nspname) || '.' || quote_ident(cl.relname), a.attname)
FROM pg_attribute a JOIN pg_class cl ON cl.oid=a.attrelid JOIN pg_namespace n ON n.oid=cl.relnamespace
WHERE n.nspname=@s AND cl.relname=@t AND a.attnum>0 AND NOT a.attisdropped
  AND pg_get_serial_sequence(quote_ident(n.nspname) || '.' || quote_ident(cl.relname), a.attname) IS NOT NULL";
            var ps = cmd.CreateParameter(); ps.ParameterName = "s"; ps.Value = schema; cmd.Parameters.Add(ps);
            var pt = cmd.CreateParameter(); pt.ParameterName = "t"; pt.Value = table; cmd.Parameters.Add(pt);
            var seqs = new List<(string Col, string Seq)>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) seqs.Add((r.GetString(0), r.GetString(1)));
            foreach (var (col, seq) in seqs)
            {
                await using var set = c.CreateCommand();
                var q = "\"" + col.Replace("\"", "\"\"") + "\"";
                set.CommandText = $"SELECT setval('{seq.Replace("'", "''")}', COALESCE((SELECT MAX({q}) FROM \"{schema.Replace("\"", "\"\"")}\".\"{table.Replace("\"", "\"\"")}\"), 0) + 1, false)";
                await set.ExecuteScalarAsync(ct);
                Log($"    sequence {seq} moved past MAX({col}).");
            }
        }
        catch (Exception ex) { Log($"    (sequence resync skipped: {ex.Message})"); }
    }

    private readonly List<(string SourceTable, TableInfo Target)> _created = new();

    private async Task CopyConstraintsAsync(IDbProvider sp, IDbProvider tp, DbConnection src, DbConnection tgt, TransferOptions o, StringBuilder script, CancellationToken ct)
    {
        string Map(string n) => NameCasing.Apply(n, o.NameCase, tp.NormalizeName);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int indexes = 0, fks = 0, failed = 0, skipped = 0;
        script.AppendLine("-- ===== indexes & foreign keys =====");

        async Task<bool> Apply(string what, string ddl)
        {
            script.AppendLine(ddl + ";");
            if (o.DryRun) { Log($"    would {what}"); return true; }
            try
            {
                await using var cmd = tgt.CreateCommand();
                cmd.CommandText = ddl;
                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                failed++;
                Log($"    FAILED to {what}: {FirstLine(ex.Message)}");
                script.AppendLine($"-- ^^^ FAILED on target: {FirstLine(ex.Message)}");
                return false;
            }
        }

        if (o.CopyIndexes)
            foreach (var (srcName, t) in _created)
            {
                ct.ThrowIfCancellationRequested();
                List<IndexInfo> list;
                try { list = await sp.GetIndexesAsync(src, o.SourceSchema, srcName, ct); }
                catch (Exception ex) { Log($"  [{srcName}] couldn't read indexes: {FirstLine(ex.Message)}"); continue; }
                foreach (var ix in list)
                {
                    var cols = ix.Columns.Select(Map).ToList();
                    if (cols.Any(c => !t.Columns.Any(tc => string.Equals(tc.Name, c, StringComparison.OrdinalIgnoreCase)))) { skipped++; continue; }
                    // A unique index on exactly the PK columns already exists as the PK.
                    if (cols.Count == t.PrimaryKey.Count && cols.All(c => t.PrimaryKey.Contains(c, StringComparer.OrdinalIgnoreCase))) { skipped++; continue; }
                    var name = UniqueName(Map(ix.Name), t.Name, tp.MaxIdentifierLength, used);
                    if (await Apply($"CREATE {(ix.Unique ? "UNIQUE " : "")}INDEX {name} ON {t.Name} ({string.Join(", ", cols)})",
                            tp.BuildCreateIndex(o.TargetSchema, t, new IndexInfo(name, cols, ix.Unique)))) indexes++;
                }
            }

        if (o.CopyForeignKeys && tp is not SqliteProvider)
            foreach (var (srcName, t) in _created)
            {
                ct.ThrowIfCancellationRequested();
                List<ForeignKeyInfo> list;
                try { list = await sp.GetForeignKeysAsync(src, o.SourceSchema, srcName, ct); }
                catch (Exception ex) { Log($"  [{srcName}] couldn't read foreign keys: {FirstLine(ex.Message)}"); continue; }
                foreach (var fk in list)
                {
                    var sameSchema = string.Equals(fk.RefSchema, o.SourceSchema, StringComparison.OrdinalIgnoreCase);
                    if (!sameSchema) Log($"  [{srcName}] FK {fk.Name} points to another schema ({fk.RefSchema}) — created against {Map(fk.RefSchema)}.{Map(fk.RefTable)}");
                    var name = UniqueName(Map(fk.Name), t.Name, tp.MaxIdentifierLength, used);
                    var mapped = fk with
                    {
                        Name = name,
                        Columns = fk.Columns.Select(Map).ToList(),
                        RefSchema = sameSchema ? o.TargetSchema : Map(fk.RefSchema),
                        RefTable = Map(fk.RefTable),
                        RefColumns = fk.RefColumns.Select(Map).ToList(),
                    };
                    var ddl = tp.BuildAddForeignKey(o.TargetSchema, t.Name, mapped);
                    if (ddl != null && await Apply($"ADD FOREIGN KEY {name} {t.Name} -> {mapped.RefTable}", ddl)) fks++;
                }
            }

        Log($"  [constraints] {(o.DryRun ? "planned" : "created")} {indexes} index(es), {fks} foreign key(s)" +
            (failed > 0 ? $", {failed} failed (see above)" : "") + (skipped > 0 ? $", {skipped} skipped (PK duplicate / missing column)" : "") +
            (tp is SqliteProvider && o.CopyForeignKeys ? " — SQLite foreign keys were written into CREATE TABLE" : ""));
        script.AppendLine();
    }

    // SQLite can only declare foreign keys inside CREATE TABLE.
    private async Task<string> InlineSqliteForeignKeysAsync(IDbProvider sp, DbConnection src, TransferOptions o, string srcTable, string ddl, Func<string, string> map, CancellationToken ct)
    {
        List<ForeignKeyInfo> fks;
        try { fks = await sp.GetForeignKeysAsync(src, o.SourceSchema, srcTable, ct); }
        catch { return ddl; }
        if (fks.Count == 0) return ddl;
        string Q(string x) => "\"" + x.Replace("\"", "\"\"") + "\"";
        var clauses = fks.Select(fk =>
            $",\n  FOREIGN KEY ({string.Join(", ", fk.Columns.Select(c => Q(map(c))))}) REFERENCES {Q(map(fk.RefTable))} ({string.Join(", ", fk.RefColumns.Select(c => Q(map(c))))})" +
            (fk.OnDelete != "NO ACTION" ? $" ON DELETE {fk.OnDelete}" : "") + (fk.OnUpdate != "NO ACTION" ? $" ON UPDATE {fk.OnUpdate}" : ""));
        var at = ddl.LastIndexOf("\n)", StringComparison.Ordinal);
        return at < 0 ? ddl : ddl[..at] + string.Concat(clauses) + ddl[at..];
    }

    // Index/constraint names are schema-wide on some engines (PG, Oracle) but
    // table-local on others (SQL Server, MySQL), so a source with two IX_Name
    // indexes on different tables would collide — prefix with the table then,
    // and respect the target's identifier length limit.
    internal static string UniqueName(string name, string table, int maxLen, HashSet<string> used)
    {
        string Fit(string n)
        {
            if (n.Length <= maxLen) return n;
            uint h = 2166136261;                       // FNV-1a: stable across runs (string.GetHashCode is not)
            foreach (var ch in n) h = (h ^ ch) * 16777619;
            var hash = h.ToString("x8")[..6];
            return n[..(maxLen - 7)] + "_" + hash;
        }
        var candidate = Fit(name);
        if (used.Contains(candidate)) candidate = Fit($"{table}_{name}");
        for (int i = 2; used.Contains(candidate); i++) candidate = Fit($"{table}_{name}_{i}");
        used.Add(candidate);
        return candidate;
    }

    private async Task TransferCodeAsync(IDbProvider sp, IDbProvider tp, DbConnection src, DbConnection tgt, TransferOptions o, StringBuilder script, CancellationToken ct)
    {
        Log($"  [code] {o.CodeObjects.Count} object(s) — " + (sp.Engine == tp.Engine ? "same engine, copied as-is" : $"converting {sp.Name} -> {tp.Name}"));
        int ok = 0, failed = 0, warnings = 0;
        foreach (var obj in o.CodeObjects)
        {
            ct.ThrowIfCancellationRequested();
            string? source;
            try { source = await sp.GetObjectSourceAsync(src, obj, ct); }
            catch (Exception ex) { Log($"    [{obj.Name}] couldn't read source: {ex.Message}"); failed++; continue; }
            if (string.IsNullOrWhiteSpace(source)) { Log($"    [{obj.Name}] no source available (permissions?)"); failed++; continue; }

            var conv = SqlCodeConverter.Convert(source, sp.Engine, tp.Engine, new ConvertContext
            {
                SourceSchema = o.SourceSchema,
                TargetSchema = o.TargetSchema,
                NameCase = o.NameCase,
                ObjectName = obj.Name,
            });
            warnings += conv.Warnings.Count;
            script.AppendLine($"-- ===== {obj.Type} {obj.Name} =====");
            foreach (var w in conv.Warnings) script.AppendLine("-- WARNING: " + w);
            script.AppendLine(conv.Sql).AppendLine();

            if (o.DryRun || !o.ApplyCode)
            {
                Log($"    [{obj.Name}] converted ({conv.Warnings.Count} warning(s)) -> script only");
                continue;
            }

            foreach (var stmt in SqlScriptSplitter.Split(conv.Sql, tp.Dialect))
            {
                try
                {
                    await using var cmd = tgt.CreateCommand();
                    cmd.CommandText = stmt;
                    cmd.CommandTimeout = 0;
                    await cmd.ExecuteNonQueryAsync(ct);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log($"    [{obj.Name}] FAILED to apply: {FirstLine(ex.Message)}");
                    script.AppendLine($"-- ^^^ FAILED on target: {FirstLine(ex.Message)}");
                }
            }
        }
        Log($"  [code] done — {ok} statement(s) applied, {failed} failed, {warnings} warning(s) (see script).");
    }

    public static async Task<List<CompareRow>> CompareAsync(TransferOptions o, CancellationToken ct = default)
    {
        var sp = DbProviders.For(o.Source);
        var tp = DbProviders.For(o.Target);
        await using var src = await sp.OpenAsync(o.Source, ct);
        await using var tgt = await tp.OpenAsync(o.Target, ct);
        var list = new List<CompareRow>();
        foreach (var table in o.Tables)
        {
            ct.ThrowIfCancellationRequested();
            var s = await sp.GetTableAsync(src, o.SourceSchema, table, ct);
            var tName = NameCasing.Apply(table, o.NameCase, tp.NormalizeName);
            var t = await tp.GetTableAsync(tgt, o.TargetSchema, tName, ct);
            long? sr = s is null ? null : await sp.CountRowsAsync(src, o.SourceSchema, table, ct);
            long? tr = t is null ? null : await tp.CountRowsAsync(tgt, o.TargetSchema, tName, ct);
            list.Add(new CompareRow(table, s?.Columns.Count, t?.Columns.Count, sr, tr));
        }
        return list;
    }

    private static bool LooksTextual(string native)
    {
        var n = native.ToLowerInvariant();
        return n.Contains("char") || n.Contains("text") || n.Contains("clob") || n.Contains("string") || n == "long" || n.Contains("name");
    }

    private static string? SaveScript(TransferOptions o, StringBuilder script)
    {
        try
        {
            var folder = o.ScriptFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PgBackupManager", "transfer-scripts");
            Directory.CreateDirectory(folder);
            var dbName = o.Source.Engine == DbEngine.Sqlite ? Path.GetFileNameWithoutExtension(o.Source.Database) : o.Source.Database;
            var path = Path.Combine(folder, $"transfer_{dbName}_{o.SourceSchema}_to_{o.TargetSchema}_{DateTime.Now:yyyyMMdd_HHmmss}.sql"
                .Replace(Path.DirectorySeparatorChar, '_').Replace(':', '_'));
            File.WriteAllText(path, script.ToString());
            return path;
        }
        catch { return null; }
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    private void Log(string line) => LogLine?.Invoke(this, line);
}
