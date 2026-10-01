using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Sql;

// A value written as a SQL literal the target engine accepts for a column of
// the given canonical type (used by generated data scripts).
public static class SqlLiterals
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(object? v, CanonicalType t, ScriptDialect d)
    {
        if (v is null || v is DBNull) return "NULL";
        switch (v)
        {
            case bool b:
                return d == ScriptDialect.Postgres ? (b ? "TRUE" : "FALSE") : (b ? "1" : "0");
            case sbyte or byte or short or ushort or int or uint or long or ulong or decimal:
                return Convert.ToString(v, Inv)!;
            case double f:
                return double.IsFinite(f) ? f.ToString("R", Inv) : Str(f.ToString(Inv), d);
            case float f:
                return float.IsFinite(f) ? f.ToString("R", Inv) : Str(f.ToString(Inv), d);
            case DateTime dt:
                return Date(dt, t, d);
            case DateTimeOffset dto:
                var s = dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", Inv);
                return d == ScriptDialect.Oracle ? $"TO_TIMESTAMP_TZ('{s}', 'YYYY-MM-DD HH24:MI:SS.FFTZH:TZM')" : Str(s, d);
            case TimeSpan ts:
                return Str(ts.ToString(@"hh\:mm\:ss\.FFFFFFF", Inv).TrimEnd('.'), d);
            case byte[] bytes:
                var hex = Convert.ToHexString(bytes);
                return d switch
                {
                    ScriptDialect.Postgres => $"'\\x{hex}'::bytea",
                    ScriptDialect.SqlServer => bytes.Length == 0 ? "0x" : "0x" + hex,
                    ScriptDialect.Oracle => bytes.Length == 0 ? "NULL" : $"HEXTORAW('{hex}')",
                    _ => $"X'{hex}'",
                };
            case Guid g:
                return Str(g.ToString("D"), d);
            default:
                return Str(ValueCoercer.ToText(v), d);
        }
    }

    private static string Date(DateTime dt, CanonicalType t, ScriptDialect d)
    {
        var dateOnly = t == CanonicalType.Date || (dt.TimeOfDay == TimeSpan.Zero && t != CanonicalType.DateTime);
        var day = dt.ToString("yyyy-MM-dd", Inv);
        var full = dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", Inv).TrimEnd('.');
        return d switch
        {
            ScriptDialect.Oracle => dateOnly ? $"DATE '{day}'" : $"TO_TIMESTAMP('{full}', 'YYYY-MM-DD HH24:MI:SS.FF')",
            // datetime2 literal converts to date / datetime / smalldatetime on assignment.
            ScriptDialect.SqlServer => dateOnly ? $"'{day}'" : $"CAST('{dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", Inv)}' AS datetime2)",
            _ => dateOnly ? $"'{day}'" : $"'{full}'",
        };
    }

    public static string Str(string s, ScriptDialect d) => d switch
    {
        ScriptDialect.SqlServer => "N'" + s.Replace("'", "''") + "'",
        // MySQL treats backslash as an escape inside '' unless NO_BACKSLASH_ESCAPES.
        ScriptDialect.MySql => "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'",
        // Oracle string literals stop at 4000 bytes: build long text as CLOB pieces.
        ScriptDialect.Oracle when s.Length > 1000 => string.Join(" || ", Chunks(s, 1000).Select(c => "TO_CLOB('" + c.Replace("'", "''") + "')")),
        _ => "'" + s.Replace("'", "''") + "'",
    };

    private static IEnumerable<string> Chunks(string s, int n)
    {
        for (int i = 0; i < s.Length; i += n) yield return s.Substring(i, Math.Min(n, s.Length - i));
    }
}
