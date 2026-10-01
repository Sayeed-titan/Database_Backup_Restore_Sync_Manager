using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PgBackupManager.Core.Providers;
using static PgBackupManager.Core.Sql.TokenOps;

namespace PgBackupManager.Core.Sql;

// Oracle SQL + PL/SQL -> PostgreSQL + PL/pgSQL.
//
// Packages become one PG schema per package (so pkg.proc(...) call sites keep
// working) or targetSchema.pkg_proc, standalone routines become PL/pgSQL
// functions/procedures, triggers are split into a trigger function + CREATE
// TRIGGER, sequences/views/tables/indexes/object types are rewritten, and
// built-ins (NVL, DECODE, SYSDATE, TO_DATE, ADD_MONTHS, LISTAGG, seq.NEXTVAL,
// RAISE_APPLICATION_ERROR, DBMS_OUTPUT, cursor attributes...) are mapped.
internal sealed class OracleToPostgres
{
    private readonly ConvertContext _ctx;
    private readonly List<string> _warn = new();
    private readonly HashSet<string> _procs;
    private readonly bool _lower;

    // PL/SQL collection types (TABLE OF / VARRAY) -> "elem[]", and variables of those types.
    private readonly Dictionary<string, string> _collTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collVars = new(StringComparer.OrdinalIgnoreCase);

    public OracleToPostgres(ConvertContext ctx)
    {
        _ctx = ctx;
        _procs = new HashSet<string>(ctx.KnownProcedures, StringComparer.OrdinalIgnoreCase);
        _lower = ctx.NameCase is NameCase.TargetDefault or NameCase.Lower;
    }

    private string TargetSchema => string.IsNullOrWhiteSpace(_ctx.TargetSchema) ? "" : Id(_ctx.TargetSchema!);

    public ConversionResult Convert(string source)
    {
        var units = SqlScriptSplitter.SplitDetailed(source, ScriptDialect.Oracle).Where(s => s.Kind == StatementKind.Sql).Select(s => s.Text).ToList();
        foreach (var u in units) PreScanProcedures(u);

        var sb = new StringBuilder();
        foreach (var u in units)
        {
            string converted;
            try { converted = ConvertUnit(u); }
            catch (Exception ex)
            {
                Warn($"could not convert a statement ({ex.Message}) — original kept as a comment.");
                converted = "/* TODO(convert): conversion failed, original Oracle text:\n" + u.Replace("*/", "* /") + "\n*/";
            }
            sb.AppendLine(converted.TrimEnd()).AppendLine();
        }
        Warn("Oracle treats '' (empty string) as NULL; PostgreSQL does not — check comparisons like x = '' / x IS NULL.");
        return new ConversionResult(SqlCodeConverter.Header("Oracle", "PostgreSQL", _warn) + "\n" + sb.ToString().TrimEnd() + "\n", _warn.Distinct().ToList());
    }

    private void Warn(string w) { if (!_warn.Contains(w)) _warn.Add(w); }

    // ------------------------------------------------------------------ names

    private string Id(string name)
    {
        var n = _ctx.NameCase switch
        {
            NameCase.Upper => name.ToUpperInvariant(),
            NameCase.Preserve => name,
            _ => name.ToLowerInvariant(),
        };
        return IsSimpleIdent(n) && n == n.ToLowerInvariant() && !PgReserved.Contains(n) ? n : "\"" + n.Replace("\"", "\"\"") + "\"";
    }

    private static readonly HashSet<string> PgReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "all","analyse","analyze","and","any","array","as","asc","asymmetric","both","case","cast","check","collate","column","constraint","create",
        "current_catalog","current_date","current_role","current_time","current_timestamp","current_user","default","deferrable","desc","distinct","do",
        "else","end","except","false","fetch","for","foreign","from","grant","group","having","in","initially","intersect","into","lateral","leading",
        "limit","localtime","localtimestamp","not","null","offset","on","only","or","order","placing","primary","references","returning","select",
        "session_user","some","symmetric","table","then","to","trailing","true","union","unique","user","using","variadic","when","where","window","with",
    };

    // Quoted "ABC" -> abc (when it's an ordinary upper-case Oracle name) and
    // SOURCE_SCHEMA. -> target schema. Unquoted identifiers are left alone —
    // PostgreSQL folds them to lower case anyway.
    private void NormalizeIdents(List<Tok> t)
    {
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.Kind == TokKind.QuotedIdent && _lower)
            {
                var v = x.Ident;
                if (IsSimpleIdent(v) && v == v.ToUpperInvariant())
                    t[i] = new Tok(TokKind.Word, Id(v));
            }
        }
        if (string.IsNullOrEmpty(_ctx.SourceSchema)) return;
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (!x.IsIdent || !string.Equals(x.Ident, _ctx.SourceSchema, StringComparison.OrdinalIgnoreCase)) continue;
            var dot = NextSig(t, i);
            if (dot < 0 || !t[dot].IsSym(".")) continue;
            var p = PrevSig(t, i);
            if (p >= 0 && t[p].IsSym(".")) continue;
            // SCHEMA.PKG.PROC -> PKG.PROC (the package itself becomes the PG schema)
            var mid = NextSig(t, dot);
            var dot2 = mid >= 0 ? NextSig(t, mid) : -1;
            if (_ctx.PackageAsSchema && mid >= 0 && t[mid].IsIdent && dot2 >= 0 && t[dot2].IsSym("."))
            {
                t.RemoveRange(i, dot - i + 1);
                continue;
            }
            t[i] = new Tok(TokKind.Word, string.IsNullOrEmpty(_ctx.TargetSchema) ? Id(x.Ident) : TargetSchema);
        }
    }

    // --------------------------------------------------------------- dispatch

    private void PreScanProcedures(string unit)
    {
        var t = SqlTokenizer.Tokenize(unit, ScriptDialect.Oracle).Where(x => !x.IsTrivia).ToList();
        string? pkg = null;
        for (int i = 0; i + 1 < t.Count; i++)
        {
            if (t[i].IsWord("PACKAGE") && t[i + 1].IsWord("BODY") && i + 2 < t.Count) pkg = LastIdent(t, i + 2);
            if (t[i].IsWord("PROCEDURE") && t[i + 1].IsIdent)
            {
                var name = LastIdent(t, i + 1);
                _procs.Add(name);
                if (pkg != null) _procs.Add(pkg + "." + name);
            }
        }
    }

    private static string LastIdent(List<Tok> sig, int i)
    {
        var name = sig[i].Ident;
        while (i + 2 < sig.Count && sig[i + 1].IsSym(".") && sig[i + 2].IsIdent) { name = sig[i + 2].Ident; i += 2; }
        return name.ToUpperInvariant();
    }

    private string ConvertUnit(string unit)
    {
        var t = SqlTokenizer.Tokenize(unit.Trim().TrimEnd('/').Trim(), ScriptDialect.Oracle);
        NormalizeIdents(t);
        int i = FirstSig(t);
        if (i < 0) return "";

        if (t[i].IsWordAny("DECLARE", "BEGIN")) return ConvertAnonymousBlock(t, i);
        if (!t[i].IsWord("CREATE")) return ConvertPlainSql(t);

        int k = NextSig(t, i);
        while (k >= 0 && t[k].IsWordAny("OR", "REPLACE", "EDITIONABLE", "NONEDITIONABLE", "EDITIONING", "FORCE", "NOFORCE", "NO", "PUBLIC", "GLOBAL", "TEMPORARY", "UNIQUE", "BITMAP", "MATERIALIZED"))
        {
            if (t[k].IsWord("NO") && !(NextSig(t, k) is var nf and >= 0 && t[nf].IsWord("FORCE"))) break;
            k = NextSig(t, k);
        }
        if (k < 0) return ConvertPlainSql(t);
        var kind = t[k].Upper;
        var headWords = Slice(t, i, k).Where(x => x.Kind == TokKind.Word).Select(x => x.Upper).ToList();

        switch (kind)
        {
            case "FUNCTION":
            case "PROCEDURE":
                return ConvertStandaloneRoutine(t, k);
            case "PACKAGE":
                var nb = NextSig(t, k);
                return nb >= 0 && t[nb].IsWord("BODY") ? ConvertPackageBody(t, nb) : ConvertPackageSpec(t, k);
            case "TRIGGER":
                return ConvertTrigger(t, k);
            case "SEQUENCE":
                return ConvertSequence(t, k);
            case "VIEW":
                if (headWords.Contains("MATERIALIZED")) return ConvertMaterializedView(t, k);
                return ConvertView(t, k);
            case "TABLE":
                return ConvertTable(t, k, headWords.Contains("TEMPORARY"));
            case "INDEX":
                return ConvertIndex(t, k, headWords.Contains("UNIQUE"), headWords.Contains("BITMAP"));
            case "TYPE":
                return ConvertType(t, k);
            case "SYNONYM":
                Warn("synonyms have no PostgreSQL equivalent — use search_path or a view; left as a comment.");
                return "-- TODO(convert): " + R(t).Replace("\n", "\n-- ");
            default:
                return ConvertPlainSql(t);
        }
    }

    // ----------------------------------------------------------- expressions

    private static readonly Dictionary<string, string> ExceptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DUP_VAL_ON_INDEX"] = "unique_violation",
        ["ZERO_DIVIDE"] = "division_by_zero",
        ["INVALID_NUMBER"] = "invalid_text_representation",
        ["VALUE_ERROR"] = "data_exception",
        ["INVALID_CURSOR"] = "invalid_cursor_state",
        ["CURSOR_ALREADY_OPEN"] = "duplicate_cursor",
        ["TIMEOUT_ON_RESOURCE"] = "lock_not_available",
        ["LOGIN_DENIED"] = "invalid_authorization_specification",
        ["STORAGE_ERROR"] = "out_of_memory",
    };

    private static readonly Dictionary<string, string> TruncUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["YYYY"] = "year", ["YEAR"] = "year", ["YYY"] = "year", ["YY"] = "year", ["Y"] = "year", ["SYYYY"] = "year", ["SYEAR"] = "year",
        ["Q"] = "quarter", ["MM"] = "month", ["MON"] = "month", ["MONTH"] = "month", ["RM"] = "month",
        ["WW"] = "week", ["IW"] = "week", ["W"] = "week",
        ["DD"] = "day", ["DDD"] = "day", ["J"] = "day",
        ["HH"] = "hour", ["HH12"] = "hour", ["HH24"] = "hour", ["MI"] = "minute",
    };

    // Rewrites built-in functions/keywords in place. Call arguments are
    // converted recursively before being re-assembled.
    private void Expr(List<Tok> t)
    {
        // Query-shape rewrites first; they re-tokenize, so the conversions below still apply inside them.
        if (t.Any(x => x.IsSym("(+)") || x.IsWord("CONNECT")))
        {
            var r = OracleStructural.ConnectBy(OracleStructural.OuterJoins(t, Warn), Warn);
            if (!ReferenceEquals(r, t)) { t.Clear(); t.AddRange(r); }
        }
        MapTypes(t);
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];

            if (x.Kind == TokKind.String && (x.Text.StartsWith("q'") || x.Text.StartsWith("Q'")) && x.Text.Length >= 5)
            {
                var inner = x.Text[3..^2];
                t[i] = new Tok(TokKind.String, "'" + inner.Replace("'", "''") + "'");
                continue;
            }
            if (x.Kind == TokKind.String && (x.Text.StartsWith("N'") || x.Text.StartsWith("n'")))
            {
                t[i] = new Tok(TokKind.String, x.Text[1..]);
                continue;
            }
            if (x.IsSym("(+)")) { Warn("Oracle (+) outer-join syntax must be rewritten as ANSI LEFT/RIGHT JOIN (left in place — it will not compile)."); continue; }
            if (x.Kind == TokKind.Symbol && x.Text == "%" && i + 1 < t.Count)
            {
                var nx = NextSig(t, i); var pv = PrevSig(t, i);
                if (nx >= 0 && pv >= 0)
                {
                    var attr = t[nx].Upper;
                    var isSql = t[pv].IsWord("SQL");
                    if (attr == "NOTFOUND") { Replace(t, pv, nx, "NOT FOUND"); i = pv; continue; }
                    if (attr == "FOUND") { Replace(t, pv, nx, "FOUND"); i = pv; continue; }
                    if (attr == "ROWCOUNT" && isSql) { Warn("SQL%ROWCOUNT: use GET DIAGNOSTICS v_count = ROW_COUNT; after the statement."); continue; }
                    if (attr == "ISOPEN") { Warn("cursor%ISOPEN has no PL/pgSQL equivalent — track open state in a variable."); continue; }
                }
                continue;
            }
            if (x.Kind != TokKind.Word) continue;

            var up = x.Upper;
            var n = NextSig(t, i);
            var p = PrevSig(t, i);
            bool call = n >= 0 && t[n].IsSym("(");
            bool afterDot = p >= 0 && t[p].IsSym(".");

            if (ExceptionNames.TryGetValue(up, out var pgEx) && !afterDot && !call) { t[i] = new Tok(TokKind.Word, pgEx); continue; }

            switch (up)
            {
                case "SYSDATE" when !call && !afterDot: t[i] = new Tok(TokKind.Word, "LOCALTIMESTAMP(0)"); continue;
                case "SYSTIMESTAMP" when !call && !afterDot: t[i] = new Tok(TokKind.Word, "CURRENT_TIMESTAMP"); continue;
                case "USER" when !call && !afterDot && !(n >= 0 && t[n].IsSym(".")): t[i] = new Tok(TokKind.Word, "CURRENT_USER"); continue;
                case "SQLCODE" when !call: t[i] = new Tok(TokKind.Word, "SQLSTATE"); continue;
                case "MINUS" when !call: t[i] = new Tok(TokKind.Word, "EXCEPT"); continue;
                case "ROWNUM" when !afterDot: Warn("ROWNUM has no direct equivalent — use LIMIT n or row_number() OVER ()."); continue;
                case "ROWID" when !afterDot: Warn("ROWID -> PostgreSQL ctid is not stable across updates; use the primary key."); continue;
                case "CONNECT" when n >= 0 && t[n].IsWord("BY"): Warn("CONNECT BY hierarchical queries must be rewritten with WITH RECURSIVE."); continue;
                case "IMMEDIATE" when p >= 0 && t[p].IsWord("EXECUTE"): t.RemoveRange(p + 1, i - p); i = p; continue;
                case "NEXTVAL" or "CURRVAL" when afterDot:
                    {
                        // [schema.]seq.NEXTVAL -> nextval('schema.seq')
                        var start = FirstIdentOfChain(t, p);
                        if (start < 0) continue;
                        var parts = Slice(t, start, p).Where(z => z.IsIdent).Select(z => z.Kind == TokKind.QuotedIdent ? z.Text : z.Text.ToLowerInvariant()).ToList();
                        if (parts.Count == 0) continue;
                        Replace(t, start, i, $"{up.ToLowerInvariant()}('{string.Join(".", parts).Replace("'", "''")}')");
                        i = start;
                        continue;
                    }                case "DUAL" when p >= 0 && t[p].IsWord("FROM"):
                    t.RemoveRange(p, i - p + 1);
                    i = p - 1;
                    continue;
            }

            if (!call) continue;
            int open = n, close = MatchParen(t, open);
            if (close < 0) continue;
            var args = SplitArgs(t, open, close);
            string A(int k) { var a = args[k].Select(z => new Tok(z.Kind, z.Text)).ToList(); Expr(a); return R(a); }

            string? rep = up switch
            {
                "NVL" when !afterDot => $"COALESCE({string.Join(", ", args.Select((_, k) => A(k)))})",
                "NVL2" when args.Count == 3 && !afterDot => $"CASE WHEN {A(0)} IS NOT NULL THEN {A(1)} ELSE {A(2)} END",
                "DECODE" when args.Count >= 3 && !afterDot => Decode(args.Count, A),
                "TO_NUMBER" when args.Count == 1 => $"CAST({A(0)} AS numeric)",
                "TO_CHAR" when args.Count == 1 => $"CAST({A(0)} AS text)",
                "TO_DATE" when args.Count == 1 => $"CAST({A(0)} AS timestamp)",
                "TO_DATE" when args.Count >= 2 => Regex.IsMatch(A(1), "HH|MI|SS", RegexOptions.IgnoreCase) ? $"to_timestamp({A(0)}, {A(1)})" : $"to_date({A(0)}, {A(1)})",
                "INSTR" when args.Count == 2 => $"POSITION({A(1)} IN {A(0)})",
                "ADD_MONTHS" when args.Count == 2 => $"({A(0)} + ({A(1)}) * INTERVAL '1 month')",
                "LAST_DAY" when args.Count == 1 => $"CAST(date_trunc('month', {A(0)}) + INTERVAL '1 month' - INTERVAL '1 day' AS date)",
                "MONTHS_BETWEEN" when args.Count == 2 => $"(EXTRACT(YEAR FROM age({A(0)}, {A(1)})) * 12 + EXTRACT(MONTH FROM age({A(0)}, {A(1)})))",
                "TRUNC" when args.Count == 2 && args[1].Any(z => z.Kind == TokKind.String) && TruncUnits.TryGetValue(A(1).Trim('\''), out var unit)
                    => $"date_trunc('{unit}', {A(0)})",
                "SYS_GUID" when args.Count <= 1 => "gen_random_uuid()",
                "LENGTHB" => $"octet_length({A(0)})",
                "BITAND" when args.Count == 2 => $"({A(0)} & {A(1)})",
                "REPLACE" when args.Count == 2 => $"replace({A(0)}, {A(1)}, '')",
                "REGEXP_REPLACE" when args.Count == 3 => $"regexp_replace({A(0)}, {A(1)}, {A(2)}, 'g')",
                "EMPTY_CLOB" => "''",
                "EMPTY_BLOB" => "''::bytea",
                "LISTAGG" => Listagg(t, close, args.Count, A, out close),
                _ => null,
            };
            if (up == "MONTHS_BETWEEN") Warn("MONTHS_BETWEEN converted to whole months via age(); Oracle returns fractional months.");
            if (up is "SYS_CONTEXT") Warn("SYS_CONTEXT(...) has no direct equivalent — use current_setting()/current_user.");
            if (up.StartsWith("DBMS_") || (afterDot && p > 0 && PrevSig(t, p) is var pk and >= 0 && t[pk].Upper.StartsWith("DBMS_") && !t[pk].IsWord("DBMS_OUTPUT")))
                Warn($"Oracle supplied package call ({(afterDot ? t[PrevSig(t, p)].Upper + "." : "")}{up}) needs a manual replacement (orafce extension covers some).");

            if (afterDot && up == "PUT_LINE" && PrevSig(t, p) is var dp and >= 0 && t[dp].IsWord("DBMS_OUTPUT"))
            {
                Replace(t, dp, close, args.Count > 0 ? $"RAISE NOTICE '%', {A(0)}" : "RAISE NOTICE ''");
                i = dp;
                continue;
            }
            if (up == "RAISE_APPLICATION_ERROR" && args.Count >= 2)
            {
                var code = A(0).Replace(" ", "");
                var hint = Regex.IsMatch(code, @"^-?\d+$") ? $", HINT = 'ORA{code}'" : "";
                Replace(t, i, close, $"RAISE EXCEPTION USING MESSAGE = {A(1)}, ERRCODE = 'P0001'{hint}");
                continue;
            }
            if (rep != null) Replace(t, i, close, rep);
        }
    }

    private static int FirstIdentOfChain(List<Tok> t, int dotBeforeAttr)
    {
        int s = PrevSig(t, dotBeforeAttr);
        while (s >= 0)
        {
            var d = PrevSig(t, s);
            if (d >= 0 && t[d].IsSym("."))
            {
                var q = PrevSig(t, d);
                if (q >= 0 && t[q].IsIdent) { s = q; continue; }
            }
            break;
        }
        return s;
    }

    private string Decode(int count, Func<int, string> A)
    {
        var sb = new StringBuilder("CASE");
        var subject = A(0);
        int k = 1;
        for (; k + 1 < count; k += 2)
        {
            var search = A(k);
            sb.Append(search.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                ? $" WHEN {subject} IS NULL THEN {A(k + 1)}"
                : $" WHEN {subject} = {search} THEN {A(k + 1)}");
        }
        if (k < count) sb.Append($" ELSE {A(k)}");
        return sb.Append(" END").ToString();
    }

    // LISTAGG(x, sep) WITHIN GROUP (ORDER BY y) -> string_agg(x, sep ORDER BY y)
    private string Listagg(List<Tok> t, int close, int argc, Func<int, string> A, out int newClose)
    {
        newClose = close;
        var w = NextSig(t, close);
        string order = "";
        if (w >= 0 && t[w].IsWord("WITHIN"))
        {
            var g = NextSig(t, w);
            var o = g >= 0 ? NextSig(t, g) : -1;
            if (o >= 0 && t[o].IsSym("("))
            {
                var oc = MatchParen(t, o);
                var inner = Slice(t, o + 1, oc).Select(z => new Tok(z.Kind, z.Text)).ToList();
                Expr(inner);
                order = " " + R(inner);
                newClose = oc;
            }
        }
        return $"string_agg({A(0)}, {(argc > 1 ? A(1) : "''")}{order})";
    }

    // ----------------------------------------------------------------- types

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
            switch (x.Upper)
            {
                case "VARCHAR2":
                case "NVARCHAR2":
                case "VARCHAR":
                    t[i] = new Tok(TokKind.Word, "varchar");
                    if (paren) StripCharByte(t, n);
                    break;
                case "NCHAR":
                case "CHAR" when paren:
                    t[i] = new Tok(TokKind.Word, "char");
                    if (paren) StripCharByte(t, n);
                    break;
                case "NUMBER":
                    if (paren)
                    {
                        var c = MatchParen(t, n);
                        var a = SplitArgs(t, n, c).Select(R).ToList();
                        int? prec = a.Count > 0 && int.TryParse(a[0], out var pp) ? pp : null;
                        int scale = a.Count > 1 && int.TryParse(a[1], out var ss) ? ss : 0;
                        var mapped = prec is > 0 && scale == 0 ? prec <= 4 ? "smallint" : prec <= 9 ? "integer" : prec <= 18 ? "bigint" : $"numeric({prec})"
                                   : prec is > 0 ? $"numeric({prec},{scale})"
                                   : a.Count > 1 ? $"numeric(38,{scale})" : "numeric";
                        Replace(t, i, c, mapped);
                    }
                    else t[i] = new Tok(TokKind.Word, "numeric");
                    break;
                case "FLOAT":
                    if (paren) Replace(t, i, MatchParen(t, n), "double precision"); else t[i] = new Tok(TokKind.Word, "double precision");
                    break;
                case "BINARY_FLOAT": t[i] = new Tok(TokKind.Word, "real"); break;
                case "BINARY_DOUBLE": t[i] = new Tok(TokKind.Word, "double precision"); break;
                case "PLS_INTEGER" or "BINARY_INTEGER" or "SIMPLE_INTEGER" or "NATURAL" or "NATURALN" or "POSITIVE" or "POSITIVEN" or "SIGNTYPE":
                    t[i] = new Tok(TokKind.Word, "integer"); break;
                case "CLOB" or "NCLOB": t[i] = new Tok(TokKind.Word, "text"); break;
                case "BLOB": t[i] = new Tok(TokKind.Word, "bytea"); break;
                case "LONG":
                    if (n >= 0 && t[n].IsWord("RAW")) { Replace(t, i, n, "bytea"); }
                    else t[i] = new Tok(TokKind.Word, "text");
                    break;
                case "RAW":
                    if (paren) Replace(t, i, MatchParen(t, n), "bytea"); else t[i] = new Tok(TokKind.Word, "bytea");
                    break;
                case "DATE" when !paren && !(n >= 0 && t[n].Kind == TokKind.String):
                    t[i] = new Tok(TokKind.Word, "timestamp(0)"); break;
                case "XMLTYPE": t[i] = new Tok(TokKind.Word, "xml"); break;
                case "SYS_REFCURSOR": t[i] = new Tok(TokKind.Word, "refcursor"); break;
                case "ROWID" or "UROWID" when n >= 0 && !t[n].IsSym(","): break;
                case "BFILE": t[i] = new Tok(TokKind.Word, "text"); Warn("BFILE (external file LOB) mapped to text path — contents are not migrated."); break;
                case "TIMESTAMP":
                    {
                        // TIMESTAMP(n) WITH LOCAL TIME ZONE -> timestamptz
                        int j = paren ? NextSig(t, MatchParen(t, n)) : n;
                        if (j >= 0 && t[j].IsWord("WITH"))
                        {
                            var l = NextSig(t, j);
                            if (l >= 0 && t[l].IsWord("LOCAL"))
                            {
                                var end = NextSig(t, NextSig(t, NextSig(t, l)));
                                if (end >= 0) Replace(t, i, end, "timestamptz");
                            }
                        }
                        break;
                    }
                case "INTERVAL" when n >= 0 && t[n].IsWordAny("DAY", "YEAR"):
                    {
                        // INTERVAL DAY(2) TO SECOND(6) -> interval
                        int j = i, guard = 0;
                        while (j < t.Count && guard++ < 16)
                        {
                            var nx = NextSig(t, j);
                            if (nx < 0) break;
                            if (t[nx].IsWordAny("DAY", "YEAR", "TO", "SECOND", "MONTH")) { j = nx; continue; }
                            if (t[nx].IsSym("(")) { j = MatchParen(t, nx); continue; }
                            break;
                        }
                        Replace(t, i, j, "interval");
                        break;
                    }
            }
        }
    }

    private static void StripCharByte(List<Tok> t, int open)
    {
        var close = MatchParen(t, open);
        for (int k = close - 1; k > open; k--)
            if (t[k].IsWordAny("CHAR", "BYTE"))
            {
                t.RemoveAt(k);
                if (k - 1 > open && t[k - 1].Kind == TokKind.Space) t.RemoveAt(k - 1);
            }
    }

    // ---------------------------------------------------------- plain / blocks

    private string ConvertPlainSql(List<Tok> t)
    {
        Expr(t);
        var s = R(t).TrimEnd(';');
        return s.Length == 0 ? "" : s + ";";
    }

    private string ConvertAnonymousBlock(List<Tok> t, int start)
    {
        string decls = "";
        int begin = start;
        if (t[start].IsWord("DECLARE"))
        {
            begin = FindTopLevelBegin(t, NextSig(t, start));
            decls = ConvertDeclarations(Slice(t, start + 1, begin), out var extra) + extra;
        }
        var end = MatchBlockEnd(t, begin);
        var body = ConvertBody(Slice(t, begin + 1, end), isFunction: false, out var loopRecords);
        return $"DO $body$\n{DeclareBlock(decls, loopRecords)}BEGIN\n{Indent(body)}\nEND\n$body$;";
    }

    // ------------------------------------------------------------- routines

    private sealed class ParsedRoutine
    {
        public string Kind = "";
        public List<Tok> Name = new();
        public List<List<Tok>> Params = new();
        public List<Tok> Between = new();   // RETURN type, AUTHID, DETERMINISTIC, PIPELINED ...
        public List<Tok> Decls = new();
        public List<Tok> Body = new();
        public bool IsForward;
        public bool IsExternal;
        public int End;                    // index of the terminating ';'
        public string SimpleName => Name.Where(z => z.IsIdent).Select(z => z.Ident).LastOrDefault() ?? "";
    }

    private ParsedRoutine ParseRoutine(List<Tok> t, int k)
    {
        var r = new ParsedRoutine { Kind = t[k].Upper };
        int i = NextSig(t, k);
        while (i >= 0 && (t[i].IsIdent || t[i].IsSym(".")))
        {
            if (t[i].IsIdent && !t[i].IsSym(".") && r.Name.Count > 0 && !r.Name[^1].IsSym(".")) break;
            r.Name.Add(t[i]);
            i = NextSig(t, i);
        }
        if (i >= 0 && t[i].IsSym("("))
        {
            var c = MatchParen(t, i);
            r.Params = SplitArgs(t, i, c).Where(a => a.Any(z => !z.IsTrivia)).ToList();
            i = NextSig(t, c);
        }
        int depth = 0;
        while (i >= 0)
        {
            if (t[i].IsSym("(")) depth++;
            else if (t[i].IsSym(")")) depth--;
            else if (depth == 0 && t[i].IsSym(";")) { r.IsForward = true; r.End = i; return r; }
            else if (depth == 0 && t[i].IsWordAny("IS", "AS")) break;
            r.Between.Add(t[i]);
            i = NextSig(t, i);
        }
        if (i < 0) throw new FormatException($"{r.Kind} {R(r.Name)}: missing IS/AS");

        int declStart = i + 1;
        var afterIs = NextSig(t, i);
        if (afterIs >= 0 && t[afterIs].IsWordAny("LANGUAGE", "EXTERNAL"))
        {
            r.IsExternal = true;
            int e = afterIs; while (e < t.Count && !t[e].IsSym(";")) e++;
            r.End = Math.Min(e, t.Count - 1);
            return r;
        }
        var begin = FindTopLevelBegin(t, afterIs);
        if (begin < 0) throw new FormatException($"{r.Kind} {R(r.Name)}: no BEGIN found");
        r.Decls = Slice(t, declStart, begin);
        var end = MatchBlockEnd(t, begin);
        if (end < 0) throw new FormatException($"{r.Kind} {R(r.Name)}: unbalanced BEGIN/END");
        r.Body = Slice(t, begin + 1, end);
        int semi = NextSig(t, end);
        if (semi >= 0 && t[semi].IsIdent) semi = NextSig(t, semi);
        r.End = semi >= 0 && t[semi].IsSym(";") ? semi : end;
        return r;
    }

    // Walks a declaration section (skipping nested subprogram definitions
    // whole) and returns the index of the BEGIN that starts its body.
    private int FindTopLevelBegin(List<Tok> t, int from)
    {
        int i = from;
        while (i >= 0 && i < t.Count)
        {
            if (t[i].IsWord("BEGIN")) return i;
            if (t[i].IsWordAny("FUNCTION", "PROCEDURE"))
            {
                var nested = ParseRoutine(t, i);
                i = NextSig(t, nested.End);
                continue;
            }
            // skip one declaration item up to its ';' (outside parentheses)
            int depth = 0;
            while (i < t.Count)
            {
                if (t[i].IsSym("(")) depth++;
                else if (t[i].IsSym(")")) depth--;
                else if (depth == 0 && t[i].IsSym(";")) break;
                else if (depth == 0 && t[i].IsWord("BEGIN")) return i;
                i++;
            }
            i = NextSig(t, i);
        }
        return -1;
    }

    private string ConvertStandaloneRoutine(List<Tok> t, int k)
    {
        var r = ParseRoutine(t, k);
        var name = QualifiedRoutineName(r.Name, pkgSchema: null);
        return EmitRoutine(r, name);
    }

    private string QualifiedRoutineName(List<Tok> nameToks, string? pkgSchema)
    {
        var parts = nameToks.Where(z => z.IsIdent).Select(z => z.Kind == TokKind.QuotedIdent ? z.Text : Id(z.Ident)).ToList();
        var simple = parts.Count > 0 ? parts[^1] : "unnamed";
        if (pkgSchema != null)
            return _ctx.PackageAsSchema ? $"{pkgSchema}.{simple}" : $"{(TargetSchema.Length > 0 ? TargetSchema + "." : "")}{Id(pkgSchema.Trim('"') + "_" + simple.Trim('"'))}";
        if (parts.Count > 1) return string.Join(".", parts);
        return TargetSchema.Length > 0 ? $"{TargetSchema}.{simple}" : simple;
    }

    private string EmitRoutine(ParsedRoutine r, string qualifiedName, string? pkgName = null)
    {
        if (r.IsExternal)
        {
            Warn($"{qualifiedName}: external (C/Java) routine can't be converted.");
            return $"-- TODO(convert): external routine {qualifiedName} skipped";
        }
        var isFunc = r.Kind == "FUNCTION";
        var paramSql = string.Join(", ", r.Params.Select(ConvertParam));

        string returns = "";
        if (isFunc)
        {
            var bt = r.Between.Select(z => new Tok(z.Kind, z.Text)).ToList();
            int ri = bt.FindIndex(z => z.IsWord("RETURN"));
            var typeToks = new List<Tok>();
            for (int j = ri + 1; j >= 1 && j < bt.Count; j++)
            {
                if (bt[j].IsWordAny("PIPELINED", "DETERMINISTIC", "RESULT_CACHE", "PARALLEL_ENABLE", "AUTHID", "ACCESSIBLE", "AGGREGATE", "USING")) break;
                typeToks.Add(bt[j]);
            }
            MapTypes(typeToks);
            returns = R(typeToks);
            if (returns.Length == 0) returns = "void";
            if (bt.Any(z => z.IsWord("PIPELINED"))) Warn($"{qualifiedName}: PIPELINED function — rewrite as RETURNS SETOF ... with RETURN NEXT/RETURN QUERY.");
            if (r.Params.Any(p => p.Any(z => z.IsWord("OUT")))) Warn($"{qualifiedName}: function with OUT parameters — PostgreSQL returns OUT params as the result; check RETURNS.");
        }
        else if (r.Params.Any(p => p.Any(z => z.IsWord("OUT"))))
        {
            Warn($"{qualifiedName}: procedure OUT parameters need PostgreSQL 14+ (older versions: INOUT only).");
        }
        var immutable = r.Between.Any(z => z.IsWord("DETERMINISTIC")) ? "\nIMMUTABLE" : "";

        var decls = ConvertDeclarations(r.Decls, out var extraDecls);
        var body = ConvertBody(r.Body, isFunc, out var loopRecords);

        var sb = new StringBuilder();
        sb.Append(isFunc ? "CREATE OR REPLACE FUNCTION " : "CREATE OR REPLACE PROCEDURE ").Append(qualifiedName).Append('(').Append(paramSql).AppendLine(")");
        if (isFunc) sb.Append("RETURNS ").AppendLine(returns);
        sb.Append("LANGUAGE plpgsql").AppendLine(immutable);
        sb.AppendLine("AS $body$");
        sb.Append(DeclareBlock(decls + extraDecls, loopRecords));
        sb.AppendLine("BEGIN");
        sb.AppendLine(Indent(body));
        sb.AppendLine("END;");
        sb.Append("$body$;");
        return sb.ToString();
    }

    private string ConvertParam(List<Tok> p)
    {
        var toks = p.Where(z => !z.IsTrivia).Select(z => new Tok(z.Kind, z.Text)).ToList();
        if (toks.Count == 0) return "";
        var name = toks[0].Kind == TokKind.QuotedIdent ? toks[0].Text : Id(toks[0].Ident);
        int i = 1;
        string mode = "";
        if (i < toks.Count && toks[i].IsWord("IN")) { i++; if (i < toks.Count && toks[i].IsWord("OUT")) { mode = "INOUT "; i++; } }
        else if (i < toks.Count && toks[i].IsWord("OUT")) { mode = "OUT "; i++; }
        if (i < toks.Count && toks[i].IsWord("NOCOPY")) i++;
        var typeToks = new List<Tok>();
        List<Tok>? def = null;
        for (; i < toks.Count; i++)
        {
            if (toks[i].IsWord("DEFAULT") || toks[i].IsSym(":=")) { def = toks.Skip(i + 1).ToList(); break; }
            typeToks.Add(toks[i]);
        }
        var typeList = SpaceOut(typeToks);
        MapTypes(typeList);
        var type = R(typeList);
        if (typeToks.Count == 1 && _collTypes.TryGetValue(typeToks[0].Ident, out var arr)) { type = arr; _collVars.Add(toks[0].Ident); }
        var s = $"{mode}{name} {type}";
        if (def != null) { var d = SpaceOut(def); Expr(d); s += " DEFAULT " + R(d); }
        return s;
    }

    // Re-inserts single spaces between significant tokens (after trivia was dropped).
    private static List<Tok> SpaceOut(List<Tok> sig)
    {
        var list = new List<Tok>();
        for (int i = 0; i < sig.Count; i++)
        {
            if (i > 0 && !(sig[i].IsSym("(") || sig[i].IsSym(")") || sig[i].IsSym(",") || sig[i].IsSym(".") || sig[i - 1].IsSym("(") || sig[i - 1].IsSym(".") || sig[i].IsSym("%") || sig[i - 1].IsSym("%")))
                list.Add(new Tok(TokKind.Space, " "));
            list.Add(sig[i]);
        }
        return list;
    }

    private static string DeclareBlock(string decls, List<string> loopRecords)
    {
        var all = decls.Trim();
        if (loopRecords.Count > 0) all = (all.Length > 0 ? all + "\n" : "") + string.Join("\n", loopRecords.Distinct().Select(r => $"{r} RECORD;  -- implicit FOR-loop record in Oracle"));
        return all.Length == 0 ? "" : "DECLARE\n" + Indent(all) + "\n";
    }

    private static string Indent(string s) => string.Join("\n", s.Replace("\r", "").Split('\n').Select(l => l.Length == 0 ? l : "    " + l.TrimEnd()));

    // Declaration section: variables stay; CURSOR c IS q -> c CURSOR FOR q;
    // exceptions/pragmas/collection types/nested subprograms are flagged.
    private string ConvertDeclarations(List<Tok> t, out string extra)
    {
        extra = "";
        var sb = new StringBuilder();
        int i = FirstSig(t);
        while (i >= 0 && i < t.Count)
        {
            if (t[i].IsWordAny("FUNCTION", "PROCEDURE"))
            {
                var nested = ParseRoutine(t, i);
                Warn($"nested subprogram {nested.SimpleName} — PL/pgSQL has none; move it out to a separate function (kept as a comment).");
                sb.AppendLine("/* TODO(convert): nested subprogram\n" + R(Slice(t, i, nested.End + 1)).Replace("*/", "* /") + "\n*/");
                i = NextSig(t, nested.End);
                continue;
            }
            int start = i, depth = 0;
            while (i < t.Count)
            {
                if (t[i].IsSym("(")) depth++;
                else if (t[i].IsSym(")")) depth--;
                else if (depth == 0 && t[i].IsSym(";")) break;
                i++;
            }
            var item = Slice(t, start, Math.Min(i, t.Count)).Select(z => new Tok(z.Kind, z.Text)).ToList();
            sb.AppendLine(ConvertDeclItem(item));
            i = i < t.Count ? NextSig(t, i) : -1;
        }
        return sb.ToString();
    }

    private string ConvertDeclItem(List<Tok> item)
    {
        var sig = item.Where(z => !z.IsTrivia).ToList();
        if (sig.Count == 0) return "";
        var text = R(item);

        if (sig[0].IsWord("CURSOR"))
        {
            int isIdx = item.FindIndex(z => z.IsWord("IS"));
            if (isIdx < 0) return "-- " + text;
            var head = Slice(item, 0, isIdx).Where(z => !z.IsWord("CURSOR")).ToList();
            var ht = head.Select(z => new Tok(z.Kind, z.Text)).ToList();
            MapTypes(ht);
            var nameAndParams = R(ht);
            var nm = Regex.Match(nameAndParams, @"^(\S+?)\s*(\(.*\))?$", RegexOptions.Singleline);
            var query = Slice(item, isIdx + 1, item.Count);
            Expr(query);
            return $"{nm.Groups[1].Value} CURSOR {nm.Groups[2].Value} FOR {R(query)};".Replace("  ", " ");
        }
        if (sig[0].IsWord("PRAGMA"))
        {
            if (sig.Any(z => z.IsWord("EXCEPTION_INIT"))) Warn("PRAGMA EXCEPTION_INIT — map the ORA code to a PostgreSQL SQLSTATE in the EXCEPTION handler.");
            if (sig.Any(z => z.IsWord("AUTONOMOUS_TRANSACTION"))) Warn("PRAGMA AUTONOMOUS_TRANSACTION has no equivalent (dblink/pg_background can emulate it).");
            return "-- " + text + ";";
        }
        if (sig[0].IsWord("TYPE") && sig.Count > 1 && OracleStructural.CollectionElementType(item) is { } elem)
        {
            var et = SqlTokenizer.Tokenize(elem, ScriptDialect.Oracle);
            MapTypes(et);
            _collTypes[sig[1].Ident] = R(et) + "[]";
            return $"-- collection type {sig[1].Text} -> {R(et)}[] (PostgreSQL array)";
        }
        if (sig.Count >= 2 && sig[1].IsIdent && _collTypes.TryGetValue(sig[1].Ident, out var arrType))
        {
            // v t_list [:= t_list(...)]
            _collVars.Add(sig[0].Ident);
            var assign = sig.FindIndex(z => z.IsSym(":=") || z.IsWord("DEFAULT"));
            var init = "";
            if (assign > 0)
            {
                // Work on the original tokens (with spacing), not the trivia-free list.
                var at = item.IndexOf(sig[assign]);
                var rhs = item.Skip(at + 1).Select(z => new Tok(z.Kind, z.Text)).ToList();
                var rs = rhs.Where(z => !z.IsTrivia).ToList();
                if (rs.Count >= 3 && rs[0].IsIdent && _collTypes.ContainsKey(rs[0].Ident) && rs[1].IsSym("("))
                {
                    var open = rhs.IndexOf(rs[1]);
                    var close = MatchParen(rhs, open);
                    var inner = R(Slice(rhs, open + 1, close));
                    init = inner.Length == 0 ? " := '{}'" : $" := ARRAY[{inner}]";
                }
                else { Expr(rhs); init = " := " + R(rhs); }
            }
            return $"{sig[0].Text} {arrType}{init};";
        }
        if (sig[0].IsWordAny("TYPE", "SUBTYPE"))
        {
            Warn("PL/SQL TYPE/SUBTYPE declarations (records, collections, REF CURSOR) — use arrays, composite types (CREATE TYPE) or refcursor.");
            return "-- TODO(convert): " + text + ";";
        }
        if (sig.Count >= 2 && sig[1].IsWord("EXCEPTION"))
        {
            Warn($"user-defined exception {sig[0].Text} — raise with RAISE EXCEPTION USING ERRCODE = '...' and catch that SQLSTATE.");
            return "-- " + text + ";  -- TODO(convert): user-defined exception";
        }
        Expr(item);
        return R(item) + ";";
    }

    private static readonly HashSet<string> StatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF","ELSIF","ELSE","END","LOOP","WHILE","FOR","EXIT","RETURN","RAISE","NULL","COMMIT","ROLLBACK","SAVEPOINT","SELECT","INSERT","UPDATE",
        "DELETE","MERGE","OPEN","FETCH","CLOSE","EXECUTE","BEGIN","DECLARE","CASE","WHEN","GOTO","PERFORM","CALL","LOCK","SET","WITH","PIPE",
        "FORALL","CONTINUE","TRUNCATE","CREATE","ALTER","DROP","GRANT","REVOKE","COMMENT","EXCEPTION","PRAGMA","GET","NOTICE","THEN","ELSEIF",
    };

    // Body statements: calls get PERFORM/CALL, query FOR-loops lose their
    // parentheses (and get a RECORD declared), unsupported constructs are flagged.
    private string ConvertBody(List<Tok> body, bool isFunction, out List<string> loopRecords)
    {
        loopRecords = new List<string>();
        var t = body.Select(z => new Tok(z.Kind, z.Text)).ToList();
        t = OracleStructural.PlSqlCollections(t, _collVars, _collTypes, Warn);
        Expr(t);

        bool atStart = true;
        for (int i = 0; i < t.Count; i++)
        {
            var x = t[i];
            if (x.IsTrivia) continue;

            if (x.IsWord("BULK") && NextSig(t, i) is var bc and >= 0 && t[bc].IsWord("COLLECT"))
                Warn("BULK COLLECT INTO — use SELECT array_agg(...) INTO v_array, or loop over the query.");
            if (x.IsWord("FORALL")) Warn("FORALL — rewrite as a single set-based INSERT/UPDATE ... SELECT unnest(...).");
            if (x.IsWord("PIPE")) Warn("PIPE ROW — use RETURN NEXT in a RETURNS SETOF function.");
            if (x.IsWord("GOTO")) Warn("GOTO has no PL/pgSQL equivalent — restructure with loops/EXIT.");
            if (isFunction && x.IsWordAny("COMMIT", "ROLLBACK") && atStart)
                Warn("COMMIT/ROLLBACK inside a function is not allowed in PostgreSQL (only in procedures called outside a transaction block).");

            // FOR r IN (SELECT ...) LOOP -> FOR r IN SELECT ... LOOP (+ r RECORD)
            if (x.IsWord("FOR") && atStart)
            {
                var v = NextSig(t, i); var inK = v >= 0 ? NextSig(t, v) : -1; var op = inK >= 0 ? NextSig(t, inK) : -1;
                if (v >= 0 && inK >= 0 && t[inK].IsWord("IN") && op >= 0)
                {
                    if (t[op].IsSym("("))
                    {
                        var cp = MatchParen(t, op);
                        var first = NextSig(t, op);
                        if (cp > 0 && first >= 0 && t[first].IsWordAny("SELECT", "WITH"))
                        {
                            t.RemoveAt(cp); t.RemoveAt(op);
                            loopRecords.Add(t[v].Text);
                        }
                    }
                    else if (t[op].IsWordAny("SELECT", "WITH"))
                    {
                        loopRecords.Add(t[v].Text);
                    }
                }
            }

            // Synthetic tokens produced by Expr() (e.g. "RAISE EXCEPTION ...") contain spaces — never calls.
            if (atStart && x.IsIdent && !StatementKeywords.Contains(x.Text) && !x.Text.Contains(' ') && !x.Text.Contains('('))
            {
                // identifier chain
                int j = i, last = i;
                var chain = new StringBuilder(x.Text);
                while (true)
                {
                    var d = NextSig(t, j);
                    if (d >= 0 && t[d].IsSym(".")) { var nx = NextSig(t, d); if (nx >= 0 && t[nx].IsIdent) { chain.Append('.').Append(t[nx].Text); j = nx; last = nx; continue; } }
                    break;
                }
                var after = NextSig(t, last);
                bool isCall = false; int callEnd = -1; bool noParens = false;
                if (after >= 0 && t[after].IsSym("("))
                {
                    var cp = MatchParen(t, after);
                    var semi = cp >= 0 ? NextSig(t, cp) : -1;
                    if (semi >= 0 && t[semi].IsSym(";")) { isCall = true; callEnd = cp; }
                }
                else if (after >= 0 && t[after].IsSym(";")) { isCall = true; noParens = true; callEnd = last; }

                if (isCall)
                {
                    var key = chain.ToString().Replace("\"", "").ToUpperInvariant();
                    var simple = key.Split('.').Last();
                    var isProc = _procs.Contains(key) || _procs.Contains(simple);
                    t.Insert(i, new Tok(TokKind.Word, isProc ? "CALL " : "PERFORM "));
                    if (noParens) t.Insert(callEnd + 2, new Tok(TokKind.Symbol, "()"));
                    if (!isProc) Warn($"call to {chain} converted to PERFORM (assumed function) — change to CALL if it is a procedure.");
                    i = callEnd + 1;
                    atStart = false;
                    continue;
                }
            }

            if (x.IsWord("RAISE") && atStart)
            {
                var nx = NextSig(t, i);
                if (nx >= 0 && t[nx].IsIdent && !t[nx].IsWordAny("EXCEPTION", "NOTICE", "WARNING", "INFO", "LOG", "DEBUG", "USING"))
                {
                    var semi = NextSig(t, nx);
                    if (semi >= 0 && t[semi].IsSym(";"))
                    {
                        var exName = t[nx].Text;
                        if (!ExceptionNames.Values.Contains(exName, StringComparer.OrdinalIgnoreCase) && !exName.Equals("no_data_found", StringComparison.OrdinalIgnoreCase) && !exName.Equals("too_many_rows", StringComparison.OrdinalIgnoreCase))
                        {
                            Replace(t, i, nx, $"RAISE EXCEPTION '{exName.Replace("'", "''")}'");
                            Warn($"RAISE {exName} (user exception) converted to RAISE EXCEPTION '{exName}' — handlers WHEN {exName} must become WHEN OTHERS / SQLSTATE checks.");
                        }
                    }
                }
            }

            atStart = x.IsSym(";") || x.IsWordAny("BEGIN", "THEN", "ELSE", "LOOP", "DECLARE") || x.IsSym(">>");
        }
        return R(t);
    }

    // --------------------------------------------------------------- packages

    private string ConvertPackageSpec(List<Tok> t, int k)
    {
        var nameIdx = NextSig(t, k);
        var pkg = LastIdentOf(t, nameIdx);
        var pkgSchema = Id(pkg);
        var isIdx = FindWord(t, nameIdx, "IS", "AS");
        var items = isIdx >= 0 ? Slice(t, isIdx + 1, t.Count) : new List<Tok>();

        var sb = new StringBuilder();
        sb.AppendLine($"-- Package spec {pkg}: PostgreSQL has no packages.");
        if (_ctx.PackageAsSchema) sb.AppendLine($"CREATE SCHEMA IF NOT EXISTS {pkgSchema};");
        var sig = items.Where(z => !z.IsTrivia).ToList();
        for (int i = 0; i < sig.Count; i++)
        {
            if (!sig[i].IsWord("TYPE") || (i > 0 && !sig[i - 1].IsSym(";") && !sig[i - 1].IsWordAny("IS", "AS"))) continue;
            var end = sig.FindIndex(i, z => z.IsSym(";"));
            if (end < 0) break;
            if (OracleStructural.CollectionElementType(sig.Skip(i).Take(end - i).ToList()) is { } elem)
            {
                var et = SqlTokenizer.Tokenize(elem, ScriptDialect.Oracle);
                MapTypes(et);
                _collTypes[sig[i + 1].Ident] = R(et) + "[]";
                sb.AppendLine($"-- collection type {pkg}.{sig[i + 1].Text} -> {R(et)}[] (used as a PostgreSQL array in the body)");
            }
        }
        bool hasState = false;
        for (int i = 0; i < sig.Count; i++)
        {
            if (sig[i].IsWordAny("FUNCTION", "PROCEDURE"))
            {
                // skip to ';'
                while (i < sig.Count && !sig[i].IsSym(";")) i++;
                continue;
            }
            if (sig[i].IsWord("END")) break;
            if (i == 0 || sig[i - 1].IsSym(";")) hasState |= sig[i].IsIdent && !sig[i].IsWordAny("TYPE", "SUBTYPE", "PRAGMA", "CURSOR");
        }
        if (hasState) Warn($"package {pkg} declares public variables/constants — PostgreSQL has no package state; use a config table, custom GUCs (set_config/current_setting) or constant functions.");
        if (sig.Any(z => z.IsWordAny("TYPE", "SUBTYPE"))) Warn($"package {pkg} declares types — create them with CREATE TYPE in schema {pkgSchema}.");
        sb.AppendLine("/* original spec:\n" + R(t).Replace("*/", "* /") + "\n*/");
        return sb.ToString();
    }

    private string ConvertPackageBody(List<Tok> t, int bodyIdx)
    {
        var nameIdx = NextSig(t, bodyIdx);
        var pkg = LastIdentOf(t, nameIdx);
        var pkgSchema = Id(pkg);
        var isIdx = FindWord(t, nameIdx, "IS", "AS");
        if (isIdx < 0) throw new FormatException($"package body {pkg}: missing IS/AS");

        var sb = new StringBuilder();
        if (_ctx.PackageAsSchema)
            sb.AppendLine($"-- Package {pkg} -> schema {pkgSchema} (calls {pkg.ToLowerInvariant()}.proc(...) keep working)").AppendLine($"CREATE SCHEMA IF NOT EXISTS {pkgSchema};").AppendLine();

        int i = NextSig(t, isIdx);
        var state = new List<string>();
        while (i >= 0 && i < t.Count)
        {
            if (t[i].IsWordAny("FUNCTION", "PROCEDURE"))
            {
                var r = ParseRoutine(t, i);
                if (!r.IsForward)
                    sb.AppendLine(EmitRoutine(r, QualifiedRoutineName(r.Name, pkgSchema), pkg)).AppendLine();
                i = NextSig(t, r.End);
                continue;
            }
            if (t[i].IsWord("END")) break;
            if (t[i].IsWord("BEGIN"))
            {
                var e = MatchBlockEnd(t, i);
                Warn($"package {pkg} has an initialization section — run that logic explicitly (e.g. from each entry point).");
                sb.AppendLine("/* TODO(convert): package initialization block\n" + R(Slice(t, i, e + 1)).Replace("*/", "* /") + "\n*/");
                break;
            }
            int start = i, depth = 0;
            while (i < t.Count)
            {
                if (t[i].IsSym("(")) depth++;
                else if (t[i].IsSym(")")) depth--;
                else if (depth == 0 && t[i].IsSym(";")) break;
                i++;
            }
            state.Add(R(Slice(t, start, Math.Min(i + 1, t.Count))));
            i = i < t.Count ? NextSig(t, i) : -1;
        }
        if (state.Count > 0)
        {
            Warn($"package {pkg} body has package-level state (variables/cursors/types) — no PostgreSQL equivalent; move constants into functions and state into a table or GUC.");
            sb.AppendLine("/* TODO(convert): package-level declarations\n" + string.Join("\n", state).Replace("*/", "* /") + "\n*/");
        }
        return sb.ToString();
    }

    private static string LastIdentOf(List<Tok> t, int i)
    {
        var name = t[i].Ident;
        while (true)
        {
            var d = NextSig(t, i);
            if (d >= 0 && t[d].IsSym(".")) { var n = NextSig(t, d); if (n >= 0 && t[n].IsIdent) { name = t[n].Ident; i = n; continue; } }
            return name;
        }
    }

    private static int FindWord(List<Tok> t, int from, params string[] words)
    {
        for (int i = from; i >= 0 && i < t.Count; i++) if (t[i].IsWordAny(words)) return i;
        return -1;
    }

    // --------------------------------------------------------------- triggers

    private string ConvertTrigger(List<Tok> t, int k)
    {
        var sig = new List<int>();
        for (int i = NextSig(t, k); i >= 0; i = NextSig(t, i)) sig.Add(i);
        int p = 0;
        var nameToks = new List<Tok>();
        while (p < sig.Count && (t[sig[p]].IsIdent || t[sig[p]].IsSym(".")) && !t[sig[p]].IsWordAny("BEFORE", "AFTER", "INSTEAD", "FOR"))
        { nameToks.Add(t[sig[p]]); p++; }
        var trgName = nameToks.Where(z => z.IsIdent).Select(z => Id(z.Ident)).LastOrDefault() ?? "trg";

        if (t.Any(z => z.IsWord("COMPOUND")))
        {
            Warn($"compound trigger {trgName} — split into separate BEFORE/AFTER trigger functions manually.");
            return "/* TODO(convert): compound trigger\n" + R(t).Replace("*/", "* /") + "\n*/";
        }

        var timing = new StringBuilder();
        if (p < sig.Count && t[sig[p]].IsWordAny("BEFORE", "AFTER")) { timing.Append(t[sig[p]].Upper); p++; }
        else if (p + 1 < sig.Count && t[sig[p]].IsWord("INSTEAD")) { timing.Append("INSTEAD OF"); p += 2; }        var events = new StringBuilder();
        var eventWords = new List<string>();
        while (p < sig.Count && !t[sig[p]].IsWord("ON"))
        {
            var w = t[sig[p]];
            if (w.IsWordAny("INSERT", "UPDATE", "DELETE")) eventWords.Add(w.Upper);
            events.Append(w.IsIdent && !w.IsWordAny("INSERT", "UPDATE", "DELETE", "OR", "OF") ? Id(w.Ident) : w.Upper).Append(w.IsSym(",") ? "" : " ");
            p++;
        }
        p++; // ON
        var tableToks = new List<Tok>();
        while (p < sig.Count && (t[sig[p]].IsIdent || t[sig[p]].IsSym(".")) && !t[sig[p]].IsWordAny("REFERENCING", "FOR", "WHEN", "BEGIN", "DECLARE", "ENABLE", "DISABLE", "FOLLOWS", "PRECEDES"))
        { tableToks.Add(t[sig[p]]); p++; }
        var table = string.Join(".", tableToks.Where(z => z.IsIdent).Select(z => z.Kind == TokKind.QuotedIdent ? z.Text : z.Text.Contains('.') ? z.Text : Id(z.Ident)));
        if (!table.Contains('.') && TargetSchema.Length > 0) table = TargetSchema + "." + table;

        string newAlias = "NEW", oldAlias = "OLD";
        bool rowLevel = false;
        string when = "";
        while (p < sig.Count && !t[sig[p]].IsWordAny("BEGIN", "DECLARE"))
        {
            var w = t[sig[p]];
            if (w.IsWord("REFERENCING"))
            {
                p++;
                while (p + 2 < sig.Count && t[sig[p]].IsWordAny("NEW", "OLD", "PARENT"))
                {
                    var which = t[sig[p]].Upper; var alias = t[sig[p + 2]].IsWord("AS") ? t[sig[p + 3]].Text : t[sig[p + 1]].Text;
                    if (which == "NEW") newAlias = alias; else if (which == "OLD") oldAlias = alias;
                    p += t[sig[p + 1]].IsWord("AS") ? 3 : 2;
                }
                continue;
            }
            if (w.IsWord("FOR")) { rowLevel = true; p += 3; continue; } // FOR EACH ROW
            if (w.IsWord("WHEN"))
            {
                var open = sig[p + 1];
                var close = MatchParen(t, open);
                var cond = Slice(t, open, close + 1).Select(z => z.Kind == TokKind.BindVar ? new Tok(TokKind.Word, z.Text.TrimStart(':')) : new Tok(z.Kind, z.Text)).ToList();
                Expr(cond);
                when = " WHEN " + R(cond);
                while (p < sig.Count && sig[p] <= close) p++;
                continue;
            }
            p++;
        }
        if (p >= sig.Count) throw new FormatException($"trigger {trgName}: no body");
        var bodyStart = sig[p];
        string decls = "";
        int begin = bodyStart;
        if (t[bodyStart].IsWord("DECLARE"))
        {
            begin = FindTopLevelBegin(t, NextSig(t, bodyStart));
            decls = ConvertDeclarations(Slice(t, bodyStart + 1, begin), out _);
        }
        var end = MatchBlockEnd(t, begin);
        var raw = Slice(t, begin + 1, end).Select(z => new Tok(z.Kind, z.Text)).ToList();

        for (int i = 0; i < raw.Count; i++)
        {
            var z = raw[i];
            if (z.Kind == TokKind.BindVar)
            {
                var v = z.Text.TrimStart(':');
                if (v.Equals(newAlias, StringComparison.OrdinalIgnoreCase) || v.Equals("NEW", StringComparison.OrdinalIgnoreCase)) raw[i] = new Tok(TokKind.Word, "NEW");
                else if (v.Equals(oldAlias, StringComparison.OrdinalIgnoreCase) || v.Equals("OLD", StringComparison.OrdinalIgnoreCase)) raw[i] = new Tok(TokKind.Word, "OLD");
            }
            else if (z.IsWordAny("INSERTING", "UPDATING", "DELETING"))
            {
                var op = z.Upper switch { "INSERTING" => "INSERT", "UPDATING" => "UPDATE", _ => "DELETE" };
                var nx = NextSig(raw, i);
                if (nx >= 0 && raw[nx].IsSym("("))
                {
                    Warn($"trigger {trgName}: UPDATING('column') — compare NEW.col IS DISTINCT FROM OLD.col instead.");
                    continue;
                }
                raw[i] = new Tok(TokKind.Word, $"(TG_OP = '{op}')");
            }
        }
        var retVal = !rowLevel ? "NULL" : eventWords.All(e => e == "DELETE") ? "OLD" : "NEW";
        // RETURN; inside an Oracle trigger body -> RETURN NEW/OLD;
        for (int i = 0; i < raw.Count; i++)
            if (raw[i].IsWord("RETURN") && NextSig(raw, i) is var s and >= 0 && raw[s].IsSym(";")) raw[i] = new Tok(TokKind.Word, "RETURN " + retVal);

        var body = ConvertBody(raw, isFunction: true, out var loopRecords);
        var schemaPrefix = table.Contains('.') ? table[..table.LastIndexOf('.')] + "." : (TargetSchema.Length > 0 ? TargetSchema + "." : "");
        var fn = $"{schemaPrefix}{Id(trgName.Trim('"') + "_fn")}";

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE OR REPLACE FUNCTION {fn}()");
        sb.AppendLine("RETURNS trigger");
        sb.AppendLine("LANGUAGE plpgsql");
        sb.AppendLine("AS $body$");
        sb.Append(DeclareBlock(decls, loopRecords));
        sb.AppendLine("BEGIN");
        sb.AppendLine(Indent(body));
        sb.AppendLine($"    RETURN {retVal};");
        sb.AppendLine("END;");
        sb.AppendLine("$body$;");
        sb.AppendLine();
        sb.AppendLine($"DROP TRIGGER IF EXISTS {trgName} ON {table};");
        sb.Append($"CREATE TRIGGER {trgName} {timing.ToString().Trim()} {events.ToString().Trim()} ON {table} FOR EACH {(rowLevel ? "ROW" : "STATEMENT")}{when} EXECUTE FUNCTION {fn}();");
        return sb.ToString();
    }

    // -------------------------------------------------------------- sequences

    private string ConvertSequence(List<Tok> t, int k)
    {
        var sig = new List<Tok>();
        for (int i = NextSig(t, k); i >= 0; i = NextSig(t, i)) sig.Add(t[i]);
        int p = 0;
        var name = new StringBuilder();
        while (p < sig.Count && (sig[p].IsIdent || sig[p].IsSym(".")) && !sig[p].IsWordAny("INCREMENT", "START", "MINVALUE", "MAXVALUE", "NOMINVALUE", "NOMAXVALUE", "CACHE", "NOCACHE", "CYCLE", "NOCYCLE", "ORDER", "NOORDER"))
        { name.Append(sig[p].Text); p++; }
        var seqName = name.ToString();
        if (!seqName.Contains('.') && TargetSchema.Length > 0) seqName = TargetSchema + "." + seqName;

        var opts = new List<string>();
        while (p < sig.Count)
        {
            var w = sig[p].Upper;
            string? val = p + 1 < sig.Count ? sig[p + 1].Text : null;
            switch (w)
            {
                case "INCREMENT": opts.Add($"INCREMENT BY {sig[p + 2].Text}"); p += 3; continue;
                case "START": opts.Add($"START WITH {sig[p + 2].Text}"); p += 3; continue;
                case "MINVALUE": opts.Add(BigOk(val) ? $"MINVALUE {val}" : "NO MINVALUE"); p += 2; continue;
                case "MAXVALUE": if (BigOk(val)) opts.Add($"MAXVALUE {val}"); p += 2; continue;
                case "NOMINVALUE": opts.Add("NO MINVALUE"); p++; continue;
                case "NOMAXVALUE": opts.Add("NO MAXVALUE"); p++; continue;
                case "CACHE": opts.Add($"CACHE {val}"); p += 2; continue;
                case "CYCLE": opts.Add("CYCLE"); p++; continue;
                case "NOCYCLE": opts.Add("NO CYCLE"); p++; continue;
                case "SHARING": p += 3; continue;
                default: p++; continue; // NOCACHE, ORDER/NOORDER, KEEP/NOKEEP, SCALE, SESSION/GLOBAL, PARTITION...
            }
        }
        return $"CREATE SEQUENCE IF NOT EXISTS {seqName} {string.Join(" ", opts)};".Replace("  ", " ");
    }

    private static bool BigOk(string? v) =>
        v != null && decimal.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) && d <= long.MaxValue && d >= long.MinValue;

    // ------------------------------------------------------ views / mviews

    private string ConvertView(List<Tok> t, int k)
    {
        int asIdx = -1, depth = 0;
        for (int i = NextSig(t, k); i >= 0; i = NextSig(t, i))
        {
            if (t[i].IsSym("(")) depth++;
            else if (t[i].IsSym(")")) depth--;
            else if (depth == 0 && t[i].IsWord("AS")) { asIdx = i; break; }
        }
        if (asIdx < 0) return ConvertPlainSql(t);
        var head = Slice(t, NextSig(t, k), asIdx).Where(z => !z.IsWordAny("BEQUEATH", "DEFINER", "CURRENT_USER", "SHARING", "METADATA", "DATA", "NONE", "EXTENDED")).ToList();
        var query = Slice(t, asIdx + 1, t.Count);
        // strip WITH READ ONLY / WITH CHECK OPTION CONSTRAINT x
        var qs = query.Select(z => new Tok(z.Kind, z.Text)).ToList();
        for (int i = 0; i < qs.Count; i++)
        {
            if (!qs[i].IsWord("WITH")) continue;
            var a = NextSig(qs, i); var b = a >= 0 ? NextSig(qs, a) : -1;
            if (a >= 0 && b >= 0 && qs[a].IsWord("READ") && qs[b].IsWord("ONLY")) { qs.RemoveRange(i, b - i + 1); Warn("WITH READ ONLY view option dropped (revoke INSERT/UPDATE/DELETE instead)."); break; }
            if (a >= 0 && b >= 0 && qs[a].IsWord("CHECK") && qs[b].IsWord("OPTION"))
            {
                var c = NextSig(qs, b);
                if (c >= 0 && qs[c].IsWord("CONSTRAINT")) qs.RemoveRange(b + 1, NextSig(qs, c) - b);
                break;
            }
        }
        Expr(qs);
        var name = R(head);
        if (!name.Contains('.') && TargetSchema.Length > 0) name = TargetSchema + "." + name;
        return $"CREATE OR REPLACE VIEW {name} AS\n{R(qs).TrimEnd(';')};";
    }

    private string ConvertMaterializedView(List<Tok> t, int k)
    {
        int asIdx = FindWord(t, k, "AS");
        var nameToks = Slice(t, NextSig(t, k), asIdx);
        var name = nameToks.Where(z => z.IsIdent || z.IsSym(".")).Aggregate("", (s, z) => s + z.Text);
        // skip BUILD/REFRESH clauses between name and AS SELECT
        var sel = FindWord(t, asIdx, "SELECT", "WITH");
        var qs = Slice(t, sel, t.Count);
        Expr(qs);
        Warn("materialized view refresh options dropped — schedule REFRESH MATERIALIZED VIEW yourself.");
        if (!name.Contains('.') && TargetSchema.Length > 0) name = TargetSchema + "." + name;
        return $"CREATE MATERIALIZED VIEW IF NOT EXISTS {name} AS\n{R(qs).TrimEnd(';')};";
    }

    // ---------------------------------------------------------- tables/index

    private string ConvertTable(List<Tok> t, int k, bool temporary)
    {
        var open = t.FindIndex(k, z => z.IsSym("("));
        if (open < 0) return ConvertPlainSql(t);
        var close = MatchParen(t, open);
        var name = R(Slice(t, NextSig(t, k), open));
        if (!name.Contains('.') && TargetSchema.Length > 0) name = TargetSchema + "." + name;
        var inner = Slice(t, open + 1, close).Select(z => new Tok(z.Kind, z.Text)).ToList();

        for (int i = 0; i < inner.Count; i++)
        {
            if (inner[i].IsWord("USING") && NextSig(inner, i) is var ix and >= 0 && inner[ix].IsWord("INDEX"))
            {
                int depth = 0, j = ix + 1;
                for (; j < inner.Count; j++)
                {
                    if (inner[j].IsSym("(")) depth++;
                    else if (inner[j].IsSym(")")) depth--;
                    else if (depth == 0 && inner[j].IsSym(",")) break;
                    if (depth < 0) break;
                }
                inner.RemoveRange(i, j - i);
                i--;
                continue;
            }
            if (inner[i].IsWordAny("ENABLE", "DISABLE", "VALIDATE", "NOVALIDATE", "RELY", "NORELY", "VISIBLE", "INVISIBLE"))
            {
                inner.RemoveAt(i);
                if (i > 0 && inner[i - 1].Kind == TokKind.Space) { inner.RemoveAt(i - 1); i--; }
                i--; continue;
            }
            if (inner[i].IsWord("ON") && NextSig(inner, i) is var nn and >= 0 && inner[nn].IsWord("NULL") && PrevSig(inner, i) is var dd and >= 0 && inner[dd].IsWord("DEFAULT"))
            { inner.RemoveRange(i, nn - i + 1); i--; continue; }
            if (inner[i].IsWord("IDENTITY") && NextSig(inner, i) is var ip and >= 0 && inner[ip].IsSym("("))
            {
                inner.RemoveRange(ip, MatchParen(inner, ip) - ip + 1);
                Warn("identity column options (MINVALUE/MAXVALUE/CACHE...) dropped — defaults used; set START WITH past existing data.");
            }
        }
        Expr(inner);
        var tail = temporary ? ConvertGttTail(Slice(t, close + 1, t.Count)) : "";
        if (Slice(t, close + 1, t.Count).Any(z => z.IsWordAny("PARTITION", "LOB", "ORGANIZATION")))
            Warn($"table {name}: partitioning / LOB storage / IOT clauses dropped — recreate partitions with PostgreSQL declarative partitioning if needed.");
        return $"CREATE {(temporary ? "TEMPORARY " : "")}TABLE {name} (\n{R(inner)}\n){tail};";
    }

    private string ConvertGttTail(List<Tok> tail)
    {
        Warn("GLOBAL TEMPORARY TABLE -> PostgreSQL TEMPORARY TABLE is per-session and must be created in each session (consider the pgtt extension).");
        var s = R(tail).ToUpperInvariant();
        return s.Contains("PRESERVE ROWS") ? " ON COMMIT PRESERVE ROWS" : s.Contains("DELETE ROWS") ? " ON COMMIT DELETE ROWS" : "";
    }

    private string ConvertIndex(List<Tok> t, int k, bool unique, bool bitmap)
    {
        if (bitmap) Warn("BITMAP index converted to a regular B-tree index.");
        var on = FindWord(t, k, "ON");
        if (on < 0) return ConvertPlainSql(t);
        var open = t.FindIndex(on, z => z.IsSym("("));
        var close = MatchParen(t, open);
        var name = R(Slice(t, NextSig(t, k), on));
        name = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name; // PG index lives in the table's schema
        var table = R(Slice(t, NextSig(t, on), open));
        if (!table.Contains('.') && TargetSchema.Length > 0) table = TargetSchema + "." + table;
        var cols = Slice(t, open + 1, close).Select(z => new Tok(z.Kind, z.Text)).ToList();
        Expr(cols);
        return $"CREATE {(unique ? "UNIQUE " : "")}INDEX IF NOT EXISTS {name} ON {table} ({R(cols)});";
    }

    // ------------------------------------------------------------------ types

    private string ConvertType(List<Tok> t, int k)
    {
        var nb = NextSig(t, k);
        if (nb >= 0 && t[nb].IsWord("BODY"))
        {
            Warn("object TYPE BODY (member methods) — rewrite methods as ordinary functions taking the composite type.");
            return "/* TODO(convert): type body\n" + R(t).Replace("*/", "* /") + "\n*/";
        }
        var asIdx = FindWord(t, k, "AS", "IS");
        var name = R(Slice(t, nb, asIdx)).Replace("FORCE", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!name.Contains('.') && TargetSchema.Length > 0) name = TargetSchema + "." + name;
        var kindIdx = NextSig(t, asIdx);
        if (kindIdx >= 0 && t[kindIdx].IsWord("OBJECT"))
        {
            var open = t.FindIndex(kindIdx, z => z.IsSym("("));
            var close = MatchParen(t, open);
            var attrs = SplitArgs(t, open, close)
                .Where(a => !a.Any(z => z.IsWordAny("MEMBER", "STATIC", "CONSTRUCTOR", "MAP", "ORDER")))
                .Select(a => { var l = a.Select(z => new Tok(z.Kind, z.Text)).ToList(); MapTypes(l); return R(l); })
                .ToList();
            if (SplitArgs(t, open, close).Count != attrs.Count) Warn($"type {name}: member methods dropped — rewrite as functions.");
            return $"CREATE TYPE {name} AS (\n    {string.Join(",\n    ", attrs)}\n);";
        }
        Warn($"type {name}: collection types (TABLE OF / VARRAY) map to PostgreSQL arrays — declare columns/variables as element_type[] instead.");
        return "-- TODO(convert): " + R(t).Replace("\n", "\n-- ");
    }
}
