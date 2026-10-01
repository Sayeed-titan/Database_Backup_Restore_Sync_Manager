using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Sql;

namespace PgBackupManager.Core.Services;

public enum RowEditKind { Update, Insert, Delete }

public sealed class RowEdit
{
    public RowEditKind Kind { get; init; }
    // column -> new value (Update: changed columns only; Insert: supplied columns)
    public Dictionary<string, object?> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    // PK column -> ORIGINAL value (Update / Delete)
    public Dictionary<string, object?> Keys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record EditTarget(string? Schema, string Table);

public sealed record RowEditResult(int Updated, int Inserted, int Deleted, List<string> Problems);

// Grid editing: works out whether a result came from exactly one table, and
// writes edits back as parameterized UPDATE / INSERT / DELETE keyed on the
// table's primary key (original key values), in one transaction.
public static class RowEditor
{
    private static readonly HashSet<string> Blockers = new(StringComparer.OrdinalIgnoreCase)
        { "DISTINCT", "GROUP", "HAVING", "UNION", "INTERSECT", "EXCEPT", "MINUS", "JOIN", "CONNECT", "PIVOT", "UNPIVOT" };

    // "SELECT ... FROM [schema.]table [alias] [WHERE/ORDER/LIMIT ...]" -> target; otherwise the reason it isn't editable.
    public static (EditTarget? Target, string? Reason) Analyze(string sql, ScriptDialect d)
    {
        var toks = SqlTokenizer.Tokenize(SqlScriptSplitter.StripLeadingComments(sql).Trim().TrimEnd(';'), d);
        var first = TokenOps.FirstSig(toks);
        if (first < 0 || !toks[first].IsWord("SELECT")) return (null, "only a plain SELECT from one table can be edited");
        int depth = 0, from = -1;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.IsSym("(")) { depth++; continue; }
            if (t.IsSym(")")) { depth--; continue; }
            if (depth != 0 || t.Kind != TokKind.Word) continue;
            if (Blockers.Contains(t.Text)) return (null, $"results with {t.Upper} can't be edited — select from a single table");
            if (t.IsWord("FROM") && from < 0) from = i;
        }
        if (from < 0) return (null, "no FROM clause");
        var sig = new List<Tok>();
        for (int i = from + 1; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.IsTrivia) continue;
            if (t.Kind == TokKind.Word && t.IsWordAny("WHERE", "ORDER", "LIMIT", "OFFSET", "FETCH", "FOR", "WITH", "OPTION")) break;
            if (t.IsSym(",") ) return (null, "results from several tables can't be edited");
            if (t.IsSym("(")) return (null, "results from a subquery can't be edited");
            sig.Add(t);
        }
        // table | schema . table | either + alias
        if (sig.Count == 0 || !sig[0].IsIdent) return (null, "couldn't identify the table");
        if (sig.Count >= 3 && sig[1].IsSym(".") && sig[2].IsIdent) return (new EditTarget(sig[0].Ident, sig[2].Ident), null);
        return (new EditTarget(null, sig[0].Ident), null);
    }

    public static async Task<RowEditResult> ApplyAsync(DbConnection conn, DbTransaction? outerTx, IDbProvider p, string schema, TableInfo table,
        IReadOnlyList<RowEdit> edits, CancellationToken ct = default)
    {
        var problems = new List<string>();
        int up = 0, ins = 0, del = 0;
        var tx = outerTx ?? await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var e in edits)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = 0;
                if (cmd is Oracle.ManagedDataAccess.Client.OracleCommand oc) oc.BindByName = true;
                int n = 0;
                string P(object? v)
                {
                    var name = "p" + n++;
                    var prm = cmd.CreateParameter();
                    prm.ParameterName = p.Dialect == ScriptDialect.Oracle ? name : "@" + name;
                    prm.Value = v ?? DBNull.Value;
                    cmd.Parameters.Add(prm);
                    return (p.Dialect == ScriptDialect.Oracle ? ":" : "@") + name;
                }
                string Where() => string.Join(" AND ", e.Keys.Select(k => k.Value is null ? $"{p.Quote(k.Key)} IS NULL" : $"{p.Quote(k.Key)} = {P(k.Value)}"));

                var target = p.Qualify(schema, table.Name);
                switch (e.Kind)
                {
                    case RowEditKind.Update:
                        if (e.Values.Count == 0) continue;
                        var set = string.Join(", ", e.Values.Select(v => $"{p.Quote(v.Key)} = {P(v.Value)}"));
                        cmd.CommandText = $"UPDATE {target} SET {set} WHERE {Where()}";
                        break;
                    case RowEditKind.Delete:
                        cmd.CommandText = $"DELETE FROM {target} WHERE {Where()}";
                        break;
                    default:
                        cmd.CommandText = e.Values.Count == 0
                            ? (p.Dialect is ScriptDialect.MySql ? $"INSERT INTO {target} () VALUES ()" : $"INSERT INTO {target} DEFAULT VALUES")
                            : $"INSERT INTO {target} ({string.Join(", ", e.Values.Keys.Select(p.Quote))}) VALUES ({string.Join(", ", e.Values.Values.Select(P))})";
                        break;
                }
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (e.Kind != RowEditKind.Insert && affected != 1)
                {
                    problems.Add($"{e.Kind} matched {affected} row(s) for key ({string.Join(", ", e.Keys.Select(k => $"{k.Key}={k.Value ?? "NULL"}"))}) — the row changed or was removed since it was read.");
                    throw new InvalidOperationException(problems[^1]);
                }
                if (e.Kind == RowEditKind.Update) up++; else if (e.Kind == RowEditKind.Insert) ins++; else del++;
            }
            if (outerTx == null) await tx.CommitAsync(ct);
            return new RowEditResult(up, ins, del, problems);
        }
        catch
        {
            // Own transaction: undo everything. Inside the editor's manual
            // transaction the caller decides (Rollback button).
            if (outerTx == null) { try { await tx.RollbackAsync(CancellationToken.None); } catch { } }
            throw;
        }
        finally
        {
            if (outerTx == null) await tx.DisposeAsync();
        }
    }

    // Grid cells hold display text: turn one back into a typed value for the column.
    public static object? ParseCell(string? text, ColumnInfo col, bool emptyMeansNull = false)
    {
        if (text is null || text == QueryExecutor.NullText) return null;
        if (emptyMeansNull && text.Length == 0) return null;
        if (TypeFacts.IsTextual(col.Type)) return text;
        return ValueCoercer.Coerce(text, col.Type);
    }

    // Display text the executor shortened ("… (12,345 chars)" / "0x…(n bytes)") can't round-trip.
    public static bool IsTruncatedDisplay(string? s) => s != null && s.Contains('…') && (s.EndsWith(" chars)") || s.EndsWith(" bytes)"));
}
