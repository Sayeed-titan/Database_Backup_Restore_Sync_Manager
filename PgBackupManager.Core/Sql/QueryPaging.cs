using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Sql;

// Builds "give me rows [offset, offset+limit)" and "how many rows in total"
// versions of a user's SELECT, per dialect — so the editor can page through a
// result far bigger than what it fetched up front.
public static class QueryPaging
{
    private static readonly Regex QueryStart = new(@"^(SELECT|WITH|VALUES|TABLE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsPageable(string sql) => QueryStart.IsMatch(SqlScriptSplitter.StripLeadingComments(sql));

    // null = this statement can't be paged safely (e.g. T-SQL SELECT TOP ...).
    public static string? PageSql(ScriptDialect d, string sql, long offset, int limit)
    {
        var q = Clean(sql);
        if (!IsPageable(q)) return null;
        var top = TopLevelWords(q, d);
        switch (d)
        {
            case ScriptDialect.SqlServer:
                // OFFSET/FETCH needs an ORDER BY and can't be combined with TOP.
                if (top.Contains("TOP") || top.Contains("OFFSET")) return null;
                return (top.Contains("ORDER") ? q : q + "\nORDER BY (SELECT NULL)") + $"\nOFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
            case ScriptDialect.Oracle:
                return top.Contains("FETCH") || top.Contains("OFFSET")
                    ? $"SELECT * FROM (\n{q}\n) OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY"
                    : q + $"\nOFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
            default:
                // The derived-table wrapper keeps any LIMIT/ORDER BY the user wrote.
                return $"SELECT * FROM (\n{q}\n) AS pgbm_page LIMIT {limit} OFFSET {offset}";
        }
    }

    public static string? CountSql(ScriptDialect d, string sql)
    {
        var q = Clean(sql);
        if (!IsPageable(q)) return null;
        if (d == ScriptDialect.SqlServer)
        {
            // SQL Server rejects a CTE inside a derived table: keep the WITH
            // part in front and count only the main SELECT.
            var (cte, main) = SplitCte(q, d);
            // ORDER BY isn't allowed inside a derived table (without TOP) — and
            // doesn't change a count anyway.
            var top = TopLevelWords(main, d);
            if (top.Contains("ORDER") && !top.Contains("TOP") && !top.Contains("OFFSET")) main = StripTopLevelOrderBy(main, d);
            return $"{cte}SELECT COUNT(*) FROM (\n{main}\n) pgbm_count";
        }
        return d == ScriptDialect.Oracle
            ? $"SELECT COUNT(*) FROM (\n{q}\n)"
            : $"SELECT COUNT(*) FROM (\n{q}\n) {(d == ScriptDialect.SqlServer ? "" : "AS ")}pgbm_count";
    }

    private static string Clean(string sql) => sql.Trim().TrimEnd(';').TrimEnd();

    // Upper-cased keywords that appear outside any parentheses.
    private static HashSet<string> TopLevelWords(string q, ScriptDialect d)
    {
        var set = new HashSet<string>();
        int depth = 0;
        foreach (var t in SqlTokenizer.Tokenize(q, d))
        {
            if (t.IsSym("(")) depth++;
            else if (t.IsSym(")")) depth--;
            else if (depth == 0 && t.Kind == TokKind.Word) set.Add(t.Upper);
        }
        return set;
    }

    // "WITH a AS (...), b AS (...) SELECT ..." -> ("WITH a AS (...), b AS (...)\n", "SELECT ...").
    private static (string Cte, string Main) SplitCte(string q, ScriptDialect d)
    {
        var toks = SqlTokenizer.Tokenize(q, d);
        var first = TokenOps.FirstSig(toks);
        if (first < 0 || !toks[first].IsWord("WITH")) return ("", q);
        int depth = 0;
        for (int i = first + 1; i < toks.Count; i++)
        {
            if (toks[i].IsSym("(")) depth++;
            else if (toks[i].IsSym(")")) depth--;
            else if (depth == 0 && toks[i].IsWord("SELECT"))
                return (SqlTokenizer.Render(toks.Take(i)).TrimEnd() + "\n", SqlTokenizer.Render(toks.Skip(i)));
        }
        return ("", q);
    }

    private static string StripTopLevelOrderBy(string q, ScriptDialect d)
    {
        var toks = SqlTokenizer.Tokenize(q, d);
        int depth = 0, orderAt = -1;
        for (int i = 0; i < toks.Count; i++)
        {
            if (toks[i].IsSym("(")) depth++;
            else if (toks[i].IsSym(")")) depth--;
            else if (depth == 0 && toks[i].IsWord("ORDER")) orderAt = i;
        }
        return orderAt < 0 ? q : SqlTokenizer.Render(toks.Take(orderAt)).TrimEnd();
    }
}
