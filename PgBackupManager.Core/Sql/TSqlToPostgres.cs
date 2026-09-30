using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PgBackupManager.Core.Providers;
using static PgBackupManager.Core.Sql.TokenOps;

namespace PgBackupManager.Core.Sql;

// SQL Server T-SQL -> PostgreSQL. Views, tables and single statements are
// rewritten token by token; procedures/functions go through a heuristic
// statement segmenter (T-SQL has no mandatory ';') and are re-emitted as
// structured PL/pgSQL (IF/WHILE/TRY-CATCH/cursors/variables).
internal sealed class TSqlToPostgres
{
    private readonly ConvertContext _ctx;
    private readonly List<string> _warn = new();
    private readonly bool _lower;
    private Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);

    public TSqlToPostgres(ConvertContext ctx)
    {
        _ctx = ctx;
        _lower = ctx.NameCase is NameCase.TargetDefault or NameCase.Lower;
    }

    private string TargetSchema => string.IsNullOrWhiteSpace(_ctx.TargetSchema) ? "" : Id(_ctx.TargetSchema!);

    private void Warn(string w) { if (!_warn.Contains(w)) _warn.Add(w); }

    private string Id(string name)
    {
        var n = _lower ? name.ToLowerInvariant() : name;
        return IsSimpleIdent(n) && n == n.ToLowerInvariant() ? n : "\"" + n.Replace("\"", "\"\"") + "\"";
    }

    public ConversionResult Convert(string source)
    {
        var sb = new StringBuilder();
        foreach (var batch in SqlScriptSplitter.SplitDetailed(source, ScriptDialect.SqlServer).Where(s => s.Kind == StatementKind.Sql))
        {
            string converted;
            try { converted = ConvertBatch(batch.Text); }
            catch (Exception ex)
            {
                Warn($"could not convert a batch ({ex.Message}) — original kept as a comment.");
                converted = "/* TODO(convert):\n" + batch.Text.Replace("*/", "* /") + "\n*/";
            }
            sb.AppendLine(converted.TrimEnd()).AppendLine();
        }
        return new ConversionResult(SqlCodeConverter.Header("SQL Server", "PostgreSQL", _warn) + "\n" + sb.ToString().TrimEnd() + "\n", _warn.Distinct().ToList());
    }

    private void NormalizeIdents(List<Tok> t)
    {
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind == TokKind.QuotedIdent) t[i] = new Tok(TokKind.Word, Id(x.Ident));
            else if (x.Kind == TokKind.Word && x.Text.StartsWith('#'))
            {
                t[i] = new Tok(TokKind.Word, Id(x.Text.TrimStart('#')));
                Warn("#temp tables -> PostgreSQL TEMP tables (CREATE TEMP TABLE ...); references rewritten without '#'.");
            }
        }
        var src = _ctx.SourceSchema ?? "dbo";
        for (int i = 0; i < t.Count; i++)
        {
            if (!t[i].IsIdent || !string.Equals(t[i].Ident.Trim('"'), src, StringComparison.OrdinalIgnoreCase)) continue;
            var d = NextSig(t, i);
            if (d < 0 || !t[d].IsSym(".")) continue;
            // db.dbo.table -> drop the database part too
            var pd = PrevSig(t, i);
            if (pd >= 0 && t[pd].IsSym(".")) { var db = PrevSig(t, pd); if (db >= 0) { t.RemoveRange(db, pd - db + 1); i = db; } }
            if (TargetSchema.Length > 0) t[i] = new Tok(TokKind.Word, TargetSchema);
            else { t.RemoveRange(i, NextSig(t, i) - i + 1); }
        }
    }

    private string ConvertBatch(string text)
    {
        var t = SqlTokenizer.Tokenize(text, ScriptDialect.SqlServer);
        NormalizeIdents(t);
        int i = FirstSig(t);
        if (i < 0) return "";
        var first = t[i];
        if (first.IsWordAny("CREATE", "ALTER"))
        {
            int k = NextSig(t, i);
            if (k >= 0 && t[k].IsWord("OR")) k = NextSig(t, NextSig(t, k)); // OR ALTER
            if (k >= 0)
            {
                switch (t[k].Upper)
                {
                    case "PROC":
                    case "PROCEDURE": return ConvertRoutine(t, k, isFunction: false);
                    case "FUNCTION": return ConvertRoutine(t, k, isFunction: true);
                    case "VIEW": return ConvertView(t, k);
                    case "TRIGGER":
                        Warn("T-SQL triggers use inserted/deleted tables (set-based) — rewrite as a PL/pgSQL trigger function using NEW/OLD (row) or transition tables (statement).");
                        return "/* TODO(convert): trigger\n" + text.Replace("*/", "* /") + "\n*/";
                    case "TABLE" when first.IsWord("CREATE"): return ConvertTable(t, k);
                }
            }
        }
        if (first.IsWordAny("SET", "USE") && t.Count(z => !z.IsTrivia) <= 4)
            return "-- " + R(t) + "  (session option, not needed in PostgreSQL)";

        // Loose statements / ad-hoc scripts: run through the procedural translator.
        _vars = new(StringComparer.OrdinalIgnoreCase);
        var body = TranslateBody(t, isFunction: false, out var decls);
        return decls.Count > 0 || Regex.IsMatch(body, @"\b(IF|WHILE|RAISE|EXCEPTION)\b")
            ? $"DO $body$\nDECLARE\n{Indent(string.Join("\n", decls))}\nBEGIN\n{Indent(body)}\nEND\n$body$;"
            : body;
    }

    // ------------------------------------------------------------ expressions

    private static readonly HashSet<string> DateParts = new(StringComparer.OrdinalIgnoreCase)
    { "year","yy","yyyy","quarter","qq","q","month","mm","m","dayofyear","dy","y","day","dd","d","week","wk","ww","weekday","dw","hour","hh","minute","mi","n","second","ss","s","millisecond","ms" };

    private static string PgPart(string p) => p.ToLowerInvariant() switch
    {
        "yy" or "yyyy" or "year" => "year",
        "qq" or "q" or "quarter" => "quarter",
        "mm" or "m" or "month" => "month",
        "dy" or "y" or "dayofyear" => "doy",
        "dd" or "d" or "day" => "day",
        "wk" or "ww" or "week" => "week",
        "dw" or "weekday" => "dow",
        "hh" or "hour" => "hour",
        "mi" or "n" or "minute" => "minute",
        "ss" or "s" or "second" => "second",
        "ms" or "millisecond" => "milliseconds",
        _ => p.ToLowerInvariant(),
    };

    private void Expr(List<Tok> t)
    {
        MapTypes(t);
        StripHints(t);
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind == TokKind.String && (x.Text.StartsWith("N'") || x.Text.StartsWith("n'"))) { t[i] = new Tok(TokKind.String, x.Text[1..]); continue; }
            if (x.Kind == TokKind.AtVar) { t[i] = new Tok(TokKind.Word, VarName(x.Text)); continue; }
            if (x.IsSym("+"))
            {
                var p0 = PrevSig(t, i); var n0 = NextSig(t, i);
                if ((p0 >= 0 && t[p0].Kind == TokKind.String) || (n0 >= 0 && t[n0].Kind == TokKind.String)) t[i] = new Tok(TokKind.Symbol, "||");
                continue;
            }
            if (x.Kind != TokKind.Word) continue;
            var up = x.Upper;
            var n = NextSig(t, i);
            bool call = n >= 0 && t[n].IsSym("(");
            if (!call)
            {
                if (up == "CURRENT_TIMESTAMP") t[i] = new Tok(TokKind.Word, "LOCALTIMESTAMP");
                continue;
            }
            int close = MatchParen(t, n);
            if (close < 0) continue;
            var args = SplitArgs(t, n, close);
            string A(int k) { var a = args[k].Select(z => new Tok(z.Kind, z.Text)).ToList(); Expr(a); return R(a); }
            string? rep = up switch
            {
                "ISNULL" when args.Count == 2 => $"COALESCE({A(0)}, {A(1)})",
                "LEN" => $"length({A(0)})",
                "DATALENGTH" => $"octet_length({A(0)})",
                "GETDATE" or "SYSDATETIME" => "LOCALTIMESTAMP",
                "GETUTCDATE" or "SYSUTCDATETIME" => "(now() AT TIME ZONE 'utc')",
                "SYSDATETIMEOFFSET" => "CURRENT_TIMESTAMP",
                "NEWID" or "NEWSEQUENTIALID" => "gen_random_uuid()",
                "SCOPE_IDENTITY" => "lastval()",
                "CHARINDEX" when args.Count == 2 => $"POSITION({A(0)} IN {A(1)})",
                "CHARINDEX" when args.Count == 3 => $"(POSITION({A(0)} IN substr({A(1)}, {A(2)})) + ({A(2)}) - 1)",
                "IIF" when args.Count == 3 => $"CASE WHEN {A(0)} THEN {A(1)} ELSE {A(2)} END",
                "CONVERT" or "TRY_CONVERT" when args.Count >= 2 => ConvertCall(args, A, up),
                "TRY_CAST" => $"CAST({A(0)})",
                "DATEADD" when args.Count == 3 => $"({A(2)} + ({A(1)}) * INTERVAL '1 {PgPart(R(args[0]))}')",
                "DATEDIFF" when args.Count == 3 => DateDiff(PgPart(R(args[0])), A(1), A(2)),
                "DATEPART" when args.Count == 2 => $"EXTRACT({PgPart(R(args[0]))} FROM {A(1)})",
                "YEAR" or "MONTH" or "DAY" when args.Count == 1 => $"EXTRACT({up} FROM {A(0)})",
                "EOMONTH" when args.Count == 1 => $"CAST(date_trunc('month', {A(0)}) + INTERVAL '1 month' - INTERVAL '1 day' AS date)",
                "REPLICATE" => $"repeat({A(0)}, {A(1)})",
                "SPACE" => $"repeat(' ', {A(0)})",
                "SQUARE" => $"power({A(0)}, 2)",
                "RAND" => "random()",
                "COUNT_BIG" => $"count({A(0)})",
                "STUFF" when args.Count == 4 => $"overlay({A(0)} placing {A(3)} from {A(1)} for {A(2)})",
                _ => null,
            };
            if (up is "ISNUMERIC" or "FORMAT" or "DATENAME" or "OBJECT_ID" or "PATINDEX") Warn($"{up}() has no direct PostgreSQL equivalent — rewrite manually (to_char / regex / pg_catalog).");
            if (up is "TRY_CONVERT" or "TRY_CAST") Warn("TRY_CONVERT/TRY_CAST converted to CAST — PostgreSQL raises instead of returning NULL on failure.");
            if (rep != null) Replace(t, i, close, rep);
        }
    }

    private string ConvertCall(List<List<Tok>> args, Func<int, string> A, string fn)
    {
        var type = args[0].Select(z => new Tok(z.Kind, z.Text)).ToList();
        MapTypes(type);
        if (args.Count > 2) Warn("CONVERT(..., style) date/number styles dropped — use to_char()/to_date() with an explicit format.");
        return $"CAST({A(1)} AS {R(type)})";
    }

    private static string DateDiff(string part, string a, string b) => part switch
    {
        "day" => $"(CAST({b} AS date) - CAST({a} AS date))",
        "year" => $"(EXTRACT(YEAR FROM {b}) - EXTRACT(YEAR FROM {a}))",
        "month" => $"((EXTRACT(YEAR FROM {b}) - EXTRACT(YEAR FROM {a})) * 12 + EXTRACT(MONTH FROM {b}) - EXTRACT(MONTH FROM {a}))",
        "week" => $"(FLOOR((CAST({b} AS date) - CAST({a} AS date)) / 7))",
        "hour" => $"FLOOR(EXTRACT(EPOCH FROM ({b} - {a})) / 3600)",
        "minute" => $"FLOOR(EXTRACT(EPOCH FROM ({b} - {a})) / 60)",
        "second" => $"FLOOR(EXTRACT(EPOCH FROM ({b} - {a})))",
        "milliseconds" => $"FLOOR(EXTRACT(EPOCH FROM ({b} - {a})) * 1000)",
        _ => $"/* TODO(convert): DATEDIFF({part}) */ (EXTRACT(EPOCH FROM ({b} - {a})))",
    };

    // WITH (NOLOCK, ...) / (NOLOCK) table hints -> removed.
    private static readonly HashSet<string> Hints = new(StringComparer.OrdinalIgnoreCase)
    { "NOLOCK","READUNCOMMITTED","READCOMMITTED","ROWLOCK","PAGLOCK","TABLOCK","TABLOCKX","UPDLOCK","XLOCK","HOLDLOCK","SERIALIZABLE","NOWAIT","READPAST","REPEATABLEREAD","INDEX","FORCESEEK","NOEXPAND" };

    private void StripHints(List<Tok> t)
    {
        for (int i = 0; i < t.Count; i++)
        {
            int open = -1, start = i;
            if (t[i].IsWord("WITH") && NextSig(t, i) is var o and >= 0 && t[o].IsSym("(")) open = o;
            else if (t[i].IsSym("(")) { open = i; }
            if (open < 0) continue;
            var first = NextSig(t, open);
            if (first < 0 || !(t[first].Kind == TokKind.Word && Hints.Contains(t[first].Text))) continue;
            var close = MatchParen(t, open);
            if (close < 0) continue;
            var inside = Slice(t, open + 1, close).Where(z => !z.IsTrivia && !z.IsSym(",")).ToList();
            if (inside.Count > 0 && inside.All(z => z.Kind == TokKind.Word && Hints.Contains(z.Text) || z.Kind == TokKind.Number || z.IsSym("(") || z.IsSym(")") || z.IsSym("=")))
            {
                t.RemoveRange(start, close - start + 1);
                i = start - 1;
            }
        }
    }

    private void MapTypes(List<Tok> t)
    {
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind != TokKind.Word) continue;
            var p = PrevSig(t, i);
            if (p >= 0 && t[p].IsSym(".")) continue;
            var n = NextSig(t, i);
            bool paren = n >= 0 && t[n].IsSym("(");
            bool max = paren && NextSig(t, n) is var m and >= 0 && t[m].IsWord("MAX");
            switch (x.Upper)
            {
                case "NVARCHAR" or "VARCHAR":
                    if (max) Replace(t, i, MatchParen(t, n), "text"); else t[i] = new Tok(TokKind.Word, "varchar"); break;
                case "NCHAR": t[i] = new Tok(TokKind.Word, "char"); break;
                case "NTEXT" or "SQL_VARIANT" or "HIERARCHYID": t[i] = new Tok(TokKind.Word, "text"); break;
                case "DATETIME" or "DATETIME2" or "SMALLDATETIME":
                    if (paren) Replace(t, i, MatchParen(t, n), "timestamp"); else t[i] = new Tok(TokKind.Word, "timestamp"); break;
                case "DATETIMEOFFSET":
                    if (paren) Replace(t, i, MatchParen(t, n), "timestamptz"); else t[i] = new Tok(TokKind.Word, "timestamptz"); break;
                case "BIT": t[i] = new Tok(TokKind.Word, "boolean"); Warn("BIT -> boolean: comparisons like col = 1 must become col = true (or col)."); break;
                case "TINYINT": t[i] = new Tok(TokKind.Word, "smallint"); break;
                case "UNIQUEIDENTIFIER": t[i] = new Tok(TokKind.Word, "uuid"); break;
                case "MONEY" or "SMALLMONEY": t[i] = new Tok(TokKind.Word, "numeric(19,4)"); break;
                case "FLOAT":
                    if (paren) Replace(t, i, MatchParen(t, n), "double precision"); else t[i] = new Tok(TokKind.Word, "double precision"); break;
                case "IMAGE" or "ROWVERSION": t[i] = new Tok(TokKind.Word, "bytea"); break;
                case "VARBINARY" or "BINARY":
                    if (paren) Replace(t, i, MatchParen(t, n), "bytea"); else t[i] = new Tok(TokKind.Word, "bytea"); break;
                case "SYSNAME": t[i] = new Tok(TokKind.Word, "varchar(128)"); break;
                case "IDENTITY":
                    if (paren) Replace(t, i, MatchParen(t, n), "GENERATED BY DEFAULT AS IDENTITY");
                    else t[i] = new Tok(TokKind.Word, "GENERATED BY DEFAULT AS IDENTITY");
                    break;
            }
        }
    }

    // ------------------------------------------------------------ view/table

    private string ConvertView(List<Tok> t, int k)
    {
        int asIdx = -1;
        for (int i = NextSig(t, k); i >= 0; i = NextSig(t, i)) if (t[i].IsWord("AS")) { asIdx = i; break; }
        var name = R(Slice(t, NextSig(t, k), asIdx)).Replace("WITH SCHEMABINDING", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!name.Contains('.') && TargetSchema.Length > 0) name = TargetSchema + "." + name;
        var q = Slice(t, asIdx + 1, t.Count).Select(z => new Tok(z.Kind, z.Text)).ToList();
        var query = TopToLimit(q);
        return $"CREATE OR REPLACE VIEW {name} AS\n{query.TrimEnd(';')};";
    }

    // SELECT TOP (n) ... -> SELECT ... LIMIT n (single top-level SELECT only).
    private string TopToLimit(List<Tok> q)
    {
        Expr(q);
        string? limit = null;
        for (int i = 0; i < q.Count; i++)
        {
            if (!q[i].IsWord("TOP")) continue;
            var n = NextSig(q, i);
            if (n < 0) break;
            int endIdx; string val;
            if (q[n].IsSym("(")) { endIdx = MatchParen(q, n); val = R(Slice(q, n + 1, endIdx)); }
            else { endIdx = n; val = q[n].Text; }
            var pct = NextSig(q, endIdx);
            if (pct >= 0 && q[pct].IsWord("PERCENT"))
            {
                q.RemoveRange(i, pct - i + 1);
                Warn("TOP ... PERCENT removed (it's usually TOP 100 PERCENT in a view, which does nothing).");
            }
            else
            {
                q.RemoveRange(i, endIdx - i + 1);
                limit = val;
            }
            break;
        }
        var s = R(q).TrimEnd(';').TrimEnd();
        return limit != null ? s + $"\nLIMIT {limit}" : s;
    }

    private string ConvertTable(List<Tok> t, int k)
    {
        var open = t.FindIndex(k, z => z.IsSym("("));
        var close = MatchParen(t, open);
        var name = R(Slice(t, NextSig(t, k), open));
        if (!name.Contains('.') && TargetSchema.Length > 0) name = TargetSchema + "." + name;
        var inner = Slice(t, open + 1, close).Select(z => new Tok(z.Kind, z.Text)).ToList();
        for (int i = 0; i < inner.Count; i++)
        {
            if (inner[i].IsWordAny("CLUSTERED", "NONCLUSTERED")) { inner.RemoveAt(i); i--; continue; }
            if (inner[i].IsWord("ON") && NextSig(inner, i) is var fg and >= 0 && (inner[fg].IsWord("PRIMARY") && NextSig(inner, fg) is var nx and >= 0 && !inner[nx].IsWord("KEY")))
            { inner.RemoveRange(i, fg - i + 1); i--; continue; }
            if (inner[i].IsWord("WITH") && NextSig(inner, i) is var w and >= 0 && inner[w].IsSym("("))
            { inner.RemoveRange(i, MatchParen(inner, w) - i + 1); i--; continue; }
        }
        Expr(inner);
        return $"CREATE TABLE {name} (\n{R(inner)}\n);";
    }

    // --------------------------------------------------------------- routines

    private string VarName(string at)
    {
        if (at.StartsWith("@@"))
        {
            switch (at.ToUpperInvariant())
            {
                case "@@IDENTITY": return "lastval()";
                case "@@FETCH_STATUS": return "__FETCH_STATUS__";
                case "@@ROWCOUNT": Warn("@@ROWCOUNT -> use GET DIAGNOSTICS v_rows = ROW_COUNT; right after the statement."); return "__ROWCOUNT__";
                default: Warn($"{at} has no PostgreSQL equivalent."); return "/*" + at + "*/NULL";
            }
        }
        if (_vars.TryGetValue(at, out var v)) return v;
        var name = "v_" + at.TrimStart('@').ToLowerInvariant();
        _vars[at] = name;
        return name;
    }

    private string ConvertRoutine(List<Tok> t, int k, bool isFunction)
    {
        _vars = new(StringComparer.OrdinalIgnoreCase);
        int i = NextSig(t, k);
        var nameToks = new List<Tok>();
        while (i >= 0 && (t[i].IsIdent || t[i].IsSym(".")) && t[i].Kind != TokKind.AtVar) { nameToks.Add(t[i]); i = NextSig(t, i); }
        var name = string.Concat(nameToks.Select(z => z.Kind == TokKind.Word && IsSimpleIdent(z.Text) ? Id(z.Text) : z.Text));
        if (!name.Contains('.') && TargetSchema.Length > 0) name = TargetSchema + "." + name;

        // parameters up to RETURNS / AS / WITH
        var paramToks = new List<Tok>();
        int depth = 0;
        bool parenWrapped = i >= 0 && t[i].IsSym("(");
        while (i >= 0)
        {
            if (t[i].IsSym("(")) depth++;
            if (t[i].IsSym(")")) depth--;
            if (depth <= 0 && t[i].IsWordAny("AS", "RETURNS", "WITH", "FOR") && !(t[i].IsWord("AS") && PrevSig(t, i) is var pe and >= 0 && t[pe].IsWordAny("EXECUTE", "EXEC"))) break;
            paramToks.Add(t[i]);
            i = NextSig(t, i);
        }
        if (parenWrapped && paramToks.Count >= 2) paramToks = paramToks.Skip(1).Take(paramToks.Count - 2).ToList();

        var paramDecls = new List<string>();
        var cur = new List<Tok>();
        depth = 0;
        void FlushParam()
        {
            if (cur.Count == 0) return;
            var pn = cur[0].Kind == TokKind.AtVar ? cur[0].Text : "@p" + paramDecls.Count;
            var pgName = "p_" + pn.TrimStart('@').ToLowerInvariant();
            _vars[pn] = pgName;
            bool output = cur.Any(z => z.IsWordAny("OUTPUT", "OUT"));
            int eq = cur.FindIndex(z => z.IsSym("="));
            var typeToks = cur.Skip(1).Take((eq < 0 ? cur.Count : eq) - 1).Where(z => !z.IsWordAny("OUTPUT", "OUT", "READONLY", "VARYING", "AS")).ToList();
            var tt = Spaced(typeToks); MapTypes(tt);
            var s = $"{(output ? "INOUT " : "")}{pgName} {R(tt)}";
            if (eq >= 0)
            {
                var def = Spaced(cur.Skip(eq + 1).Where(z => !z.IsWordAny("OUTPUT", "OUT", "READONLY")).ToList()); Expr(def);
                s += " DEFAULT " + R(def);
            }
            if (cur.Any(z => z.IsWord("READONLY"))) Warn($"table-valued parameter {pn} — pass an array or a temp table instead.");
            paramDecls.Add(s);
            cur = new List<Tok>();
        }
        foreach (var p in paramToks.Where(z => !z.IsTrivia))
        {
            if (p.IsSym("(")) depth++;
            if (p.IsSym(")")) depth--;
            if (depth == 0 && p.IsSym(",")) { FlushParam(); continue; }
            cur.Add(p);
        }
        FlushParam();

        string returns = "";
        if (isFunction && i >= 0 && t[i].IsWord("RETURNS"))
        {
            var rt = new List<Tok>();
            i = NextSig(t, i);
            if (i >= 0 && t[i].Kind == TokKind.AtVar)
            {
                Warn($"multi-statement table function {name} — declare RETURNS TABLE(...) and replace INSERT INTO {t[i].Text} with RETURN QUERY / RETURN NEXT.");
                var tableOpen = NextSig(t, NextSig(t, i));
                var tableClose = MatchParen(t, tableOpen);
                var cols = Slice(t, tableOpen + 1, tableClose).Select(z => new Tok(z.Kind, z.Text)).ToList(); MapTypes(cols);
                returns = $"TABLE ({R(cols)})";
                i = NextSig(t, tableClose);
            }
            else
            {
                while (i >= 0 && !t[i].IsWordAny("AS", "WITH", "BEGIN")) { rt.Add(t[i]); i = NextSig(t, i); }
                var rtl = Spaced(rt); MapTypes(rtl);
                returns = R(rtl);
                if (returns.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    returns = "SETOF record";
                    Warn($"inline table function {name} returns SETOF record — replace with RETURNS TABLE(col type, ...) so callers don't need a column list.");
                }
            }
        }
        while (i >= 0 && !t[i].IsWord("AS")) i = NextSig(t, i); // skip WITH options
        if (i < 0) throw new FormatException("routine body (AS) not found");
        var bodyToks = Slice(t, i + 1, t.Count);

        // Inline TVF: AS RETURN (SELECT ...)
        var bs = FirstSig(bodyToks);
        if (isFunction && bs >= 0 && bodyToks[bs].IsWord("RETURN") && returns.Contains("record"))
        {
            var q = Slice(bodyToks, bs + 1, bodyToks.Count).Select(z => new Tok(z.Kind, z.Text)).ToList();
            var qs = TopToLimit(q).Trim();
            if (qs.StartsWith("(") && qs.EndsWith(")")) qs = qs[1..^1];
            return $"CREATE OR REPLACE FUNCTION {name}({string.Join(", ", paramDecls)})\nRETURNS {returns}\nLANGUAGE sql\nAS $body$\n{qs}\n$body$;";
        }

        var body = TranslateBody(bodyToks, isFunction, out var decls);
        var sb = new StringBuilder();
        sb.Append(isFunction ? "CREATE OR REPLACE FUNCTION " : "CREATE OR REPLACE PROCEDURE ").Append(name).Append('(').Append(string.Join(", ", paramDecls)).AppendLine(")");
        if (isFunction) sb.Append("RETURNS ").AppendLine(returns.Length > 0 ? returns : "void");
        sb.AppendLine("LANGUAGE plpgsql");
        sb.AppendLine("AS $body$");
        if (decls.Count > 0) sb.AppendLine("DECLARE").AppendLine(Indent(string.Join("\n", decls)));
        sb.AppendLine("BEGIN");
        sb.AppendLine(Indent(body));
        sb.AppendLine("END;");
        sb.Append("$body$;");
        Warn("T-SQL procedural code was translated heuristically (T-SQL has no mandatory ';') — review control flow before applying.");
        return sb.ToString();
    }

    private static List<Tok> Spaced(List<Tok> sig)
    {
        var l = new List<Tok>();
        foreach (var z in sig.Where(z => !z.IsTrivia))
        {
            if (l.Count > 0 && !(z.IsSym("(") || z.IsSym(")") || z.IsSym(",") || z.IsSym(".") || l[^1].IsSym("(") || l[^1].IsSym("."))) l.Add(new Tok(TokKind.Space, " "));
            l.Add(new Tok(z.Kind, z.Text));
        }
        return l;
    }

    // ---------------------------------------------------- statement segmenter

    private enum SK { Simple, If, Else, While, Begin, End, BeginTry, EndTry, BeginCatch, EndCatch }
    private sealed record Stmt(SK Kind, List<Tok> Toks);

    private static readonly HashSet<string> StartWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "DECLARE","SET","SELECT","INSERT","UPDATE","DELETE","IF","ELSE","WHILE","BEGIN","END","RETURN","EXEC","EXECUTE","PRINT","RAISERROR",
        "THROW","TRUNCATE","MERGE","WITH","BREAK","CONTINUE","OPEN","FETCH","CLOSE","DEALLOCATE","COMMIT","ROLLBACK","CREATE","DROP","ALTER","GOTO","WAITFOR",
    };

    private List<Stmt> Segment(List<Tok> toks)
    {
        var sig = toks.Where(z => !z.IsTrivia || z.Kind == TokKind.Comment).ToList();
        var list = new List<Stmt>();
        var cur = new List<Tok>();
        int depth = 0, caseDepth = 0;
        bool updateSetSeen = false;

        void Flush(SK kind = SK.Simple)
        {
            if (cur.Any(z => !z.IsTrivia)) list.Add(new Stmt(kind, cur));
            cur = new List<Tok>();
            updateSetSeen = false;
        }
        Tok? FirstWord() => cur.FirstOrDefault(z => !z.IsTrivia);
        Tok? LastSig() => cur.LastOrDefault(z => !z.IsTrivia);

        for (int i = 0; i < sig.Count; i++)
        {
            var x = sig[i];
            if (x.Kind == TokKind.Comment) { cur.Add(x); continue; }
            if (x.IsSym("(")) depth++;
            if (x.IsSym(")")) depth--;
            if (depth > 0) { cur.Add(x); continue; }
            if (x.IsSym(";")) { Flush(); continue; }

            if (x.IsWord("CASE")) { caseDepth++; cur.Add(x); continue; }
            if (caseDepth > 0 && x.IsWord("END")) { caseDepth--; cur.Add(x); continue; }
            if (caseDepth > 0) { cur.Add(x); continue; }

            if (x.Kind == TokKind.Word && StartWords.Contains(x.Text))
            {
                var fw = FirstWord();
                var ls = LastSig();
                bool cont = false;
                if (fw != null)
                {
                    var f = fw.Upper;
                    if (x.IsWord("SELECT") && (f is "INSERT" or "WITH" || (f == "DECLARE" && cur.Any(z => z.IsWord("CURSOR"))) || (ls != null && ls.IsWordAny("UNION", "ALL", "EXCEPT", "INTERSECT")))) cont = true;
                    if (x.IsWord("SET") && f == "UPDATE" && !updateSetSeen) { cont = true; updateSetSeen = true; }
                    if (f == "MERGE") cont = true;
                    if (x.IsWordAny("INSERT", "UPDATE", "DELETE") && f == "WITH") cont = true;
                    if (x.IsWord("EXEC") && f == "INSERT") cont = true;
                    if (x.IsWord("WITH") && f is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE") cont = true;
                    if (x.IsWordAny("COMMIT", "ROLLBACK") && ls != null && ls.IsWord("BEGIN")) cont = true;
                    if (x.IsWord("BEGIN") && ls != null && ls.IsWord("END")) cont = false;
                    if (x.IsWord("SET") && f == "SET") cont = false;
                }
                if (!cont)
                {
                    // IF/WHILE header ends where its body statement starts
                    if (fw != null && fw.IsWordAny("IF", "WHILE")) { Flush(fw.IsWord("IF") ? SK.If : SK.While); }
                    else Flush();

                    if (x.IsWord("BEGIN"))
                    {
                        var nx = i + 1 < sig.Count ? sig[i + 1] : null;
                        if (nx != null && nx.IsWord("TRY")) { list.Add(new Stmt(SK.BeginTry, new())); i++; continue; }
                        if (nx != null && nx.IsWord("CATCH")) { list.Add(new Stmt(SK.BeginCatch, new())); i++; continue; }
                        if (nx != null && nx.IsWordAny("TRAN", "TRANSACTION", "DISTRIBUTED"))
                        {
                            list.Add(new Stmt(SK.Simple, new() { new Tok(TokKind.Comment, "-- BEGIN TRANSACTION: PostgreSQL procedures already run inside a transaction") }));
                            i++; if (i + 1 < sig.Count && sig[i + 1].IsIdent && !StartWords.Contains(sig[i + 1].Text)) i++;
                            continue;
                        }
                        list.Add(new Stmt(SK.Begin, new())); continue;
                    }
                    if (x.IsWord("END"))
                    {
                        var nx = i + 1 < sig.Count ? sig[i + 1] : null;
                        if (nx != null && nx.IsWord("TRY")) { list.Add(new Stmt(SK.EndTry, new())); i++; continue; }
                        if (nx != null && nx.IsWord("CATCH")) { list.Add(new Stmt(SK.EndCatch, new())); i++; continue; }
                        list.Add(new Stmt(SK.End, new())); continue;
                    }
                    if (x.IsWord("ELSE")) { list.Add(new Stmt(SK.Else, new())); continue; }
                    cur.Add(x);
                    if (x.IsWordAny("IF", "WHILE")) { /* header continues until the next start word */ }
                    continue;
                }
            }
            cur.Add(x);
        }
        var last = FirstWord();
        Flush(last != null && last.IsWord("IF") ? SK.If : last != null && last.IsWord("WHILE") ? SK.While : SK.Simple);
        return list;
    }

    // ------------------------------------------------------- tree + emission

    private abstract record Node;
    private sealed record Leaf(List<Tok> Toks) : Node;
    private sealed record Block(List<Node> Items) : Node;
    private sealed record IfNode(List<Tok> Cond, Node Then, Node? Else) : Node;
    private sealed record WhileNode(List<Tok> Cond, Node Body) : Node;
    private sealed record TryNode(Block Try, Block Catch) : Node;

    private Node? ParseOne(List<Stmt> s, ref int i)
    {
        if (i >= s.Count) return null;
        var st = s[i++];
        switch (st.Kind)
        {
            case SK.Begin:
                {
                    var items = new List<Node>();
                    while (i < s.Count && s[i].Kind != SK.End) { var n = ParseOne(s, ref i); if (n != null) items.Add(n); }
                    i++; // END
                    return new Block(items);
                }
            case SK.BeginTry:
                {
                    var tr = new List<Node>();
                    while (i < s.Count && s[i].Kind != SK.EndTry) { var n = ParseOne(s, ref i); if (n != null) tr.Add(n); }
                    i++;
                    var ca = new List<Node>();
                    if (i < s.Count && s[i].Kind == SK.BeginCatch)
                    {
                        i++;
                        while (i < s.Count && s[i].Kind != SK.EndCatch) { var n = ParseOne(s, ref i); if (n != null) ca.Add(n); }
                        i++;
                    }
                    return new TryNode(new Block(tr), new Block(ca));
                }
            case SK.If:
                {
                    var cond = st.Toks.Skip(1).ToList();
                    var then = ParseOne(s, ref i) ?? new Block(new());
                    Node? els = null;
                    if (i < s.Count && s[i].Kind == SK.Else) { i++; els = ParseOne(s, ref i); }
                    return new IfNode(cond, then, els);
                }
            case SK.While:
                {
                    var cond = st.Toks.Skip(1).ToList();
                    var body = ParseOne(s, ref i) ?? new Block(new());
                    return new WhileNode(cond, body);
                }
            case SK.End or SK.EndTry or SK.EndCatch or SK.Else or SK.BeginCatch:
                return null; // stray
            default:
                return new Leaf(st.Toks);
        }
    }

    private string TranslateBody(List<Tok> toks, bool isFunction, out List<string> decls)
    {
        var d = new List<string>();
        var stmts = Segment(toks);
        // Unwrap a single outer BEGIN ... END.
        int idx = 0;
        var nodes = new List<Node>();
        while (idx < stmts.Count) { var n = ParseOne(stmts, ref idx); if (n != null) nodes.Add(n); }
        if (nodes.Count == 1 && nodes[0] is Block b) nodes = b.Items;
        var sb = new StringBuilder();
        foreach (var n in nodes) Emit(n, sb, 0, d, isFunction);
        decls = d;
        return sb.ToString().TrimEnd();
    }

    private void Emit(Node n, StringBuilder sb, int level, List<string> decls, bool isFunction)
    {
        var pad = new string(' ', level * 4);
        switch (n)
        {
            case Block b:
                foreach (var c in b.Items) Emit(c, sb, level, decls, isFunction);
                break;
            case IfNode f:
                sb.Append(pad).Append("IF ").Append(Cond(f.Cond)).AppendLine(" THEN");
                Emit(f.Then, sb, level + 1, decls, isFunction);
                if (f.Else is IfNode elseIf)
                {
                    sb.Append(pad).AppendLine("ELSE");
                    Emit(elseIf, sb, level + 1, decls, isFunction);
                }
                else if (f.Else != null)
                {
                    sb.Append(pad).AppendLine("ELSE");
                    Emit(f.Else, sb, level + 1, decls, isFunction);
                }
                sb.Append(pad).AppendLine("END IF;");
                break;
            case WhileNode w:
                sb.Append(pad).Append("WHILE ").Append(Cond(w.Cond)).AppendLine(" LOOP");
                Emit(w.Body, sb, level + 1, decls, isFunction);
                sb.Append(pad).AppendLine("END LOOP;");
                break;
            case TryNode tr:
                sb.Append(pad).AppendLine("BEGIN");
                Emit(tr.Try, sb, level + 1, decls, isFunction);
                sb.Append(pad).AppendLine("EXCEPTION WHEN OTHERS THEN");
                Emit(tr.Catch, sb, level + 1, decls, isFunction);
                sb.Append(pad).AppendLine("END;");
                break;
            case Leaf l:
                foreach (var line in Statement(l.Toks, decls, isFunction))
                    sb.Append(pad).AppendLine(line);
                break;
        }
    }

    private string Cond(List<Tok> cond)
    {
        var c = Spaced(cond); Expr(c);
        return R(c).Replace("__FETCH_STATUS__ = 0", "FOUND").Replace("__FETCH_STATUS__ <> 0", "NOT FOUND").Replace("__FETCH_STATUS__ != 0", "NOT FOUND").Replace("__ROWCOUNT__", "/* TODO(convert): @@ROWCOUNT */ 0");
    }

    private IEnumerable<string> Statement(List<Tok> raw, List<string> decls, bool isFunction)
    {
        var comments = raw.Where(z => z.Kind == TokKind.Comment).Select(z => z.Text.TrimEnd()).ToList();
        foreach (var c in comments) yield return c;
        var sig = raw.Where(z => !z.IsTrivia).ToList();
        if (sig.Count == 0) yield break;
        var head = sig[0].Upper;

        switch (head)
        {
            case "DECLARE":
                {
                    // DECLARE @a INT = 1, @b VARCHAR(10) | DECLARE c CURSOR FOR SELECT ...
                    if (sig.Count > 2 && sig[2].IsWord("CURSOR"))
                    {
                        var forIdx = sig.FindIndex(z => z.IsWord("FOR"));
                        var q = Spaced(sig.Skip(forIdx + 1).ToList()); Expr(q);
                        decls.Add($"{Id(sig[1].Text)} CURSOR FOR {R(q)};");
                        yield break;
                    }
                    foreach (var part in SplitTop(sig.Skip(1).ToList()))
                    {
                        if (part.Count == 0) continue;
                        var v = VarName(part[0].Text);
                        int eq = part.FindIndex(z => z.IsSym("="));
                        var typeToks = Spaced(part.Skip(1).Take((eq < 0 ? part.Count : eq) - 1).Where(z => !z.IsWord("AS")).ToList());
                        if (typeToks.Any(z => z.IsWord("TABLE")))
                        {
                            Warn($"table variable {part[0].Text} — use a TEMP table (CREATE TEMP TABLE ...) instead.");
                            decls.Add($"-- TODO(convert): table variable {part[0].Text} {R(typeToks)}");
                            continue;
                        }
                        MapTypes(typeToks);
                        decls.Add($"{v} {R(typeToks)};");
                        if (eq >= 0)
                        {
                            var val = Spaced(part.Skip(eq + 1).ToList()); Expr(val);
                            yield return $"{v} := {R(val)};";
                        }
                    }
                    yield break;
                }
            case "SET":
                {
                    if (sig.Count > 1 && sig[1].Kind == TokKind.AtVar)
                    {
                        int eq = sig.FindIndex(z => z.IsSym("="));
                        var val = Spaced(sig.Skip(eq + 1).ToList()); Expr(val);
                        yield return $"{VarName(sig[1].Text)} := {R(val)};";
                    }
                    else yield return "-- " + R(Spaced(sig)) + "  (session option dropped)";
                    yield break;
                }
            case "SELECT" when sig.Count > 2 && sig[1].Kind == TokKind.AtVar && sig[2].IsSym("="):
                {
                    // SELECT @a = x, @b = y FROM ...  -> SELECT x, y INTO v_a, v_b FROM ...
                    var fromIdx = IndexOfTop(sig, "FROM");
                    var selList = SplitTop(sig.Skip(1).Take((fromIdx < 0 ? sig.Count : fromIdx) - 1).ToList());
                    var targets = new List<string>(); var exprs = new List<string>();
                    foreach (var item in selList)
                    {
                        int eq = item.FindIndex(z => z.IsSym("="));
                        targets.Add(VarName(item[0].Text));
                        var e = Spaced(item.Skip(eq + 1).ToList()); Expr(e);
                        exprs.Add(R(e));
                    }
                    if (fromIdx < 0)
                    {
                        for (int k = 0; k < targets.Count; k++) yield return $"{targets[k]} := {exprs[k]};";
                    }
                    else
                    {
                        var rest = Spaced(sig.Skip(fromIdx).ToList()); Expr(rest);
                        yield return $"SELECT {string.Join(", ", exprs)} INTO {string.Join(", ", targets)} {R(rest)};";
                    }
                    yield break;
                }
            case "PRINT":
                {
                    var e = Spaced(sig.Skip(1).ToList()); Expr(e);
                    yield return $"RAISE NOTICE '%', {R(e)};";
                    yield break;
                }
            case "RAISERROR":
                {
                    var open = sig.FindIndex(z => z.IsSym("("));
                    var args = open >= 0 ? SplitArgs(sig, open, MatchParen(sig, open)) : new List<List<Tok>>();
                    var msg = args.Count > 0 ? ExprText(args[0]) : "'error'";
                    var sev = args.Count > 1 && int.TryParse(R(args[1]), out var sv) ? sv : 16;
                    yield return sev <= 10 ? $"RAISE NOTICE '%', {msg};" : $"RAISE EXCEPTION '%', {msg};";
                    yield break;
                }
            case "THROW":
                {
                    if (sig.Count == 1) { yield return "RAISE;"; yield break; }
                    var args = SplitTop(sig.Skip(1).ToList());
                    yield return $"RAISE EXCEPTION '%', {(args.Count > 1 ? ExprText(args[1]) : "'error'")} USING ERRCODE = 'P0001';";
                    yield break;
                }
            case "EXEC" or "EXECUTE":
                {
                    var rest = sig.Skip(1).ToList();
                    if (rest.Count > 0 && rest[0].IsSym("("))
                    {
                        yield return $"EXECUTE {ExprText(rest)};";
                        Warn("dynamic SQL EXEC(@sql) -> EXECUTE v_sql; the SQL text itself must be valid PostgreSQL.");
                        yield break;
                    }
                    if (rest.Count > 0 && rest[0].IsWord("sp_executesql"))
                    {
                        var a = SplitTop(rest.Skip(1).ToList());
                        yield return $"EXECUTE {(a.Count > 0 ? ExprText(a[0]) : "''")};  -- TODO(convert): sp_executesql parameters -> USING ...";
                        Warn("sp_executesql -> EXECUTE ... USING; parameters need manual mapping.");
                        yield break;
                    }
                    // EXEC [@ret =] proc @a, @b = x
                    int ni = 0;
                    if (rest.Count > 2 && rest[0].Kind == TokKind.AtVar && rest[1].IsSym("=")) ni = 2;
                    var nameParts = new List<string>();
                    while (ni < rest.Count && (rest[ni].IsIdent || rest[ni].IsSym(".")) && rest[ni].Kind != TokKind.AtVar) { nameParts.Add(rest[ni].Text); ni++; }
                    var args = SplitTop(rest.Skip(ni).ToList()).Where(a => a.Count > 0).Select(a =>
                    {
                        var clean = a.Where(z => !z.IsWordAny("OUTPUT", "OUT")).ToList();
                        if (clean.Count > 2 && clean[0].Kind == TokKind.AtVar && clean[1].IsSym("="))
                            return $"p_{clean[0].Text.TrimStart('@').ToLowerInvariant()} => {ExprText(clean.Skip(2).ToList())}";
                        return ExprText(clean);
                    });
                    var procName = string.Concat(nameParts);
                    if (!procName.Contains('.') && TargetSchema.Length > 0) procName = TargetSchema + "." + procName;
                    yield return $"CALL {procName}({string.Join(", ", args)});";
                    yield break;
                }
            case "RETURN":
                {
                    if (isFunction) { yield return $"RETURN {ExprText(sig.Skip(1).ToList())};".Replace("RETURN ;", "RETURN;"); yield break; }
                    if (sig.Count > 1) Warn("RETURN <value> in a procedure — PostgreSQL procedures can't return a value; use an INOUT parameter.");
                    yield return "RETURN;";
                    yield break;
                }
            case "BREAK": yield return "EXIT;"; yield break;
            case "CONTINUE": yield return "CONTINUE;"; yield break;
            case "COMMIT" or "ROLLBACK": yield return head + ";"; yield break;
            case "DEALLOCATE": yield return "-- " + R(Spaced(sig)) + "  (not needed)"; yield break;
            case "OPEN" or "CLOSE": yield return $"{head} {Id(sig[1].Text)};"; yield break;
            case "FETCH":
                {
                    var from = sig.FindIndex(z => z.IsWord("FROM"));
                    var into = sig.FindIndex(z => z.IsWord("INTO"));
                    var cursor = from >= 0 ? sig[from + 1].Text : sig[^1].Text;
                    var vars = into >= 0 ? string.Join(", ", sig.Skip(into + 1).Where(z => z.Kind == TokKind.AtVar).Select(z => VarName(z.Text))) : "";
                    yield return $"FETCH {Id(cursor)}{(vars.Length > 0 ? " INTO " + vars : "")};";
                    yield break;
                }
            case "GOTO":
                Warn("GOTO has no PL/pgSQL equivalent — restructure with loops/EXIT.");
                yield return "-- TODO(convert): " + R(Spaced(sig));
                yield break;
            case "WAITFOR":
                yield return "PERFORM pg_sleep(1);  -- TODO(convert): " + R(Spaced(sig));
                yield break;
        }

        var toks = Spaced(sig);
        var text = TopToLimit(toks);
        if (head == "SELECT" && !Regex.IsMatch(text, @"\bINTO\b", RegexOptions.IgnoreCase))
        {
            Warn("a bare SELECT returns a result set in T-SQL — in PostgreSQL use a function RETURNS TABLE(...) with RETURN QUERY, or a refcursor.");
            yield return "-- TODO(convert): result set -> RETURN QUERY (needs RETURNS TABLE) or OPEN refcursor FOR";
        }
        text = text.Replace("__ROWCOUNT__", "0 /* TODO(convert): @@ROWCOUNT */").Replace("__FETCH_STATUS__", "0 /* TODO(convert): @@FETCH_STATUS */");
        yield return text + ";";
    }

    private string ExprText(List<Tok> toks) { var l = Spaced(toks); Expr(l); return R(l); }

    private static List<List<Tok>> SplitTop(List<Tok> sig)
    {
        var parts = new List<List<Tok>>();
        var cur = new List<Tok>();
        int depth = 0;
        foreach (var z in sig)
        {
            if (z.IsSym("(")) depth++;
            if (z.IsSym(")")) depth--;
            if (depth == 0 && z.IsSym(",")) { parts.Add(cur); cur = new(); continue; }
            cur.Add(z);
        }
        parts.Add(cur);
        return parts;
    }

    private static int IndexOfTop(List<Tok> sig, string word)
    {
        int depth = 0;
        for (int i = 0; i < sig.Count; i++)
        {
            if (sig[i].IsSym("(")) depth++;
            if (sig[i].IsSym(")")) depth--;
            if (depth == 0 && sig[i].IsWord(word)) return i;
        }
        return -1;
    }

    private static string Indent(string s) => string.Join("\n", s.Replace("\r", "").Split('\n').Select(l => l.Length == 0 ? l : "    " + l.TrimEnd()));
}
