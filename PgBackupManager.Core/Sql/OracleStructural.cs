using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgBackupManager.Core.Providers;
using static PgBackupManager.Core.Sql.TokenOps;

namespace PgBackupManager.Core.Sql;

// Query-shape rewrites Oracle needs before the token-level conversions:
//   - "(+)" outer joins      -> ANSI LEFT JOIN
//   - START WITH/CONNECT BY  -> WITH RECURSIVE
//   - BULK COLLECT / FORALL / collection methods (PL/SQL bodies)
// Each rewrite re-tokenizes its output so the normal Expr() pass still
// converts functions and types inside it. Anything it can't do safely is left
// untouched and reported, never half-rewritten.
internal static class OracleStructural
{
    private static readonly HashSet<string> BlockEnders = new(StringComparer.OrdinalIgnoreCase) { "UNION", "INTERSECT", "MINUS", "EXCEPT" };
    private static readonly HashSet<string> WhereEnders = new(StringComparer.OrdinalIgnoreCase)
        { "GROUP", "ORDER", "HAVING", "CONNECT", "START", "FOR", "FETCH", "OFFSET", "MODEL", "WINDOW", "UNION", "INTERSECT", "MINUS", "EXCEPT", "RETURNING" };

    private static List<Tok> Tokens(string sql) => SqlTokenizer.Tokenize(sql, ScriptDialect.Oracle);

    private static int[] Depths(List<Tok> t)
    {
        var d = new int[t.Count];
        int cur = 0;
        for (int i = 0; i < t.Count; i++)
        {
            if (t[i].IsSym("(")) { d[i] = cur; cur++; }
            else if (t[i].IsSym(")")) { cur = Math.Max(0, cur - 1); d[i] = cur; }
            else d[i] = cur;
        }
        return d;
    }

    // [SELECT index, end index (exclusive)] of a query block at the SELECT's depth.
    private static int BlockEnd(List<Tok> t, int[] d, int s)
    {
        int depth = d[s];
        for (int j = s + 1; j < t.Count; j++)
        {
            if (t[j].IsSym(")") && d[j] < depth) return j;
            if (d[j] == depth && (t[j].IsSym(";") || (t[j].Kind == TokKind.Word && BlockEnders.Contains(t[j].Text)))) return j;
        }
        return t.Count;
    }

    private static int FindWord(List<Tok> t, int[] d, int from, int to, int depth, params string[] words)
    {
        for (int i = from; i < to; i++)
            if (d[i] == depth && t[i].IsWordAny(words)) return i;
        return -1;
    }

    private static List<List<Tok>> SplitTop(List<Tok> t, int[] d, int from, int to, int depth, Func<List<Tok>, int, bool> isSeparator)
    {
        var parts = new List<List<Tok>>();
        var cur = new List<Tok>();
        for (int i = from; i < to; i++)
        {
            if (d[i] == depth && isSeparator(cur, i)) { parts.Add(cur); cur = new List<Tok>(); continue; }
            cur.Add(t[i]);
        }
        parts.Add(cur);
        return parts.Where(p => p.Any(x => !x.IsTrivia)).ToList();
    }

    private static string Text(IEnumerable<Tok> toks) => R(toks);

    // Alias of a FROM item: "emp e" -> e, "hr.emp" -> emp, "(select ..) x" -> x.
    private static string? AliasOf(List<Tok> item)
    {
        var sig = item.Where(x => !x.IsTrivia).ToList();
        if (sig.Count == 0) return null;
        var last = sig[^1];
        if (sig.Count >= 2 && last.IsIdent && !sig[^2].IsSym(".")) return last.Ident;
        return last.IsIdent ? last.Ident : null;
    }

    // ------------------------------------------------------------ (+) joins

    public static List<Tok> OuterJoins(List<Tok> t, Action<string> warn)
    {
        if (!t.Any(x => x.IsSym("(+)"))) return t;
        for (int guard = 0; guard < 50; guard++)
        {
            var d = Depths(t);
            int target = -1, end = -1, from = -1, where = -1, whereEnd = -1;
            // innermost first: the last SELECT whose own WHERE holds a (+)
            for (int s = t.Count - 1; s >= 0; s--)
            {
                if (!t[s].IsWord("SELECT")) continue;
                var e = BlockEnd(t, d, s);
                var f = FindWord(t, d, s + 1, e, d[s], "FROM");
                if (f < 0) continue;
                var w = FindWord(t, d, f + 1, e, d[s], "WHERE");
                if (w < 0) continue;
                var we = e;
                for (int k = w + 1; k < e; k++)
                    if (d[k] == d[s] && t[k].Kind == TokKind.Word && WhereEnders.Contains(t[k].Text)) { we = k; break; }
                bool has = false;
                for (int k = w + 1; k < we; k++) if (t[k].IsSym("(+)") && d[k] >= d[s]) { has = true; break; }
                if (!has) continue;
                target = s; end = e; from = f; where = w; whereEnd = we;
                break;
            }
            if (target < 0) return t;

            var rewritten = RewriteJoinBlock(t, d, d[target], from, where, whereEnd, warn);
            if (rewritten == null) return t; // reported; leave the rest as-is
            t = rewritten;
        }
        return t;
    }

    private static List<Tok>? RewriteJoinBlock(List<Tok> t, int[] d, int depth, int from, int where, int whereEnd, Action<string> warn)
    {
        if (FindWord(t, d, from + 1, where, depth, "JOIN") >= 0)
        {
            warn("(+) outer join mixed with ANSI JOIN in the same query — rewrite that query by hand.");
            return null;
        }
        var items = SplitTop(t, d, from + 1, where, depth, (_, i) => t[i].IsSym(","));
        var aliases = items.Select(AliasOf).ToList();
        if (aliases.Any(a => a == null)) { warn("(+) outer join: couldn't identify every table alias in FROM — left unchanged."); return null; }

        // split WHERE on top-level AND (but not the AND of BETWEEN x AND y)
        var conds = SplitTop(t, d, where + 1, whereEnd, depth, (cur, i) => t[i].IsWord("AND") && !PendingBetween(cur));

        var optional = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<string>();
        foreach (var c in conds)
        {
            var plusAt = Enumerable.Range(0, c.Count).Where(i => c[i].IsSym("(+)")).ToList();
            if (plusAt.Count == 0) { remaining.Add(Text(c)); continue; }
            var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plusAt)
            {
                // alias.col(+)  -> alias
                int col = PrevSig(c, p), dot = col >= 0 ? PrevSig(c, col) : -1, al = dot >= 0 ? PrevSig(c, dot) : -1;
                if (dot >= 0 && c[dot].IsSym(".") && al >= 0 && c[al].IsIdent) owners.Add(c[al].Ident);
                else if (col >= 0 && c[col].IsIdent)
                {
                    warn($"(+) on an unqualified column ({c[col].Text}) — qualify it with its table alias so it can be converted.");
                    return null;
                }
            }
            if (owners.Count != 1) { warn("(+) condition references more than one outer table — left unchanged."); return null; }
            var owner = owners.First();
            if (!aliases.Contains(owner, StringComparer.OrdinalIgnoreCase)) { warn($"(+) refers to unknown alias {owner} — left unchanged."); return null; }
            var clean = c.Where(x => !x.IsSym("(+)")).ToList();
            if (!optional.TryGetValue(owner, out var list)) optional[owner] = list = new List<string>();
            list.Add(Text(clean));
        }

        // Required tables first (CROSS JOIN keeps them visible to every ON), then
        // outer tables — each after everything its ON clause mentions.
        var placed = new List<string>();
        var sb = new StringBuilder(" FROM ");
        bool first = true;
        for (int i = 0; i < items.Count; i++)
        {
            if (optional.ContainsKey(aliases[i]!)) continue;
            sb.Append(first ? "" : "\n  CROSS JOIN ").Append(Text(items[i]));
            placed.Add(aliases[i]!);
            first = false;
        }
        if (first) { warn("(+) outer join: every table is optional — can't choose a driving table; left unchanged."); return null; }
        var pending = Enumerable.Range(0, items.Count).Where(i => optional.ContainsKey(aliases[i]!)).ToList();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(i => MentionedAliases(optional[aliases[i]!], aliases!).All(a => a.Equals(aliases[i], StringComparison.OrdinalIgnoreCase) || placed.Contains(a, StringComparer.OrdinalIgnoreCase)), -1);
            if (next < 0) { warn("(+) outer joins depend on each other in a cycle — left unchanged."); return null; }
            sb.Append("\n  LEFT JOIN ").Append(Text(items[next])).Append(" ON ").Append(string.Join(" AND ", optional[aliases[next]!]));
            placed.Add(aliases[next]!);
            pending.Remove(next);
        }
        if (remaining.Count > 0) sb.Append("\nWHERE ").Append(string.Join("\n  AND ", remaining));
        sb.Append(' ');

        var result = t.Take(from).ToList();
        result.AddRange(Tokens(sb.ToString()));
        result.AddRange(t.Skip(whereEnd));
        return result;
    }

    // "x BETWEEN 1" is still waiting for its own AND.
    private static bool PendingBetween(List<Tok> cur)
    {
        int b = cur.FindLastIndex(x => x.IsWord("BETWEEN"));
        return b >= 0 && !cur.Skip(b).Any(x => x.IsWord("AND"));
    }

    private static IEnumerable<string> MentionedAliases(IEnumerable<string> conditions, IEnumerable<string> known)
    {
        var set = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        foreach (var c in conditions)
        {
            var toks = Tokens(c);
            for (int i = 0; i + 1 < toks.Count; i++)
                if (toks[i].IsIdent && toks[i + 1].IsSym(".") && set.Contains(toks[i].Ident)) yield return toks[i].Ident;
        }
    }

    // ------------------------------------------------------ CONNECT BY

    public static List<Tok> ConnectBy(List<Tok> t, Action<string> warn)
    {
        if (!t.Any(x => x.IsWord("CONNECT"))) return t;
        for (int guard = 0; guard < 20; guard++)
        {
            var d = Depths(t);
            int s = -1, e = -1;
            for (int i = t.Count - 1; i >= 0; i--)
            {
                if (!t[i].IsWord("SELECT")) continue;
                var end = BlockEnd(t, d, i);
                var c = FindWord(t, d, i + 1, end, d[i], "CONNECT");
                if (c >= 0 && NextSig(t, c) is var by and >= 0 && t[by].IsWord("BY")) { s = i; e = end; break; }
            }
            if (s < 0) return t;
            var r = RewriteHierarchy(t, d, s, e, warn);
            if (r == null) return t;
            t = r;
        }
        return t;
    }

    private static List<Tok>? RewriteHierarchy(List<Tok> t, int[] d, int s, int e, Action<string> warn)
    {
        int depth = d[s];
        int from = FindWord(t, d, s + 1, e, depth, "FROM");
        if (from < 0) return null;
        // clause starts after FROM
        var clauses = new SortedDictionary<int, string>();
        foreach (var w in new[] { "WHERE", "START", "CONNECT", "GROUP", "ORDER" })
        {
            var at = FindWord(t, d, from + 1, e, depth, w);
            if (at >= 0) clauses[at] = w;
        }
        int ClauseEnd(int at) => clauses.Keys.Where(k => k > at).DefaultIfEmpty(e).Min();
        int fromEnd = clauses.Keys.DefaultIfEmpty(e).Min();

        var fromItems = SplitTop(t, d, from + 1, fromEnd, depth, (_, i) => t[i].IsSym(","));
        if (fromItems.Count != 1 || fromItems[0].Any(x => x.IsWord("JOIN")))
        {
            warn("CONNECT BY over several tables/joins — rewrite with WITH RECURSIVE by hand.");
            return null;
        }
        var tableText = Text(fromItems[0]);
        var alias = AliasOf(fromItems[0])!;
        var sig = fromItems[0].Where(x => !x.IsTrivia).ToList();
        var tableRef = sig.Count >= 2 && !sig[^2].IsSym(".") ? tableText : $"{tableText} {alias}";

        // CONNECT BY [NOCYCLE] cond
        var cAt = clauses.First(kv => kv.Value == "CONNECT").Key;
        int condStart = NextSig(t, NextSig(t, cAt));
        if (t[condStart].IsWord("NOCYCLE")) { warn("CONNECT BY NOCYCLE — add a CYCLE clause (PostgreSQL 14+) if the data can loop."); condStart = NextSig(t, condStart); }
        var connectToks = Slice(t, condStart, ClauseEnd(cAt));
        var cd = Depths(connectToks);
        var eqs = SplitTop(connectToks, cd, 0, connectToks.Count, 0, (_, i) => connectToks[i].IsWord("AND"));
        var joins = new List<string>();
        foreach (var eq in eqs)
        {
            var q = eq.Where(x => !x.IsTrivia).ToList();
            int op = q.FindIndex(x => x.IsSym("="));
            if (op < 0) { warn("CONNECT BY condition isn't a PRIOR equality — left unchanged."); return null; }
            var left = q.Take(op).ToList(); var right = q.Skip(op + 1).ToList();
            bool priorLeft = left.Count > 0 && left[0].IsWord("PRIOR");
            bool priorRight = right.Count > 0 && right[0].IsWord("PRIOR");
            if (priorLeft == priorRight) { warn("CONNECT BY needs exactly one PRIOR per equality — left unchanged."); return null; }
            var parentCol = LastIdent(priorLeft ? left.Skip(1).ToList() : right.Skip(1).ToList());
            var childCol = LastIdent(priorLeft ? right : left);
            if (parentCol == null || childCol == null) { warn("CONNECT BY with expressions (not plain columns) — left unchanged."); return null; }
            joins.Add($"{alias}.{childCol} = pgbm_h.{parentCol}");
        }

        var selectToks = Slice(t, s + 1, from);
        var select = Text(selectToks);
        var startCond = clauses.ContainsValue("START") ? Text(Slice(t, NextSig(t, NextSig(t, clauses.First(kv => kv.Value == "START").Key)), ClauseEnd(clauses.First(kv => kv.Value == "START").Key))) : "";
        var whereCond = clauses.ContainsValue("WHERE") ? Text(Slice(t, NextSig(t, clauses.First(kv => kv.Value == "WHERE").Key), ClauseEnd(clauses.First(kv => kv.Value == "WHERE").Key))) : "";
        var group = clauses.ContainsValue("GROUP") ? Text(Slice(t, clauses.First(kv => kv.Value == "GROUP").Key, ClauseEnd(clauses.First(kv => kv.Value == "GROUP").Key))) : "";
        var order = clauses.ContainsValue("ORDER") ? Text(Slice(t, clauses.First(kv => kv.Value == "ORDER").Key, ClauseEnd(clauses.First(kv => kv.Value == "ORDER").Key))) : "";

        var extraBase = new StringBuilder();
        var extraRec = new StringBuilder();

        // SYS_CONNECT_BY_PATH(col, sep) -> running path column
        var sel = Tokens(select);
        for (int i = 0; i < sel.Count; i++)
        {
            if (!sel[i].IsWord("SYS_CONNECT_BY_PATH")) continue;
            var open = NextSig(sel, i);
            var close = MatchParen(sel, open);
            var args = SplitArgs(sel, open, close);
            if (args.Count != 2 || extraBase.ToString().Contains("pgbm_path")) { warn("more than one SYS_CONNECT_BY_PATH — only the first is converted."); continue; }
            var col = LastIdent(args[0].Where(x => !x.IsTrivia).ToList());
            var sep = Text(args[1]);
            if (col == null) { warn("SYS_CONNECT_BY_PATH over an expression — left unchanged."); return null; }
            extraBase.Append($", CAST({sep} || {alias}.{col} AS text) AS pgbm_path");
            extraRec.Append($", pgbm_h.pgbm_path || {sep} || {alias}.{col}");
            Replace(sel, i, close, $"{alias}.pgbm_path");
        }
        select = Text(sel);
        if (select.Contains("CONNECT_BY_ROOT", StringComparison.OrdinalIgnoreCase) || select.Contains("CONNECT_BY_ISLEAF", StringComparison.OrdinalIgnoreCase))
            warn("CONNECT_BY_ROOT / CONNECT_BY_ISLEAF need an extra CTE column — add them by hand.");

        // ORDER SIBLINGS BY col -> depth-first order via an array sort key
        if (order.Length > 0 && Tokens(order).Where(x => !x.IsTrivia).Skip(1).FirstOrDefault()?.IsWord("SIBLINGS") == true)
        {
            var ot = Tokens(order).Where(x => !x.IsTrivia).ToList(); // ORDER SIBLINGS BY x
            var keyToks = ot.Skip(3).ToList();
            var key = LastIdent(keyToks);
            if (key == null || keyToks.Any(x => x.IsSym(",")) || keyToks.Any(x => x.IsWord("DESC")))
            {
                warn("ORDER SIBLINGS BY with several/descending keys — converted to a plain ORDER BY; check the order.");
                order = "ORDER BY " + Text(keyToks);
            }
            else
            {
                extraBase.Append($", ARRAY[{alias}.{key}] AS pgbm_sort");
                extraRec.Append($", pgbm_h.pgbm_sort || {alias}.{key}");
                order = $"ORDER BY {alias}.pgbm_sort";
            }
        }

        var sb = new StringBuilder();
        sb.Append("WITH RECURSIVE pgbm_h AS (\n");
        sb.Append($"  SELECT {alias}.*, 1 AS level{extraBase} FROM {tableRef}{(startCond.Length > 0 ? " WHERE " + startCond : "")}\n");
        sb.Append("  UNION ALL\n");
        sb.Append($"  SELECT {alias}.*, pgbm_h.level + 1{extraRec} FROM {tableRef} JOIN pgbm_h ON {string.Join(" AND ", joins)}\n");
        sb.Append(")\n");
        sb.Append($"SELECT {select} FROM pgbm_h {alias}");
        if (whereCond.Length > 0) sb.Append(" WHERE " + whereCond);
        if (group.Length > 0) sb.Append(' ').Append(group);
        if (order.Length > 0) sb.Append(' ').Append(order);
        sb.Append(' ');

        var result = t.Take(s).ToList();
        result.AddRange(Tokens(sb.ToString()));
        result.AddRange(t.Skip(e));
        return result;
    }

    private static string? LastIdent(List<Tok> toks)
    {
        var sig = toks.Where(x => !x.IsTrivia).ToList();
        if (sig.Count == 0 || !sig[^1].IsIdent) return null;
        // plain column or alias.column only
        if (sig.Count == 1 || (sig.Count == 3 && sig[1].IsSym(".") && sig[0].IsIdent)) return sig[^1].Text;
        return null;
    }

    // ------------------------------------------------ PL/SQL collections

    // TYPE t IS TABLE OF elem [INDEX BY ...] / VARRAY(n) OF elem  -> "elem"
    public static string? CollectionElementType(List<Tok> item)
    {
        var sig = item.Where(x => !x.IsTrivia).ToList();
        if (sig.Count < 5 || !sig[0].IsWord("TYPE") || !sig[2].IsWord("IS")) return null;
        int of;
        if (sig[3].IsWord("TABLE") && sig[4].IsWord("OF")) of = 5;
        else if ((sig[3].IsWord("VARRAY") || sig[3].IsWord("VARYING")) && sig.FindIndex(x => x.IsWord("OF")) is var o and > 0) of = o + 1;
        else return null;
        var end = sig.FindIndex(of, x => x.IsWordAny("INDEX", "NOT"));
        var elem = sig.Skip(of).Take((end < 0 ? sig.Count : end) - of).ToList();
        if (elem.Count == 0 || elem.Any(x => x.IsSym("%") && sig.Any(y => y.IsWord("ROWTYPE")))) return null; // %ROWTYPE records need a composite type
        return string.Join("", elem.Select((x, i) => (i > 0 && !x.IsSym("(") && !x.IsSym(")") && !x.IsSym(",") && !elem[i - 1].IsSym("(") && !x.IsSym("%") && !elem[i - 1].IsSym("%") ? " " : "") + x.Text));
    }

    // BULK COLLECT, FORALL and collection methods inside a PL/SQL body.
    public static List<Tok> PlSqlCollections(List<Tok> t, HashSet<string> collVars, Dictionary<string, string> collTypes, Action<string> warn)
    {
        t = BulkCollect(t, warn);
        t = Forall(t, collVars, warn);
        if (collVars.Count > 0 || collTypes.Count > 0) t = CollectionMethods(t, collVars, collTypes, warn);
        return t;
    }

    private static List<Tok> BulkCollect(List<Tok> t, Action<string> warn)
    {
        for (int guard = 0; guard < 100; guard++)
        {
            int b = t.FindIndex(x => x.IsWord("BULK"));
            if (b < 0) return t;
            var c = NextSig(t, b);
            if (c < 0 || !t[c].IsWord("COLLECT")) return t;
            var d = Depths(t);
            // find the SELECT that owns it: walk back at the same depth
            int s = -1;
            for (int i = b - 1; i >= 0; i--)
            {
                if (d[i] < d[b]) break;
                if (d[i] == d[b] && t[i].IsWord("SELECT")) { s = i; break; }
                if (d[i] == d[b] && (t[i].IsSym(";") || t[i].IsWordAny("FETCH", "RETURNING"))) break;
            }
            if (s < 0)
            {
                warn("FETCH/RETURNING ... BULK COLLECT INTO — use a loop, or SELECT array_agg(...) INTO; left for manual rewrite (BULK COLLECT removed).");
                t.RemoveRange(b, c - b + 1);
                t.Insert(b, new Tok(TokKind.Comment, "/* TODO(convert): was BULK COLLECT */"));
                continue;
            }
            var list = SplitTop(t, d, s + 1, b, d[s], (_, i) => t[i].IsSym(","));
            if (list.Any(p => Text(p) == "*" || Text(p).EndsWith(".*")))
            {
                warn("SELECT * BULK COLLECT INTO a record collection — needs a composite-type array; left for manual rewrite.");
                t.RemoveRange(b, c - b + 1);
                continue;
            }
            // The query's own ORDER BY must move INTO the aggregate (array_agg(x ORDER BY ...)),
            // otherwise PostgreSQL rejects it and the array order would be undefined anyway.
            var blockEnd = BlockEnd(t, d, s);
            var orderAt = FindWord(t, d, c + 1, blockEnd, d[s], "ORDER");
            var orderText = orderAt >= 0 ? " " + Text(Slice(t, orderAt, blockEnd)) : "";
            if (FindWord(t, d, c + 1, blockEnd, d[s], "GROUP") >= 0)
                warn("BULK COLLECT over a GROUP BY query — array_agg now aggregates per group; check the result.");
            var agg = " " + string.Join(", ", list.Select(p => $"array_agg({Text(p)}{orderText})")) + " ";
            var result = t.Take(s + 1).ToList();
            result.AddRange(Tokens(agg));
            // drops "BULK COLLECT", keeps "INTO v FROM ... WHERE ..." but not the moved ORDER BY
            result.AddRange(orderAt >= 0 ? Slice(t, c + 1, orderAt) : t.Skip(c + 1).Take(blockEnd - c - 1));
            if (orderAt >= 0) result.Add(new Tok(TokKind.Space, " "));
            result.AddRange(t.Skip(blockEnd));
            t = result;
        }
        return t;
    }

    private static List<Tok> Forall(List<Tok> t, HashSet<string> collVars, Action<string> warn)
    {
        for (int guard = 0; guard < 100; guard++)
        {
            int f = t.FindIndex(x => x.IsWord("FORALL"));
            if (f < 0) return t;
            var d = Depths(t);
            int semi = -1;
            for (int i = f + 1; i < t.Count; i++) if (t[i].IsSym(";") && d[i] == d[f]) { semi = i; break; }
            int dml = t.FindIndex(f + 1, x => x.IsWordAny("INSERT", "UPDATE", "DELETE", "MERGE", "EXECUTE"));
            if (semi < 0 || dml < 0 || dml > semi) { warn("FORALL without a recognizable DML statement — left unchanged."); return t; }
            var header = Slice(t, NextSig(t, f), dml).Where(x => !x.IsTrivia).ToList(); // i IN lo..hi [SAVE EXCEPTIONS]
            if (header.Any(x => x.IsWord("SAVE"))) warn("FORALL ... SAVE EXCEPTIONS — per-row error collection is not converted; the loop stops at the first error.");
            header = header.TakeWhile(x => !x.IsWord("SAVE")).ToList();
            string range;
            var inAt = header.FindIndex(x => x.IsWord("IN"));
            if (inAt < 0) return t;
            var rangeToks = header.Skip(inAt + 1).ToList();
            if (rangeToks.Count >= 3 && rangeToks[0].IsWordAny("INDICES", "VALUES") && rangeToks[1].IsWord("OF"))
            {
                var coll = rangeToks[2].Text;
                if (rangeToks[0].IsWord("VALUES")) warn("FORALL ... IN VALUES OF — converted as INDICES OF; check the loop variable use.");
                range = $"1 .. COALESCE(array_length({coll}, 1), 0)";
            }
            else
            {
                // Render the original tokens (keeps "1..v.COUNT" intact for the collection pass).
                var inIdx = t.FindIndex(f, x => x.IsWord("IN"));
                var saveIdx = t.FindIndex(inIdx, x => x.IsWord("SAVE"));
                var rangeEnd = saveIdx >= 0 && saveIdx < dml ? saveIdx : dml;
                range = Text(Slice(t, inIdx + 1, rangeEnd));
            }
            var loopVar = header[0].Text;
            var body = Text(Slice(t, dml, semi));
            var repl = $"FOR {loopVar} IN {range} LOOP\n    {body};\nEND LOOP;";
            var result = t.Take(f).ToList();
            result.AddRange(Tokens(repl));
            result.AddRange(t.Skip(semi + 1));
            t = result;
        }
        return t;
    }

    private static List<Tok> CollectionMethods(List<Tok> t, HashSet<string> collVars, Dictionary<string, string> collTypes, Action<string> warn)
    {
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            // t_list(...) constructor -> ARRAY[...] / '{}'
            if (x.IsIdent && collTypes.ContainsKey(x.Ident) && NextSig(t, i) is var cp and >= 0 && t[cp].IsSym("("))
            {
                var cc = MatchParen(t, cp);
                var inner = R(Slice(t, cp + 1, cc));
                Replace(t, i, cc, inner.Length == 0 ? "'{}'" : $"ARRAY[{inner}]");
                continue;
            }
            if (!x.IsIdent || !collVars.Contains(x.Ident)) continue;
            var p = PrevSig(t, i);
            // "schema.v" is a qualified name — but "1..v" is a range, not a qualifier.
            if (p >= 0 && t[p].IsSym(".") && !(PrevSig(t, p) is var pp and >= 0 && t[pp].IsSym("."))) continue;
            var n = NextSig(t, i);
            if (n < 0) continue;
            // v(i) -> v[i]
            if (t[n].IsSym("("))
            {
                var close = MatchParen(t, n);
                if (close > 0) { t[n] = new Tok(TokKind.Symbol, "["); t[close] = new Tok(TokKind.Symbol, "]"); }
                continue;
            }
            if (!t[n].IsSym(".")) continue;
            var m = NextSig(t, n);
            if (m < 0 || t[m].Kind != TokKind.Word) continue;
            var after = NextSig(t, m);
            int endIdx = m;
            if (after >= 0 && t[after].IsSym("(")) endIdx = MatchParen(t, after);
            string? rep = t[m].Upper switch
            {
                "COUNT" => $"COALESCE(array_length({x.Text}, 1), 0)",
                "FIRST" => "1",
                "LAST" => $"array_length({x.Text}, 1)",
                "EXISTS" when after >= 0 && t[after].IsSym("(") => $"({R(Slice(t, after + 1, endIdx))} <= COALESCE(array_length({x.Text}, 1), 0))",
                "EXTEND" => "NULL /* EXTEND: PostgreSQL arrays grow on assignment */",
                "DELETE" => $"{x.Text} := '{{}}'",
                "TRIM" => $"{x.Text} := {x.Text}[1:array_length({x.Text}, 1) - 1]",
                _ => null,
            };
            if (rep == null) continue;
            if (t[m].Upper == "DELETE" && after >= 0 && t[after].IsSym("(")) { warn($"{x.Text}.DELETE(n) — deleting single elements isn't converted."); continue; }
            Replace(t, i, endIdx, rep);
        }
        return t;
    }
}
