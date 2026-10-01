using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Sql;

namespace PgBackupManager.Core.Services;

public enum DataDiffStatus { OnlyInSource, OnlyInTarget, Different }

public sealed class TableDiffOptions
{
    public required ConnectionProfile Source { get; init; }
    public required ConnectionProfile Target { get; init; }
    public required string SourceSchema { get; init; }
    public required string SourceTable { get; init; }
    public required string TargetSchema { get; init; }
    public required string TargetTable { get; init; }
    // Source column names; empty = the primary key.
    public List<string> KeyColumns { get; init; } = new();
    // Source column names left out of the comparison (still copied on insert).
    public List<string> IgnoreColumns { get; init; } = new();
    public string? SourceFilter { get; init; }
    public string? TargetFilter { get; init; }
    // CHAR padding / trailing blanks differ between engines; on by default.
    public bool IgnoreTrailingSpaces { get; init; } = true;
    // Both sides are held in memory as normalized text; refuse bigger tables
    // (use a filter) instead of running the PC out of memory.
    public int MaxRows { get; init; } = 2_000_000;
}

public sealed class DataDiff
{
    public DataDiffStatus Status { get; init; }
    public string KeyText { get; init; } = "";
    public string?[] SourceValues { get; init; } = Array.Empty<string?>();
    public string?[] TargetValues { get; init; } = Array.Empty<string?>();
    public int[] Changed { get; init; } = Array.Empty<int>();
    // Source row coerced to the target column types (insert / update values).
    internal object?[]? SourceRow { get; init; }
    // Target key values exactly as read (update / delete WHERE).
    internal object?[]? TargetKey { get; init; }
}

public sealed class TableDiffResult
{
    public required TableDiffOptions Options { get; init; }
    // Compared columns, as (source name, target name).
    public required List<(string Source, string Target)> Columns { get; init; }
    public required List<int> KeyIndexes { get; init; }
    public required TableInfo TargetTable { get; init; }
    public List<DataDiff> Diffs { get; } = new();
    public long SourceRows { get; set; }
    public long TargetRows { get; set; }
    public long Same { get; set; }
    public int Count(DataDiffStatus s) => Diffs.Count(d => d.Status == s);
    public List<string> Notes { get; } = new();
}

public sealed record TableDiffApplyResult(int Inserted, int Updated, int Deleted);

// Row-level diff of one table across any two connections (any engines):
// rows are matched on a key, values compared after converting both sides to
// the target column's type, and the differences can be written to the target
// (insert missing, update changed, optionally delete extra) in one transaction.
public sealed class TableDiffRunner
{
    public event EventHandler<string>? Progress;
    private void Report(string m) => Progress?.Invoke(this, m);

    private const char KeySep = '\u001F';

    public async Task<TableDiffResult> CompareAsync(TableDiffOptions o, CancellationToken ct = default)
    {
        var sp = DbProviders.For(o.Source);
        var tp = DbProviders.For(o.Target);
        await using var src = await sp.OpenAsync(o.Source, ct);
        await using var tgt = await tp.OpenAsync(o.Target, ct);

        var s = await sp.GetTableAsync(src, o.SourceSchema, o.SourceTable, ct) ?? throw new InvalidOperationException($"Source table {o.SourceSchema}.{o.SourceTable} not found.");
        var t = await tp.GetTableAsync(tgt, o.TargetSchema, o.TargetTable, ct) ?? throw new InvalidOperationException($"Target table {o.TargetSchema}.{o.TargetTable} not found.");

        // Pair columns by name (case-insensitive).
        var pairs = new List<(ColumnInfo S, ColumnInfo T)>();
        foreach (var sc in s.Columns)
        {
            var tc = t.Columns.FirstOrDefault(c => string.Equals(c.Name, sc.Name, StringComparison.OrdinalIgnoreCase));
            if (tc != null) pairs.Add((sc, tc));
        }
        if (pairs.Count == 0) throw new InvalidOperationException("The two tables have no column names in common.");

        int IndexOf(string name) => pairs.FindIndex(p => string.Equals(p.S.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(p.T.Name, name, StringComparison.OrdinalIgnoreCase));
        var keyNames = o.KeyColumns.Count > 0 ? o.KeyColumns
                     : s.PrimaryKey.Count > 0 && s.PrimaryKey.All(k => IndexOf(k) >= 0) ? s.PrimaryKey
                     : t.PrimaryKey;
        var keyIdx = keyNames.Select(IndexOf).ToList();
        if (keyNames.Count == 0 || keyIdx.Any(i => i < 0))
            throw new InvalidOperationException("No usable key: neither table has a primary key present on both sides — pick key column(s).");

        var ignore = new HashSet<string>(o.IgnoreColumns, StringComparer.OrdinalIgnoreCase);
        var compareIdx = Enumerable.Range(0, pairs.Count).Where(i => !keyIdx.Contains(i) && !ignore.Contains(pairs[i].S.Name)).ToList();
        var types = pairs.Select(p => p.T.Type).ToArray();

        var result = new TableDiffResult
        {
            Options = o,
            Columns = pairs.Select(p => (p.S.Name, p.T.Name)).ToList(),
            KeyIndexes = keyIdx,
            TargetTable = new TableInfo { Schema = t.Schema, Name = t.Name, Columns = pairs.Select(p => p.T).ToList(), PrimaryKey = keyIdx.Select(i => pairs[i].T.Name).ToList() },
        };
        var onlyS = s.Columns.Where(c => pairs.All(p => p.S != c)).Select(c => c.Name).ToList();
        var onlyT = t.Columns.Where(c => pairs.All(p => p.T != c)).Select(c => c.Name).ToList();
        if (onlyS.Count > 0) result.Notes.Add("Only in source (not compared): " + string.Join(", ", onlyS));
        if (onlyT.Count > 0) result.Notes.Add("Only in target (not compared): " + string.Join(", ", onlyT));

        // 1) Target side into memory, keyed.
        Report($"Reading target {o.TargetSchema}.{o.TargetTable}...");
        var target = new Dictionary<string, (string?[] Norm, object?[] Key)>(StringComparer.Ordinal);
        var tRead = new TableInfo { Schema = t.Schema, Name = t.Name, Columns = pairs.Select(p => p.T).ToList() };
        await foreach (var raw in ReadAsync(tp, tgt, tp.SelectAllSql(o.TargetSchema, tRead), o.TargetFilter, ct))
        {
            if (++result.TargetRows > o.MaxRows) throw TooBig("Target", o.MaxRows);
            var norm = new string?[raw.Length];
            for (int i = 0; i < raw.Length; i++) norm[i] = Norm(raw[i], types[i], o.IgnoreTrailingSpaces);
            var k = KeyOf(norm, keyIdx);
            if (!target.TryAdd(k, (norm, keyIdx.Select(i => raw[i]).ToArray())))
                throw new InvalidOperationException($"Key ({string.Join(", ", keyNames)}) isn't unique in the target: {KeyDisplay(norm, keyIdx)} appears more than once.");
            if (result.TargetRows % 50_000 == 0) Report($"Reading target... {result.TargetRows:N0} rows");
        }

        // 2) Stream the source and match.
        Report($"Reading source {o.SourceSchema}.{o.SourceTable}...");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sRead = new TableInfo { Schema = s.Schema, Name = s.Name, Columns = pairs.Select(p => p.S).ToList() };
        await foreach (var raw in ReadAsync(sp, src, sp.SelectAllSql(o.SourceSchema, sRead), o.SourceFilter, ct))
        {
            if (++result.SourceRows > o.MaxRows) throw TooBig("Source", o.MaxRows);
            var row = new object?[raw.Length];
            var norm = new string?[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                row[i] = SafeCoerce(raw[i], types[i]);
                norm[i] = Norm(row[i], types[i], o.IgnoreTrailingSpaces);
            }
            var k = KeyOf(norm, keyIdx);
            if (!seen.Add(k)) throw new InvalidOperationException($"Key ({string.Join(", ", keyNames)}) isn't unique in the source: {KeyDisplay(norm, keyIdx)} appears more than once.");
            if (!target.Remove(k, out var tv))
            {
                result.Diffs.Add(new DataDiff { Status = DataDiffStatus.OnlyInSource, KeyText = KeyDisplay(norm, keyIdx), SourceValues = norm, SourceRow = row });
            }
            else
            {
                var changed = compareIdx.Where(i => !string.Equals(norm[i], tv.Norm[i], StringComparison.Ordinal)).ToArray();
                if (changed.Length == 0) result.Same++;
                else result.Diffs.Add(new DataDiff { Status = DataDiffStatus.Different, KeyText = KeyDisplay(norm, keyIdx), SourceValues = norm, TargetValues = tv.Norm, Changed = changed, SourceRow = row, TargetKey = tv.Key });
            }
            if (result.SourceRows % 50_000 == 0) Report($"Reading source... {result.SourceRows:N0} rows");
        }
        foreach (var tv in target.Values)
            result.Diffs.Add(new DataDiff { Status = DataDiffStatus.OnlyInTarget, KeyText = KeyDisplay(tv.Norm, keyIdx), TargetValues = tv.Norm, TargetKey = tv.Key });

        Report($"Compared {result.SourceRows:N0} source / {result.TargetRows:N0} target rows: {result.Same:N0} same, {result.Diffs.Count:N0} difference(s).");
        return result;
    }

    // Writes the chosen differences to the target in ONE transaction:
    // only-in-source -> INSERT, different -> UPDATE changed columns, only-in-target -> DELETE (opt-in).
    public async Task<TableDiffApplyResult> ApplyAsync(TableDiffResult r, IReadOnlyList<DataDiff> diffs, bool insert, bool update, bool delete, CancellationToken ct = default)
    {
        var o = r.Options;
        var tp = DbProviders.For(o.Target);
        var cols = r.TargetTable.Columns;
        var keyCols = r.KeyIndexes.Select(i => cols[i].Name).ToList();

        var edits = new List<RowEdit>();
        foreach (var d in diffs)
        {
            if (d.Status == DataDiffStatus.OnlyInSource && insert)
            {
                var e = new RowEdit { Kind = RowEditKind.Insert };
                for (int i = 0; i < cols.Count; i++) e.Values[cols[i].Name] = d.SourceRow![i];
                edits.Add(e);
            }
            else if (d.Status == DataDiffStatus.Different && update)
            {
                var e = new RowEdit { Kind = RowEditKind.Update };
                foreach (var i in d.Changed) e.Values[cols[i].Name] = d.SourceRow![i];
                for (int k = 0; k < keyCols.Count; k++) e.Keys[keyCols[k]] = d.TargetKey![k];
                edits.Add(e);
            }
            else if (d.Status == DataDiffStatus.OnlyInTarget && delete)
            {
                var e = new RowEdit { Kind = RowEditKind.Delete };
                for (int k = 0; k < keyCols.Count; k++) e.Keys[keyCols[k]] = d.TargetKey![k];
                edits.Add(e);
            }
        }
        if (edits.Count == 0) return new TableDiffApplyResult(0, 0, 0);

        await using var c = await tp.OpenAsync(o.Target, ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var inserts = edits.Any(e => e.Kind == RowEditKind.Insert);
        var identity = inserts && tp.Engine == DbEngine.SqlServer && cols.Any(x => x.IsIdentity);
        try
        {
            if (identity) await ExecAsync(c, tx, $"SET IDENTITY_INSERT {tp.Qualify(o.TargetSchema, r.TargetTable.Name)} ON", ct);
            int ins = 0, up = 0, del = 0;
            // Deletes first so a re-keyed row can't collide with its own insert.
            var ordered = edits.OrderBy(e => e.Kind == RowEditKind.Delete ? 0 : e.Kind == RowEditKind.Update ? 1 : 2).ToList();
            for (int at = 0; at < ordered.Count; at += 500)
            {
                var chunk = ordered.Skip(at).Take(500).ToList();
                var res = await RowEditor.ApplyAsync(c, tx, tp, o.TargetSchema, r.TargetTable, chunk, ct);
                ins += res.Inserted; up += res.Updated; del += res.Deleted;
                Report($"Writing to target... {at + chunk.Count:N0} / {ordered.Count:N0}");
            }
            if (identity) await ExecAsync(c, tx, $"SET IDENTITY_INSERT {tp.Qualify(o.TargetSchema, r.TargetTable.Name)} OFF", ct);
            await tx.CommitAsync(ct);
            if (inserts && tp is PostgresProvider)
                await TransferRunner.ResyncPgSequencesAsync(c, o.TargetSchema, r.TargetTable.Name, Report, ct);
            Report($"Target updated: {ins:N0} inserted, {up:N0} updated, {del:N0} deleted (one transaction).");
            return new TableDiffApplyResult(ins, up, del);
        }
        catch
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    // Same changes as ApplyAsync, as a reviewable script in the target's dialect.
    public string BuildScript(TableDiffResult r, IReadOnlyList<DataDiff> diffs, bool insert, bool update, bool delete)
    {
        var o = r.Options;
        var tp = DbProviders.For(o.Target);
        var d = tp.Dialect;
        var cols = r.TargetTable.Columns;
        var keyIdx = r.KeyIndexes;
        var target = tp.Qualify(o.TargetSchema, r.TargetTable.Name);
        string Lit(object? v, int i) => SqlLiterals.Format(v, cols[i].Type, d);
        string Where(object?[] key) => string.Join(" AND ", keyIdx.Select((ci, k) => key[k] is null ? $"{tp.Quote(cols[ci].Name)} IS NULL" : $"{tp.Quote(cols[ci].Name)} = {Lit(key[k], ci)}"));

        var dels = delete ? diffs.Where(x => x.Status == DataDiffStatus.OnlyInTarget).ToList() : new();
        var ups = update ? diffs.Where(x => x.Status == DataDiffStatus.Different).ToList() : new();
        var ins = insert ? diffs.Where(x => x.Status == DataDiffStatus.OnlyInSource).ToList() : new();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-- Data sync {o.SourceSchema}.{o.SourceTable} ({o.Source}) -> {o.TargetSchema}.{o.TargetTable} ({o.Target})");
        sb.AppendLine($"-- {DateTime.Now:yyyy-MM-dd HH:mm}: {ins.Count:N0} insert(s), {ups.Count:N0} update(s), {dels.Count:N0} delete(s). Review, then run it in one transaction.");
        sb.AppendLine();
        foreach (var x in dels) sb.AppendLine($"DELETE FROM {target} WHERE {Where(x.TargetKey!)};");
        foreach (var x in ups)
            sb.AppendLine($"UPDATE {target} SET {string.Join(", ", x.Changed.Select(i => $"{tp.Quote(cols[i].Name)} = {Lit(x.SourceRow![i], i)}"))} WHERE {Where(x.TargetKey!)};");
        if (ins.Count > 0)
        {
            var identity = d == ScriptDialect.SqlServer && cols.Any(c => c.IsIdentity);
            if (identity) sb.AppendLine($"SET IDENTITY_INSERT {target} ON;");
            var colList = string.Join(", ", cols.Select(c => tp.Quote(c.Name)));
            foreach (var x in ins)
                sb.AppendLine($"INSERT INTO {target} ({colList}) VALUES ({string.Join(", ", x.SourceRow!.Select((v, i) => Lit(v, i)))});");
            if (identity) sb.AppendLine($"SET IDENTITY_INSERT {target} OFF;");
            if (d == ScriptDialect.Postgres)
            {
                // Move serial / identity sequences past the inserted keys.
                var s = SqlLiterals.Str(o.TargetSchema, d); var t = SqlLiterals.Str(r.TargetTable.Name, d);
                sb.AppendLine($@"DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT a.attname, pg_get_serial_sequence(format('%I.%I', {s}, {t}), a.attname) AS seq
           FROM pg_attribute a WHERE a.attrelid = format('%I.%I', {s}, {t})::regclass AND a.attnum > 0 AND NOT a.attisdropped
  LOOP
    IF r.seq IS NOT NULL THEN
      EXECUTE format('SELECT setval(%L, COALESCE((SELECT MAX(%I) FROM %I.%I), 0) + 1, false)', r.seq, r.attname, {s}, {t});
    END IF;
  END LOOP;
END $$;");
            }
        }
        return sb.ToString();
    }

    private static async Task ExecAsync(DbConnection c, DbTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async IAsyncEnumerable<object?[]> ReadAsync(IDbProvider p, DbConnection c, string sql, string? filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(filter) ? sql : sql + " WHERE " + filter;
        cmd.CommandTimeout = 0;
        p.PrepareReadCommand(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = new object?[r.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = p.ReadValue(r, i);
            yield return row;
        }
    }

    private static Exception TooBig(string side, int max) =>
        new InvalidOperationException($"{side} has more than {max:N0} rows — add a row filter (e.g. a key or date range) and compare in slices.");

    private static object? SafeCoerce(object? v, CanonicalType t)
    {
        try { return ValueCoercer.Coerce(v, t); }
        catch (InvalidCastException) { return v is null or DBNull ? null : ValueCoercer.ToText(v); }
    }

    private static string KeyOf(string?[] norm, List<int> keyIdx) =>
        string.Join(KeySep, keyIdx.Select(i => norm[i] ?? "\u0000"));

    private static string KeyDisplay(string?[] norm, List<int> keyIdx) =>
        string.Join(", ", keyIdx.Select(i => norm[i] ?? "NULL"));

    // Engine-neutral text form used for comparing. Both sides are first
    // converted to the TARGET column's type, so 1 vs 1.00, true vs 1,
    // 2024-01-02 vs 2024-01-02 00:00:00 compare equal.
    internal static string? Norm(object? v, CanonicalType t, bool trimEnd)
    {
        v = SafeCoerce(v, t);
        switch (v)
        {
            case null: return null;
            case string s: return trimEnd || t == CanonicalType.FixedString ? s.TrimEnd(' ') : s;
            case bool b: return b ? "1" : "0";
            case decimal d: return d.ToString("0.############################", CultureInfo.InvariantCulture);
            case double f when Math.Abs(f) < 1e15 && f == Math.Floor(f): return ((decimal)f).ToString("0", CultureInfo.InvariantCulture);
            case double f: return f.ToString("R", CultureInfo.InvariantCulture);
            case float f: return Norm((double)f, CanonicalType.Float64, trimEnd);
            case sbyte or byte or short or ushort or int or uint or long or ulong: return Convert.ToString(v, CultureInfo.InvariantCulture);
            case DateTimeOffset dto: return dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "Z";
            case Guid g: return g.ToString("D");
            default: return ValueCoercer.ToText(v);
        }
    }
}
