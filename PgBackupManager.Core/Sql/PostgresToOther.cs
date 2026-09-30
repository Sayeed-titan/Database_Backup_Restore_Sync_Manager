using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;
using static PgBackupManager.Core.Sql.TokenOps;

namespace PgBackupManager.Core.Sql;

// PostgreSQL -> Oracle / SQL Server / MySQL / SQLite for the declarative
// parts (tables, views, indexes, sequences, plain DML). PL/pgSQL function
// bodies are not translated — they're kept as comments with a warning.
internal sealed class PostgresToOther
{
    private readonly ConvertContext _ctx;
    private readonly DbEngine _to;
    private readonly List<string> _warn = new();

    public PostgresToOther(ConvertContext ctx, DbEngine to) { _ctx = ctx; _to = to; }

    private void Warn(string w) { if (!_warn.Contains(w)) _warn.Add(w); }

    private string Q(string name)
    {
        var n = _to == DbEngine.Oracle && _ctx.NameCase != NameCase.Preserve && _ctx.NameCase != NameCase.Lower ? name.ToUpperInvariant()
              : _ctx.NameCase == NameCase.Upper ? name.ToUpperInvariant() : _ctx.NameCase == NameCase.Lower ? name.ToLowerInvariant() : name;
        bool simple = IsSimpleIdent(n);
        return _to switch
        {
            DbEngine.SqlServer => simple ? n : "[" + n.Replace("]", "]]") + "]",
            DbEngine.MySql => simple ? n : "`" + n.Replace("`", "``") + "`",
            DbEngine.Oracle => simple && n == n.ToUpperInvariant() ? n : "\"" + n + "\"",
            _ => simple ? n : "\"" + n + "\"",
        };
    }

    public ConversionResult Convert(string source)
    {
        var sb = new StringBuilder();
        foreach (var st in SqlScriptSplitter.SplitDetailed(source, ScriptDialect.Postgres))
        {
            if (st.Kind == StatementKind.Meta) continue;
            if (st.Kind == StatementKind.CopyIn) { Warn("COPY ... FROM stdin data blocks skipped — use the Transfer tab to move data."); continue; }
            string c;
            try { c = ConvertStatement(st.Text); }
            catch (Exception ex) { Warn($"could not convert a statement ({ex.Message})."); c = "/* TODO(convert):\n" + st.Text.Replace("*/", "* /") + "\n*/"; }
            if (c.Length > 0) sb.AppendLine(c.TrimEnd()).AppendLine(_to == DbEngine.SqlServer ? "GO" : "");
        }
        var target = _to switch { DbEngine.SqlServer => "SQL Server", DbEngine.MySql => "MySQL", DbEngine.Sqlite => "SQLite", _ => "Oracle" };
        return new ConversionResult(SqlCodeConverter.Header("PostgreSQL", target, _warn).Replace("--", _to == DbEngine.MySql ? "-- " : "--") + "\n" + sb.ToString().TrimEnd() + "\n", _warn.Distinct().ToList());
    }

    private static readonly Regex PgDumpNoise = new(@"^(SET\s+\w+|SELECT\s+pg_catalog\.set_config|ALTER\s+.+\s+OWNER\s+TO|GRANT|REVOKE|COMMENT\s+ON\s+EXTENSION|CREATE\s+EXTENSION)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string ConvertStatement(string text)
    {
        var head = SqlScriptSplitter.StripLeadingComments(text);
        if (PgDumpNoise.IsMatch(head)) return "";
        if (Regex.IsMatch(head, @"^CREATE\s+(OR\s+REPLACE\s+)?(FUNCTION|PROCEDURE|TRIGGER|AGGREGATE)\b", RegexOptions.IgnoreCase))
        {
            Warn("PL/pgSQL functions/procedures/triggers are not auto-translated to other engines — kept as comments for manual rewrite.");
            return "/* TODO(convert): PostgreSQL routine\n" + text.Replace("*/", "* /") + "\n*/";
        }
        var t = SqlTokenizer.Tokenize(text, ScriptDialect.Postgres);
        Rewrite(t);
        var s = R(t).TrimEnd(';');
        if (_to == DbEngine.Oracle) return s + (Regex.IsMatch(s, @"^\s*(BEGIN|DECLARE)", RegexOptions.IgnoreCase) ? ";\n/" : ";");
        return s + ";";
    }

    private void Rewrite(List<Tok> t)
    {
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind == TokKind.QuotedIdent) { t[i] = new Tok(TokKind.Word, Q(x.Ident)); continue; }
            if (x.Kind == TokKind.Word && IsSimpleIdent(x.Text) && !Keywords.Contains(x.Text) && _to == DbEngine.Oracle && _ctx.NameCase is NameCase.TargetDefault or NameCase.Upper)
            {
                // leave unquoted — Oracle folds to upper itself
            }
            if (x.Kind == TokKind.Dollar)
            {
                var body = x.Text;
                var tagEnd = body.IndexOf('$', 1);
                var inner = body[(tagEnd + 1)..^(tagEnd + 1)];
                t[i] = new Tok(TokKind.String, "'" + inner.Replace("'", "''") + "'");
                continue;
            }
            // expr::type -> CAST(expr AS type)  (expr = previous primary)
            if (x.IsSym("::"))
            {
                int p = PrevSig(t, i), start = p;
                if (p >= 0 && t[p].IsSym(")"))
                {
                    int depth = 0;
                    for (int k = p; k >= 0; k--) { if (t[k].IsSym(")")) depth++; if (t[k].IsSym("(")) { depth--; if (depth == 0) { start = k; break; } } }
                    var fn = PrevSig(t, start); if (fn >= 0 && t[fn].Kind == TokKind.Word) start = fn;
                }
                else
                {
                    while (true) { var d = PrevSig(t, start); if (d >= 0 && t[d].IsSym(".")) { var q = PrevSig(t, d); if (q >= 0 && t[q].IsIdent) { start = q; continue; } } break; }
                }
                int tEnd = NextSig(t, i);
                if (tEnd < 0 || start < 0) continue;
                var nx = NextSig(t, tEnd);
                while (nx >= 0 && (t[nx].IsWordAny("precision", "varying", "without", "with", "time", "zone") || t[nx].IsSym("(") || t[nx].IsSym("[")))
                {
                    tEnd = t[nx].IsSym("(") ? MatchParen(t, nx) : t[nx].IsSym("[") ? NextSig(t, nx) : nx;
                    nx = NextSig(t, tEnd);
                }
                var exprText = R(Slice(t, start, p + 1));
                var typeToks = Slice(t, NextSig(t, i), tEnd + 1).Select(z => new Tok(z.Kind, z.Text)).ToList();
                MapTypes(typeToks);
                Replace(t, start, tEnd, $"CAST({exprText} AS {R(typeToks)})");
                i = start;
                continue;
            }
            if (x.Kind != TokKind.Word) continue;
            var up = x.Upper;
            var n = NextSig(t, i);
            bool call = n >= 0 && t[n].IsSym("(");

            if (up is "TRUE" or "FALSE" && _to is DbEngine.Oracle or DbEngine.SqlServer) { t[i] = new Tok(TokKind.Word, up == "TRUE" ? "1" : "0"); continue; }
            if (up == "ILIKE") { t[i] = new Tok(TokKind.Word, "LIKE"); Warn("ILIKE -> LIKE: wrap both sides in UPPER() for case-insensitive matching (collation-dependent)."); continue; }
            if (up == "LIMIT" && n >= 0) { RewriteLimit(t, i); continue; }
            if (up == "CURRENT_TIMESTAMP" || up == "LOCALTIMESTAMP")
            {
                t[i] = new Tok(TokKind.Word, _to switch { DbEngine.Oracle => "SYSTIMESTAMP", DbEngine.SqlServer => "SYSDATETIME()", DbEngine.MySql => "NOW(6)", _ => "CURRENT_TIMESTAMP" });
                continue;
            }
            if (!call) continue;
            var close = MatchParen(t, n);
            var args = SplitArgs(t, n, close);
            string A(int k) { var a = args[k].Select(z => new Tok(z.Kind, z.Text)).ToList(); Rewrite(a); return R(a); }
            string? rep = (up, _to) switch
            {
                ("NOW", DbEngine.Oracle) => "SYSTIMESTAMP",
                ("NOW", DbEngine.SqlServer) => "SYSDATETIME()",
                ("NOW", DbEngine.Sqlite) => "CURRENT_TIMESTAMP",
                ("GEN_RANDOM_UUID", DbEngine.Oracle) => "SYS_GUID()",
                ("GEN_RANDOM_UUID", DbEngine.SqlServer) => "NEWID()",
                ("GEN_RANDOM_UUID", DbEngine.MySql) => "UUID()",
                ("LENGTH", DbEngine.SqlServer) => $"LEN({A(0)})",
                ("COALESCE", DbEngine.Oracle) when args.Count == 2 => $"NVL({A(0)}, {A(1)})",
                ("STRING_AGG", DbEngine.Oracle) => StringAggOracle(args, A),
                ("STRING_AGG", DbEngine.MySql) => $"GROUP_CONCAT({A(0)} SEPARATOR {(args.Count > 1 ? A(1) : "','")})",
                ("POSITION", _) when args.Count == 1 && args[0].Any(z => z.IsWord("IN")) => PositionCall(args[0]),
                ("NEXTVAL", DbEngine.Oracle) => $"{A(0).Trim('\'')}.NEXTVAL",
                ("NEXTVAL", DbEngine.SqlServer) => $"NEXT VALUE FOR {A(0).Trim('\'')}",
                _ => null,
            };
            if (rep != null) Replace(t, i, close, rep);
        }
        MapTypes(t);
        if (_to == DbEngine.SqlServer)
            for (int i = 0; i < t.Count; i++) if (t[i].IsSym("||")) t[i] = new Tok(TokKind.Symbol, "+");
        if (_to == DbEngine.MySql && t.Any(z => z.IsSym("||"))) Warn("|| is logical OR in MySQL by default — use CONCAT() (or enable PIPES_AS_CONCAT).");
    }

    private string PositionCall(List<Tok> arg)
    {
        var inIdx = arg.FindIndex(z => z.IsWord("IN"));
        var a = R(arg.Take(inIdx).ToList()); var b = R(arg.Skip(inIdx + 1).ToList());
        return _to switch
        {
            DbEngine.Oracle => $"INSTR({b}, {a})",
            DbEngine.SqlServer => $"CHARINDEX({a}, {b})",
            DbEngine.Sqlite => $"instr({b}, {a})",
            _ => $"LOCATE({a}, {b})",
        };
    }

    private static string StringAggOracle(List<List<Tok>> args, Func<int, string> A)
    {
        var sep = args.Count > 1 ? A(1) : "','";
        var ob = Regex.Match(sep, @"^(.*?)\s+(ORDER\s+BY\s+.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return ob.Success ? $"LISTAGG({A(0)}, {ob.Groups[1].Value}) WITHIN GROUP ({ob.Groups[2].Value})" : $"LISTAGG({A(0)}, {sep}) WITHIN GROUP (ORDER BY NULL)";
    }

    private void RewriteLimit(List<Tok> t, int i)
    {
        var n = NextSig(t, i);
        var val = t[n].Text;
        var off = NextSig(t, n);
        string? offset = null; int end = n;
        if (off >= 0 && t[off].IsWord("OFFSET")) { offset = t[NextSig(t, off)].Text; end = NextSig(t, off); }
        switch (_to)
        {
            case DbEngine.Oracle:
                Replace(t, i, end, (offset != null ? $"OFFSET {offset} ROWS " : "") + $"FETCH NEXT {val} ROWS ONLY");
                break;
            case DbEngine.SqlServer:
                Replace(t, i, end, $"OFFSET {offset ?? "0"} ROWS FETCH NEXT {val} ROWS ONLY");
                Warn("LIMIT -> OFFSET/FETCH: SQL Server requires an ORDER BY for OFFSET/FETCH (or use TOP n).");
                break;
        }
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "select", "from", "where", "and", "or", "not", "null", "as", "on", "join" };

    private void MapTypes(List<Tok> t)
    {
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind != TokKind.Word) continue;
            var n = NextSig(t, i);
            string? rep = (x.Upper, _to) switch
            {
                ("TEXT", DbEngine.Oracle) => "CLOB",
                ("TEXT", DbEngine.SqlServer) => "nvarchar(max)",
                ("TEXT", DbEngine.MySql) => "longtext",
                ("VARCHAR", DbEngine.Oracle) => "VARCHAR2",
                ("VARCHAR", DbEngine.SqlServer) => "nvarchar",
                ("BOOLEAN", DbEngine.Oracle) => "NUMBER(1)",
                ("BOOLEAN", DbEngine.SqlServer) => "bit",
                ("BOOLEAN", DbEngine.MySql) => "tinyint(1)",
                ("BYTEA", DbEngine.Oracle) => "BLOB",
                ("BYTEA", DbEngine.SqlServer) => "varbinary(max)",
                ("BYTEA", DbEngine.MySql) => "longblob",
                ("BYTEA", DbEngine.Sqlite) => "BLOB",
                ("UUID", DbEngine.Oracle) => "VARCHAR2(36)",
                ("UUID", DbEngine.SqlServer) => "uniqueidentifier",
                ("UUID", DbEngine.MySql) => "char(36)",
                ("JSONB" or "JSON", DbEngine.Oracle) => "CLOB",
                ("JSONB" or "JSON", DbEngine.SqlServer) => "nvarchar(max)",
                ("JSONB", DbEngine.MySql) => "json",
                ("TIMESTAMPTZ", DbEngine.Oracle) => "TIMESTAMP WITH TIME ZONE",
                ("TIMESTAMPTZ", DbEngine.SqlServer) => "datetimeoffset",
                ("TIMESTAMPTZ", DbEngine.MySql) => "datetime(6)",
                ("TIMESTAMP", DbEngine.SqlServer) => "datetime2",
                ("TIMESTAMP", DbEngine.MySql) => "datetime(6)",
                ("INTEGER" or "INT4", DbEngine.Oracle) => "NUMBER(10)",
                ("BIGINT" or "INT8", DbEngine.Oracle) => "NUMBER(19)",
                ("SMALLINT" or "INT2", DbEngine.Oracle) => "NUMBER(5)",
                ("NUMERIC", DbEngine.Oracle) => "NUMBER",
                ("SERIAL", DbEngine.SqlServer) => "int IDENTITY(1,1)",
                ("BIGSERIAL", DbEngine.SqlServer) => "bigint IDENTITY(1,1)",
                ("SERIAL", DbEngine.MySql) => "int AUTO_INCREMENT",
                ("BIGSERIAL", DbEngine.MySql) => "bigint AUTO_INCREMENT",
                ("SERIAL" or "BIGSERIAL", DbEngine.Oracle) => "NUMBER(19) GENERATED BY DEFAULT AS IDENTITY",
                ("SERIAL" or "BIGSERIAL", DbEngine.Sqlite) => "INTEGER",
                ("DOUBLE", DbEngine.Oracle) when n >= 0 && t[n].IsWord("PRECISION") => "BINARY_DOUBLE",
                ("DOUBLE", DbEngine.SqlServer) when n >= 0 && t[n].IsWord("PRECISION") => "float",
                ("DOUBLE", DbEngine.MySql) when n >= 0 && t[n].IsWord("PRECISION") => "double",
                _ => null,
            };
            if (rep == null) continue;
            if (x.Upper == "DOUBLE") Replace(t, i, n, rep); else t[i] = new Tok(TokKind.Word, rep);
        }
    }
}
