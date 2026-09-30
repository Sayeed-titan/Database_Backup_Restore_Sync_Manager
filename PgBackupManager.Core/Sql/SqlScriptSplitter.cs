using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Sql;

public enum StatementKind { Sql, CopyIn, Meta }

// CopyData holds the raw rows of a pg_dump "COPY ... FROM stdin;" block
// (everything up to the "\." terminator line).
public sealed record SqlStatement(string Text, int StartLine, StatementKind Kind = StatementKind.Sql, string? CopyData = null);

// Splits a script into executable statements the way each engine's own
// client would (psql, sqlcmd/SSMS "GO", SQL*Plus "/", mysql DELIMITER),
// never splitting inside strings, comments, quoted identifiers, PG dollar
// quotes, or PL/SQL blocks.
public static class SqlScriptSplitter
{
    public static List<string> Split(string sql, ScriptDialect d) =>
        SplitDetailed(sql, d).Where(s => s.Kind == StatementKind.Sql).Select(s => s.Text).ToList();

    private static readonly Regex PlSqlStart = new(
        @"^(CREATE\s+(OR\s+REPLACE\s+)?((NON)?EDITIONABLE\s+)?(FUNCTION|PROCEDURE|PACKAGE|TRIGGER|TYPE)\b|DECLARE\b|BEGIN\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqliteTriggerStart = new(@"^CREATE\s+(TEMP\s+|TEMPORARY\s+)?TRIGGER\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndsWithEnd = new(@"\bEND\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CopyFromStdin = new(@"^COPY\s+.+\s+FROM\s+stdin\b", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex GoLine = new(@"^\s*GO(\s+\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DelimiterLine = new(@"^\s*DELIMITER\s+(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqlPlusCommand = new(@"^\s*(SET|SPOOL|PROMPT|WHENEVER|EXIT|QUIT|REM|REMARK|SHOW|CONNECT|DEFINE|UNDEFINE|COLUMN|TTITLE|BTITLE|@)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<SqlStatement> SplitDetailed(string sql, ScriptDialect d)
    {
        var result = new List<SqlStatement>();
        var sb = new StringBuilder();
        int n = sql.Length, i = 0, stmtStart = -1;
        string delimiter = ";";
        bool plsql = false;

        var lineStarts = new List<int> { 0 };
        for (int k = 0; k < n; k++) if (sql[k] == '\n') lineStarts.Add(k + 1);
        int LineOf(int offset) { var idx = lineStarts.BinarySearch(offset); return (idx >= 0 ? idx : ~idx - 1) + 1; }

        void Append(string s, int at) { if (stmtStart < 0 && !string.IsNullOrWhiteSpace(s)) stmtStart = at; sb.Append(s); }
        void Append1(char ch, int at) { if (stmtStart < 0 && !char.IsWhiteSpace(ch)) stmtStart = at; sb.Append(ch); }

        SqlStatement? Flush()
        {
            var text = sb.ToString().Trim();
            var start = stmtStart;
            sb.Clear(); stmtStart = -1; plsql = false;
            if (text.Length == 0 || IsOnlyComments(text)) return null;
            var st = new SqlStatement(text, LineOf(Math.Max(0, start)));
            result.Add(st);
            return st;
        }

        string LineAt(int pos)
        {
            var e = sql.IndexOf('\n', pos);
            return (e < 0 ? sql[pos..] : sql[pos..e]).TrimEnd('\r');
        }
        int NextLine(int pos) { var e = sql.IndexOf('\n', pos); return e < 0 ? n : e + 1; }

        while (i < n)
        {
            char c = sql[i];
            bool atLineStart = i == 0 || sql[i - 1] == '\n';

            if (atLineStart)
            {
                var line = LineAt(i);
                var trimmed = line.Trim();
                bool empty = string.IsNullOrWhiteSpace(sb.ToString());
                if (d == ScriptDialect.SqlServer && GoLine.IsMatch(line)) { Flush(); i = NextLine(i); continue; }
                if (d == ScriptDialect.Oracle && trimmed == "/") { Flush(); i = NextLine(i); continue; }
                if (d == ScriptDialect.MySql && DelimiterLine.Match(line) is { Success: true } dm)
                {
                    Flush(); delimiter = dm.Groups[1].Value; i = NextLine(i); continue;
                }
                if (empty && d == ScriptDialect.Postgres && trimmed.StartsWith('\\'))
                {
                    result.Add(new SqlStatement(trimmed, LineOf(i), StatementKind.Meta)); i = NextLine(i); continue;
                }
                if (empty && d == ScriptDialect.Oracle && SqlPlusCommand.IsMatch(line))
                {
                    result.Add(new SqlStatement(trimmed, LineOf(i), StatementKind.Meta)); i = NextLine(i); continue;
                }
            }

            char next = i + 1 < n ? sql[i + 1] : '\0';
            char prev = i > 0 ? sql[i - 1] : '\0';

            // -- line comment (and MySQL #)
            if ((c == '-' && next == '-') || (c == '#' && d == ScriptDialect.MySql))
            {
                var e = sql.IndexOf('\n', i); if (e < 0) e = n;
                sb.Append(sql, i, e - i); i = e; continue;
            }
            // /* block comment */
            if (c == '/' && next == '*')
            {
                var e = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 ? n : e + 2;
                sb.Append(sql, i, e - i); i = e; continue;
            }
            // Oracle q'[ ... ]'
            if (d == ScriptDialect.Oracle && (c == 'q' || c == 'Q') && next == '\'' && !IsWordChar(prev) && i + 2 < n)
            {
                var open = sql[i + 2];
                var close = open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open };
                var e = sql.IndexOf(close + "'", i + 3, StringComparison.Ordinal); e = e < 0 ? n : e + 2;
                Append(sql[i..e], i); i = e; continue;
            }
            // 'string'
            if (c == '\'')
            {
                bool backslashEscapes = d == ScriptDialect.MySql || (d == ScriptDialect.Postgres && (prev == 'E' || prev == 'e') && (i < 2 || !IsWordChar(sql[i - 2])));
                int j = i + 1;
                while (j < n)
                {
                    if (backslashEscapes && sql[j] == '\\') { j += 2; continue; }
                    if (sql[j] == '\'') { if (j + 1 < n && sql[j + 1] == '\'') { j += 2; continue; } break; }
                    j++;
                }
                j = Math.Min(n, j + 1);
                Append(sql[i..j], i); i = j; continue;
            }
            // "quoted identifier"
            if (c == '"')
            {
                int j = i + 1;
                while (j < n) { if (sql[j] == '"') { if (j + 1 < n && sql[j + 1] == '"') { j += 2; continue; } break; } j++; }
                j = Math.Min(n, j + 1);
                Append(sql[i..j], i); i = j; continue;
            }
            if (c == '[' && d == ScriptDialect.SqlServer)
            {
                var e = sql.IndexOf(']', i + 1); e = e < 0 ? n : e + 1;
                Append(sql[i..e], i); i = e; continue;
            }
            if (c == '`' && d == ScriptDialect.MySql)
            {
                var e = sql.IndexOf('`', i + 1); e = e < 0 ? n : e + 1;
                Append(sql[i..e], i); i = e; continue;
            }
            // $tag$ ... $tag$
            if (c == '$' && d == ScriptDialect.Postgres && !IsWordChar(prev))
            {
                var m = DollarTag.Match(sql, i);
                if (m.Success && m.Index == i)
                {
                    var tag = m.Value;
                    var e = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal); e = e < 0 ? n : e + tag.Length;
                    Append(sql[i..e], i); i = e; continue;
                }
            }

            // delimiter
            if (string.CompareOrdinal(sql, i, delimiter, 0, delimiter.Length) == 0)
            {
                var soFar = StripLeadingComments(sb.ToString());
                if (d == ScriptDialect.SqlServer) { Append(delimiter, i); i += delimiter.Length; continue; }
                if (d == ScriptDialect.Oracle && (plsql || PlSqlStart.IsMatch(soFar))) { plsql = true; Append(delimiter, i); i += delimiter.Length; continue; }
                if (d == ScriptDialect.Sqlite && SqliteTriggerStart.IsMatch(soFar) && !EndsWithEnd.IsMatch(sb.ToString())) { Append(delimiter, i); i += delimiter.Length; continue; }

                i += delimiter.Length;
                var st = Flush();
                if (st != null && d == ScriptDialect.Postgres && CopyFromStdin.IsMatch(StripLeadingComments(st.Text)))
                {
                    // Raw data lines follow until a line that is exactly "\."
                    int dataStart = NextLine(i);
                    int p = dataStart;
                    while (p < n && LineAt(p) != "\\.") p = NextLine(p);
                    var data = p > dataStart ? sql[dataStart..p] : "";
                    result[^1] = st with { Kind = StatementKind.CopyIn, CopyData = data };
                    i = p < n ? NextLine(p) : n;
                }
                continue;
            }

            Append1(c, i);
            i++;
        }
        Flush();
        return result;
    }

    private static readonly Regex DollarTag = new(@"\$([A-Za-z_][A-Za-z0-9_]*)?\$", RegexOptions.Compiled);

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    public static string StripLeadingComments(string s)
    {
        var t = s.TrimStart();
        while (true)
        {
            if (t.StartsWith("--")) { var e = t.IndexOf('\n'); t = e < 0 ? "" : t[(e + 1)..].TrimStart(); continue; }
            if (t.StartsWith("/*")) { var e = t.IndexOf("*/", StringComparison.Ordinal); t = e < 0 ? "" : t[(e + 2)..].TrimStart(); continue; }
            return t;
        }
    }

    private static bool IsOnlyComments(string s) => StripLeadingComments(s).Length == 0;

    // Is this statement a row-returning query? (Used by the editor to pick
    // ExecuteReader vs ExecuteNonQuery and to label results.)
    public static bool LooksLikeQuery(string sql)
    {
        var t = StripLeadingComments(sql);
        return Regex.IsMatch(t, @"^(SELECT|WITH|SHOW|VALUES|TABLE|EXPLAIN|DESC|DESCRIBE|PRAGMA|EXEC|EXECUTE|CALL)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(t, @"\bRETURNING\b", RegexOptions.IgnoreCase);
    }
}
