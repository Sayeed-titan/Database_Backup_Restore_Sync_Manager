using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Sql;

public enum TokKind { Word, QuotedIdent, String, Number, Comment, Space, Symbol, Dollar, BindVar, AtVar }

public sealed class Tok
{
    public TokKind Kind;
    public string Text;
    public Tok(TokKind kind, string text) { Kind = kind; Text = text; }

    public bool IsWord(string w) => Kind == TokKind.Word && string.Equals(Text, w, StringComparison.OrdinalIgnoreCase);
    public bool IsWordAny(params string[] ws) => Kind == TokKind.Word && ws.Any(w => string.Equals(Text, w, StringComparison.OrdinalIgnoreCase));
    public bool IsSym(string s) => Kind == TokKind.Symbol && Text == s;
    public bool IsTrivia => Kind is TokKind.Space or TokKind.Comment;
    public bool IsIdent => Kind is TokKind.Word or TokKind.QuotedIdent;
    public string Upper => Text.ToUpperInvariant();

    // Identifier value without quotes/brackets.
    public string Ident => Kind == TokKind.QuotedIdent && Text.Length >= 2 ? Text[1..^1].Replace("\"\"", "\"").Replace("]]", "]").Replace("``", "`") : Text;

    public override string ToString() => Text;
}

public static class SqlTokenizer
{
    public static List<Tok> Tokenize(string sql, ScriptDialect d)
    {
        var list = new List<Tok>();
        int n = sql.Length, i = 0;
        while (i < n)
        {
            char c = sql[i], next = i + 1 < n ? sql[i + 1] : '\0';
            int start = i;

            if (char.IsWhiteSpace(c))
            {
                while (i < n && char.IsWhiteSpace(sql[i])) i++;
                list.Add(new Tok(TokKind.Space, sql[start..i])); continue;
            }
            if ((c == '-' && next == '-') || (c == '#' && d == ScriptDialect.MySql))
            {
                while (i < n && sql[i] != '\n') i++;
                list.Add(new Tok(TokKind.Comment, sql[start..i])); continue;
            }
            if (c == '/' && next == '*')
            {
                var e = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? n : e + 2;
                list.Add(new Tok(TokKind.Comment, sql[start..i])); continue;
            }
            // Prefixed strings: N'..' E'..' X'..' B'..' and Oracle q'[..]'
            if ((c is 'N' or 'n' or 'E' or 'e' or 'X' or 'x' or 'B' or 'b') && next == '\'' && (i == 0 || !IsWordChar(sql[i - 1])))
            {
                i = ReadString(sql, i + 1, backslash: c is 'E' or 'e' || d == ScriptDialect.MySql);
                list.Add(new Tok(TokKind.String, sql[start..i])); continue;
            }
            if ((c is 'q' or 'Q') && next == '\'' && d == ScriptDialect.Oracle && i + 2 < n)
            {
                var open = sql[i + 2];
                var close = open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open };
                var e = sql.IndexOf(close + "'", i + 3, StringComparison.Ordinal); i = e < 0 ? n : e + 2;
                list.Add(new Tok(TokKind.String, sql[start..i])); continue;
            }
            if (c == '\'')
            {
                i = ReadString(sql, i, backslash: d == ScriptDialect.MySql);
                list.Add(new Tok(TokKind.String, sql[start..i])); continue;
            }
            if (c == '"' || (c == '[' && d == ScriptDialect.SqlServer) || (c == '`' && d == ScriptDialect.MySql))
            {
                var close = c == '[' ? ']' : c;
                int j = i + 1;
                while (j < n) { if (sql[j] == close) { if (j + 1 < n && sql[j + 1] == close) { j += 2; continue; } break; } j++; }
                i = Math.Min(n, j + 1);
                list.Add(new Tok(TokKind.QuotedIdent, sql[start..i])); continue;
            }
            if (c == '$' && d == ScriptDialect.Postgres)
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                if (j < n && sql[j] == '$' && (j == i + 1 || !char.IsDigit(sql[i + 1])))
                {
                    var tag = sql[i..(j + 1)];
                    var e = sql.IndexOf(tag, j + 1, StringComparison.Ordinal); i = e < 0 ? n : e + tag.Length;
                    list.Add(new Tok(TokKind.Dollar, sql[start..i])); continue;
                }
            }
            if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
            {
                while (i < n && (char.IsDigit(sql[i]) || sql[i] == '.')) i++;
                if (i < n && (sql[i] == 'e' || sql[i] == 'E') && i + 1 < n && (char.IsDigit(sql[i + 1]) || ((sql[i + 1] == '+' || sql[i + 1] == '-') && i + 2 < n && char.IsDigit(sql[i + 2]))))
                {
                    i += 2; while (i < n && char.IsDigit(sql[i])) i++;
                }
                list.Add(new Tok(TokKind.Number, sql[start..i])); continue;
            }
            if (char.IsLetter(c) || c == '_' || (c == '#' && d == ScriptDialect.SqlServer))
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$' || sql[i] == '#')) i++;
                list.Add(new Tok(TokKind.Word, sql[start..i])); continue;
            }
            if (c == ':' && next != ':' && next != '=' && (char.IsLetter(next) || char.IsDigit(next)) && (i == 0 || sql[i - 1] != ':'))
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                list.Add(new Tok(TokKind.BindVar, sql[start..i])); continue;
            }
            if (c == '@' && d == ScriptDialect.SqlServer && (char.IsLetter(next) || next == '@' || next == '_'))
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '@' || sql[i] == '#' || sql[i] == '$')) i++;
                list.Add(new Tok(TokKind.AtVar, sql[start..i])); continue;
            }
            if (c == '(' && d == ScriptDialect.Oracle && next == '+' && i + 2 < n && sql[i + 2] == ')')
            {
                i += 3; list.Add(new Tok(TokKind.Symbol, "(+)")); continue;
            }
            var two = i + 1 < n ? sql.Substring(i, 2) : "";
            if (two is "::" or ":=" or "||" or "<>" or "!=" or "<=" or ">=" or "=>" or "->")
            {
                i += 2; list.Add(new Tok(TokKind.Symbol, two)); continue;
            }
            i++;
            list.Add(new Tok(TokKind.Symbol, c.ToString()));
        }
        return list;
    }

    private static int ReadString(string sql, int quoteAt, bool backslash)
    {
        int n = sql.Length, j = quoteAt + 1;
        while (j < n)
        {
            if (backslash && sql[j] == '\\') { j += 2; continue; }
            if (sql[j] == '\'') { if (j + 1 < n && sql[j + 1] == '\'') { j += 2; continue; } return j + 1; }
            j++;
        }
        return n;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

    public static string Render(IEnumerable<Tok> toks)
    {
        var sb = new StringBuilder();
        foreach (var t in toks) sb.Append(t.Text);
        return sb.ToString();
    }
}
