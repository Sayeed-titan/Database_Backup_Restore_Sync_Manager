using System;
using System.Collections.Generic;
using System.Linq;

namespace PgBackupManager.Core.Sql;

// Small toolkit for rewriting token lists (find the next real token, match
// parentheses, split call arguments, replace a range).
internal static class TokenOps
{
    public static int NextSig(List<Tok> t, int i)
    {
        for (int k = i + 1; k < t.Count; k++) if (!t[k].IsTrivia) return k;
        return -1;
    }

    public static int PrevSig(List<Tok> t, int i)
    {
        for (int k = i - 1; k >= 0; k--) if (!t[k].IsTrivia) return k;
        return -1;
    }

    public static int FirstSig(List<Tok> t, int from = 0)
    {
        for (int k = from; k < t.Count; k++) if (!t[k].IsTrivia) return k;
        return -1;
    }

    public static int MatchParen(List<Tok> t, int open)
    {
        int depth = 0;
        for (int k = open; k < t.Count; k++)
        {
            if (t[k].IsSym("(")) depth++;
            else if (t[k].IsSym(")")) { depth--; if (depth == 0) return k; }
        }
        return -1;
    }

    // Arguments of a call whose "(" is at open and ")" at close, split on
    // top-level commas. Returned token lists exclude the separators.
    public static List<List<Tok>> SplitArgs(List<Tok> t, int open, int close)
    {
        var args = new List<List<Tok>>();
        var cur = new List<Tok>();
        int depth = 0;
        for (int k = open + 1; k < close; k++)
        {
            var x = t[k];
            if (x.IsSym("(")) depth++;
            else if (x.IsSym(")")) depth--;
            if (depth == 0 && x.IsSym(",")) { args.Add(cur); cur = new List<Tok>(); continue; }
            cur.Add(x);
        }
        if (cur.Count > 0 || args.Count > 0) args.Add(cur);
        return args;
    }

    public static string R(IEnumerable<Tok> toks) => SqlTokenizer.Render(toks).Trim();

    public static void Replace(List<Tok> t, int from, int toInclusive, string text)
    {
        t.RemoveRange(from, toInclusive - from + 1);
        t.Insert(from, new Tok(TokKind.Word, text));
    }

    public static List<Tok> Slice(List<Tok> t, int from, int toExclusive) => t.GetRange(from, Math.Max(0, toExclusive - from));

    // Index of the matching END for a PL/SQL-style block that starts at
    // beginIdx (BEGIN or CASE). END IF / END LOOP don't close blocks;
    // END CASE and plain END do.
    public static int MatchBlockEnd(List<Tok> t, int beginIdx)
    {
        int depth = 0;
        for (int k = beginIdx; k < t.Count; k++)
        {
            var x = t[k];
            if (x.Kind != TokKind.Word) continue;
            if (x.IsWord("BEGIN")) depth++;
            else if (x.IsWord("CASE"))
            {
                var p = PrevSig(t, k);
                if (p >= 0 && t[p].IsWord("END")) continue;
                depth++;
            }
            else if (x.IsWord("END"))
            {
                var nx = NextSig(t, k);
                if (nx >= 0 && t[nx].IsWordAny("IF", "LOOP", "WHILE", "REPEAT", "TRY", "CATCH")) continue;
                depth--;
                if (depth == 0) return k;
            }
        }
        return -1;
    }

    public static bool IsSimpleIdent(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '$' or '#');
}
