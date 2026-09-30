using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PgBackupManager.Core.Providers;
using static PgBackupManager.Core.Sql.TokenOps;

namespace PgBackupManager.Core.Sql;

// MySQL / MariaDB -> PostgreSQL: DDL types, backtick identifiers, built-ins,
// LIMIT a,b, views (DEFINER/ALGORITHM stripped) and stored routines (MySQL's
// block syntax is already close to PL/pgSQL: IF/THEN/END IF, WHILE/DO,
// DECLARE, SET x = ...).
internal sealed class MySqlToPostgres
{
    private readonly ConvertContext _ctx;
    private readonly List<string> _warn = new();
    private readonly bool _lower;

    public MySqlToPostgres(ConvertContext ctx)
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
        foreach (var st in SqlScriptSplitter.SplitDetailed(source, ScriptDialect.MySql).Where(s => s.Kind == StatementKind.Sql))
        {
            string c;
            try { c = ConvertStatement(st.Text); }
            catch (Exception ex)
            {
                Warn($"could not convert a statement ({ex.Message}) — original kept as a comment.");
                c = "/* TODO(convert):\n" + st.Text.Replace("*/", "* /") + "\n*/";
            }
            sb.AppendLine(c.TrimEnd()).AppendLine();
        }
        return new ConversionResult(SqlCodeConverter.Header("MySQL", "PostgreSQL", _warn) + "\n" + sb.ToString().TrimEnd() + "\n", _warn.Distinct().ToList());
    }

    private string ConvertStatement(string text)
    {
        var t = SqlTokenizer.Tokenize(text, ScriptDialect.MySql);
        for (int i = 0; i < t.Count; i++)
        {
            if (t[i].Kind == TokKind.QuotedIdent) t[i] = new Tok(TokKind.Word, Id(t[i].Ident));
            if (!string.IsNullOrEmpty(_ctx.SourceSchema) && t[i].IsIdent && string.Equals(t[i].Ident, _ctx.SourceSchema, StringComparison.OrdinalIgnoreCase)
                && NextSig(t, i) is var d and >= 0 && t[d].IsSym(".") && TargetSchema.Length > 0)
                t[i] = new Tok(TokKind.Word, TargetSchema);
        }

        // Drop ALGORITHM=... DEFINER=... SQL SECURITY ... from CREATE headers.
        int c = FirstSig(t);
        if (c >= 0 && t[c].IsWord("CREATE"))
        {
            int k = NextSig(t, c);
            while (k >= 0)
            {
                if (t[k].IsWordAny("OR", "REPLACE")) { k = NextSig(t, k); continue; }
                if (!t[k].IsWordAny("ALGORITHM", "DEFINER", "SQL")) break;
                int e;
                if (t[k].IsWord("SQL")) e = NextSig(t, NextSig(t, k));            // SQL SECURITY DEFINER|INVOKER
                else if (t[k].IsWord("ALGORITHM")) e = NextSig(t, NextSig(t, k)); // ALGORITHM = X
                else
                {
                    e = k;                                                        // DEFINER = `user`@`host`
                    while (NextSig(t, e) is var nx and >= 0 && !t[nx].IsWordAny("VIEW", "PROCEDURE", "FUNCTION", "TRIGGER", "EVENT", "ALGORITHM", "SQL")) e = nx;
                }
                t.RemoveRange(k, e - k + 1);
            }
            k = NextSig(t, c);
            while (k >= 0 && t[k].IsWordAny("OR", "REPLACE")) k = NextSig(t, k);
            if (k >= 0)
            {
                switch (t[k].Upper)
                {
                    case "VIEW":
                        Expr(t);
                        var s = R(t);
                        s = Regex.Replace(s, @"^CREATE\s+(OR\s+REPLACE\s+)?VIEW", "CREATE OR REPLACE VIEW", RegexOptions.IgnoreCase);
                        return s.TrimEnd(';') + ";";
                    case "TABLE": return ConvertTable(t, k);
                    case "PROCEDURE":
                    case "FUNCTION": return ConvertRoutine(t, k);
                    case "TRIGGER":
                        Warn("MySQL triggers: rewrite as a PL/pgSQL trigger function + CREATE TRIGGER (NEW/OLD work the same).");
                        return "/* TODO(convert): trigger\n" + text.Replace("*/", "* /") + "\n*/";
                }
            }
        }
        if (c >= 0 && t[c].IsWordAny("LOCK", "UNLOCK")) return "-- " + R(t) + "  (table locks from mysqldump — not needed)";
        if (c >= 0 && t[c].IsWord("SET") && R(t).Contains("@")) return "-- " + R(t) + "  (mysqldump session variable)";
        Expr(t);
        return R(t).TrimEnd(';') + ";";
    }

    private void Expr(List<Tok> t)
    {
        MapTypes(t);
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind != TokKind.Word) continue;
            var up = x.Upper;
            var n = NextSig(t, i);
            bool call = n >= 0 && t[n].IsSym("(");

            if (up == "LIMIT" && n >= 0 && t[n].Kind == TokKind.Number)
            {
                var comma = NextSig(t, n);
                if (comma >= 0 && t[comma].IsSym(","))
                {
                    var cnt = NextSig(t, comma);
                    Replace(t, i, cnt, $"LIMIT {t[cnt].Text} OFFSET {t[n].Text}");
                }
                continue;
            }
            if (!call) continue;
            var close = MatchParen(t, n);
            if (close < 0) continue;
            var args = SplitArgs(t, n, close);
            string A(int k) { var a = args[k].Select(z => new Tok(z.Kind, z.Text)).ToList(); Expr(a); return R(a); }
            string? rep = up switch
            {
                "IFNULL" when args.Count == 2 => $"COALESCE({A(0)}, {A(1)})",
                "IF" when args.Count == 3 => $"CASE WHEN {A(0)} THEN {A(1)} ELSE {A(2)} END",
                "CURDATE" => "CURRENT_DATE",
                "CURTIME" => "LOCALTIME",
                "NOW" or "SYSDATE" => "LOCALTIMESTAMP",
                "UUID" => "gen_random_uuid()",
                "RAND" => "random()",
                "LCASE" => $"lower({A(0)})",
                "UCASE" => $"upper({A(0)})",
                "DATE_ADD" when args.Count == 2 => $"({A(0)} + {A(1)})",
                "DATE_SUB" when args.Count == 2 => $"({A(0)} - {A(1)})",
                "DATEDIFF" when args.Count == 2 => $"(CAST({A(0)} AS date) - CAST({A(1)} AS date))",
                "UNIX_TIMESTAMP" when args.Count == 0 => "EXTRACT(EPOCH FROM now())::bigint",
                "FROM_UNIXTIME" when args.Count == 1 => $"to_timestamp({A(0)})",
                "LOCATE" when args.Count == 2 => $"POSITION({A(0)} IN {A(1)})",
                "GROUP_CONCAT" => GroupConcat(t, n, close),
                _ => null,
            };
            if (up is "DATE_FORMAT" or "STR_TO_DATE") Warn($"{up}() uses MySQL % formats — rewrite with to_char()/to_timestamp() format patterns.");
            if (up == "FOUND_ROWS" || up == "LAST_INSERT_ID") Warn($"{up}() -> use RETURNING / GET DIAGNOSTICS / lastval().");
            if (rep != null) Replace(t, i, close, rep);
        }
    }

    private string GroupConcat(List<Tok> t, int open, int close)
    {
        var inner = Slice(t, open + 1, close).Select(z => new Tok(z.Kind, z.Text)).ToList();
        string sep = "','";
        int si = inner.FindIndex(z => z.IsWord("SEPARATOR"));
        if (si >= 0) { sep = R(inner.Skip(si + 1).ToList()); inner = inner.Take(si).ToList(); }
        int oi = inner.FindIndex(z => z.IsWord("ORDER"));
        var order = oi >= 0 ? " " + R(inner.Skip(oi).ToList()) : "";
        var expr = oi >= 0 ? inner.Take(oi).ToList() : inner;
        var distinct = expr.FirstOrDefault(z => !z.IsTrivia)?.IsWord("DISTINCT") == true;
        Expr(expr);
        return $"string_agg({R(expr)}::text, {sep}{order})";
    }

    private void MapTypes(List<Tok> t)
    {
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind != TokKind.Word) continue;
            var n = NextSig(t, i);
            bool paren = n >= 0 && t[n].IsSym("(");
            int end = paren ? MatchParen(t, n) : i;
            string? rep = x.Upper switch
            {
                "TINYINT" when paren && R(Slice(t, n + 1, end)) == "1" => "boolean",
                "TINYINT" or "SMALLINT" or "YEAR" => "smallint",
                "MEDIUMINT" or "INT" or "INTEGER" => "integer",
                "BIGINT" => "bigint",
                "DOUBLE" => "double precision",
                "FLOAT" => "real",
                "DATETIME" or "TIMESTAMP" => "timestamp",
                "TINYTEXT" or "MEDIUMTEXT" or "LONGTEXT" => "text",
                "TINYBLOB" or "BLOB" or "MEDIUMBLOB" or "LONGBLOB" or "VARBINARY" or "BINARY" => "bytea",
                "ENUM" or "SET" when paren && PrevSig(t, i) is var p && (p < 0 || !t[p].IsWord("CHARACTER")) => "varchar(255)",
                _ => null,
            };
            if (rep == null) continue;
            bool keepArgs = x.Upper is "DATETIME" or "TIMESTAMP" && paren;
            if (x.Upper is "ENUM" or "SET") Warn("ENUM/SET columns -> varchar(255); add a CHECK constraint or a PostgreSQL enum type if needed.");
            if (keepArgs) t[i] = new Tok(TokKind.Word, rep);
            else Replace(t, i, end, rep);
            // UNSIGNED / ZEROFILL modifiers
            var m = NextSig(t, i);
            while (m >= 0 && t[m].IsWordAny("UNSIGNED", "ZEROFILL", "SIGNED")) { t.RemoveRange(i + 1, m - i); m = NextSig(t, i); }
        }
    }

    private string ConvertTable(List<Tok> t, int k)
    {
        var open = t.FindIndex(k, z => z.IsSym("("));
        var close = MatchParen(t, open);
        var name = R(Slice(t, NextSig(t, k), open)).Replace("IF NOT EXISTS", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!name.Contains('.') && TargetSchema.Length > 0) name = TargetSchema + "." + name;
        var parts = SplitArgs(t, open, close);
        var outParts = new List<string>();
        foreach (var p in parts)
        {
            var toks = p.Select(z => new Tok(z.Kind, z.Text)).ToList();
            var first = toks.FirstOrDefault(z => !z.IsTrivia);
            if (first == null) continue;
            if (first.IsWordAny("KEY", "INDEX", "FULLTEXT", "SPATIAL") || (first.IsWord("UNIQUE") && toks.Any(z => z.IsWordAny("KEY", "INDEX"))))
            {
                Warn("inline KEY/INDEX definitions moved out — create them with CREATE INDEX (listed as comments).");
                outParts.Add("-- TODO(convert): CREATE INDEX for " + R(toks));
                continue;
            }
            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].IsWord("AUTO_INCREMENT")) toks[i] = new Tok(TokKind.Word, "GENERATED BY DEFAULT AS IDENTITY");
                else if (toks[i].IsWord("COMMENT") && NextSig(toks, i) is var cs and >= 0 && toks[cs].Kind == TokKind.String) { toks.RemoveRange(i, cs - i + 1); i--; }
                else if (toks[i].IsWordAny("CHARACTER", "CHARSET", "COLLATE"))
                {
                    var e = NextSig(toks, i); if (toks[i].IsWord("CHARACTER")) e = NextSig(toks, e);
                    toks.RemoveRange(i, e - i + 1); i--;
                }
                else if (toks[i].IsWord("ON") && NextSig(toks, i) is var u and >= 0 && toks[u].IsWord("UPDATE"))
                {
                    var e = NextSig(toks, u); var e2 = NextSig(toks, e);
                    if (e2 >= 0 && toks[e2].IsSym("(")) e2 = MatchParen(toks, e2); else e2 = e;
                    toks.RemoveRange(i, e2 - i + 1); i--;
                    Warn("ON UPDATE CURRENT_TIMESTAMP needs a BEFORE UPDATE trigger in PostgreSQL.");
                }
            }
            Expr(toks);
            outParts.Add(R(toks));
        }
        var cols = outParts.Where(s => !s.StartsWith("--")).ToList();
        var notes = outParts.Where(s => s.StartsWith("--")).ToList();
        return $"CREATE TABLE {name} (\n    {string.Join(",\n    ", cols)}\n);" + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "");
    }

    private string ConvertRoutine(List<Tok> t, int k)
    {
        var isFunc = t[k].IsWord("FUNCTION");
        var nameIdx = NextSig(t, k);
        var open = t.FindIndex(nameIdx, z => z.IsSym("("));
        var close = MatchParen(t, open);
        var name = R(Slice(t, nameIdx, open));
        if (!name.Contains('.') && TargetSchema.Length > 0) name = TargetSchema + "." + name;
        var ps = SplitArgs(t, open, close).Where(a => a.Any(z => !z.IsTrivia)).Select(a =>
        {
            var sig = a.Where(z => !z.IsTrivia).Select(z => new Tok(z.Kind, z.Text)).ToList();
            string mode = "";
            if (sig[0].IsWordAny("IN", "OUT", "INOUT")) { mode = sig[0].IsWord("IN") ? "" : sig[0].Upper + " "; sig.RemoveAt(0); }
            var rest = sig.Skip(1).ToList();
            var spaced = new List<Tok>();
            foreach (var z in rest) { if (spaced.Count > 0 && !z.IsSym("(") && !z.IsSym(")") && !z.IsSym(",")) spaced.Add(new Tok(TokKind.Space, " ")); spaced.Add(z); }
            MapTypes(spaced);
            return $"{mode}{Id(sig[0].Text)} {R(spaced)}";
        }).ToList();

        int i = NextSig(t, close);
        string returns = "";
        if (isFunc && i >= 0 && t[i].IsWord("RETURNS"))
        {
            var rt = new List<Tok>();
            i = NextSig(t, i);
            while (i >= 0 && !t[i].IsWordAny("BEGIN", "DETERMINISTIC", "NOT", "READS", "MODIFIES", "CONTAINS", "NO", "LANGUAGE", "SQL", "COMMENT", "RETURN")) { rt.Add(t[i]); i = NextSig(t, i); }
            var rl = rt.Select(z => new Tok(z.Kind, z.Text)).ToList(); MapTypes(rl);
            returns = R(rl);
        }
        var begin = FindWordIndex(t, i, "BEGIN");
        if (begin < 0)
        {
            // single-statement body
            var stmt = Slice(t, i, t.Count).Select(z => new Tok(z.Kind, z.Text)).ToList(); Expr(stmt);
            return Emit(isFunc, name, ps, returns, "", R(stmt).TrimEnd(';') + ";");
        }
        var end = MatchBlockEnd(t, begin);
        var body = Slice(t, begin + 1, end).Select(z => new Tok(z.Kind, z.Text)).ToList();

        // Hoist DECLARE v type [DEFAULT x]; (cursors and handlers flagged)
        var decls = new List<string>();
        var stmts = SqlTokenizer.Render(body);
        var declRx = new Regex(@"^\s*DECLARE\s+([^;]+);", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        stmts = declRx.Replace(stmts, m =>
        {
            var d = m.Groups[1].Value.Trim();
            if (Regex.IsMatch(d, @"\bHANDLER\b", RegexOptions.IgnoreCase))
            {
                Warn("DECLARE ... HANDLER -> use an EXCEPTION block (or IF NOT FOUND after FETCH).");
                return "-- TODO(convert): DECLARE " + d + ";";
            }
            var cm = Regex.Match(d, @"^(\w+)\s+CURSOR\s+FOR\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (cm.Success) { decls.Add($"{Id(cm.Groups[1].Value)} CURSOR FOR {cm.Groups[2].Value};"); return ""; }
            var dt = SqlTokenizer.Tokenize(d, ScriptDialect.MySql); MapTypes(dt);
            var txt = R(dt);
            var vm = Regex.Match(txt, @"^([\w\s,]+?)\s+(\w[\w\s\(\),]*?)(\s+DEFAULT\s+(.+))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (vm.Success)
                foreach (var v in vm.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    decls.Add($"{Id(v)} {vm.Groups[2].Value.Trim()}{(vm.Groups[4].Success ? " := " + vm.Groups[4].Value.Trim() : "")};");
            else decls.Add(txt + ";");
            return "";
        });
        // Block syntax differences
        stmts = Regex.Replace(stmts, @"\bSET\s+(\w+)\s*=", "$1 :=", RegexOptions.IgnoreCase);
        stmts = Regex.Replace(stmts, @"\bEND\s+WHILE\b", "END LOOP", RegexOptions.IgnoreCase);
        stmts = Regex.Replace(stmts, @"\bWHILE\b(.+?)\bDO\b", "WHILE$1LOOP", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        stmts = Regex.Replace(stmts, @"\bREPEAT\b(.+?)\bUNTIL\b(.+?)\bEND\s+REPEAT\b", "LOOP$1EXIT WHEN$2; END LOOP", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        stmts = Regex.Replace(stmts, @"\bLEAVE\b", "EXIT", RegexOptions.IgnoreCase);
        stmts = Regex.Replace(stmts, @"\bITERATE\b", "CONTINUE", RegexOptions.IgnoreCase);
        stmts = Regex.Replace(stmts, @"\bSIGNAL\s+SQLSTATE\s+'(\w+)'\s+SET\s+MESSAGE_TEXT\s*=\s*([^;]+);", "RAISE EXCEPTION '%', $2 USING ERRCODE = '$1';", RegexOptions.IgnoreCase);
        stmts = Regex.Replace(stmts, @"(\w+)\s*:\s*LOOP\b", "<<$1>> LOOP", RegexOptions.IgnoreCase);
        var bt = SqlTokenizer.Tokenize(stmts, ScriptDialect.MySql); Expr(bt);
        Warn("MySQL routine converted — check CALL vs PERFORM for calls, and cursor/handler logic.");
        return Emit(isFunc, name, ps, returns, string.Join("\n", decls), R(bt));
    }

    private static int FindWordIndex(List<Tok> t, int from, string w)
    {
        for (int i = Math.Max(0, from); i < t.Count; i++) if (t[i].IsWord(w)) return i;
        return -1;
    }

    private static string Emit(bool isFunc, string name, List<string> ps, string returns, string decls, string body)
    {
        var sb = new StringBuilder();
        sb.Append(isFunc ? "CREATE OR REPLACE FUNCTION " : "CREATE OR REPLACE PROCEDURE ").Append(name).Append('(').Append(string.Join(", ", ps)).AppendLine(")");
        if (isFunc) sb.Append("RETURNS ").AppendLine(returns.Length > 0 ? returns : "void");
        sb.AppendLine("LANGUAGE plpgsql").AppendLine("AS $body$");
        if (decls.Trim().Length > 0) sb.AppendLine("DECLARE").AppendLine("    " + decls.Replace("\n", "\n    "));
        sb.AppendLine("BEGIN").AppendLine("    " + body.Trim().Replace("\n", "\n    ")).AppendLine("END;").Append("$body$;");
        return sb.ToString();
    }
}
