using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PgBackupManager.Core.Providers;

// Engine-neutral column type. Every provider maps its native catalog types
// INTO this (reading) and OUT of it (DDL + value binding), so any engine can
// transfer to any other through one path instead of one runner per pair.
public enum CanonicalType
{
    Bool, Int16, Int32, Int64, Decimal, Float32, Float64,
    Date, Time, DateTime, DateTimeOffset,
    String, FixedString, Text,
    Binary, Guid, Json, Xml,
}

public sealed record ColumnInfo(
    string Name,
    string NativeType,
    CanonicalType Type,
    int? Length = null,
    int? Precision = null,
    int? Scale = null,
    bool Nullable = true,
    bool IsIdentity = false)
{
    public ColumnInfo WithName(string name) => this with { Name = name };
}

public sealed class TableInfo
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public List<ColumnInfo> Columns { get; init; } = new();
    public List<string> PrimaryKey { get; init; } = new();
}

// Plain column index (expression / partial / filtered indexes are not carried across engines).
public sealed record IndexInfo(string Name, IReadOnlyList<string> Columns, bool Unique);

public sealed record ForeignKeyInfo(
    string Name,
    IReadOnlyList<string> Columns,
    string RefSchema,
    string RefTable,
    IReadOnlyList<string> RefColumns,
    string OnDelete = "NO ACTION",   // NO ACTION | RESTRICT | CASCADE | SET NULL | SET DEFAULT
    string OnUpdate = "NO ACTION");

public enum DbObjectType { Table, View, Function, Procedure, Package, Sequence, Trigger, Type }

public sealed record DbObjectInfo(string Schema, string Name, DbObjectType Type)
{
    public string Display => Type == DbObjectType.Table ? Name : $"{Name}  ({Type.ToString().ToLowerInvariant()})";
}

public enum NameCase { TargetDefault, Preserve, Lower, Upper }

public static class NameCasing
{
    public static string Apply(string name, NameCase c, Func<string, string> targetDefault) => c switch
    {
        NameCase.Lower => name.ToLowerInvariant(),
        NameCase.Upper => name.ToUpperInvariant(),
        NameCase.Preserve => name,
        _ => targetDefault(name),
    };
}

// Converts whatever a source ADO.NET reader handed back into the .NET type
// the TARGET column's binding expects. Kept in one place because this is where
// cross-engine transfers actually break (Oracle NUMBER -> PG integer, SQLite
// text dates, MySQL tinyint(1) bools, arrays, NUL bytes in strings...).
public static class ValueCoercer
{
    public static object? Coerce(object? v, CanonicalType t)
    {
        if (v is null || v is DBNull) return null;
        try
        {
            return t switch
            {
                CanonicalType.Bool => v switch
                {
                    bool b => b,
                    string s => ParseBool(s),
                    byte[] bytes => bytes.Length > 0 && bytes[0] != 0,
                    _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture) != 0m,
                },
                CanonicalType.Int16 => Convert.ToInt16(v is bool b16 ? (b16 ? 1 : 0) : v, CultureInfo.InvariantCulture),
                CanonicalType.Int32 => Convert.ToInt32(v is bool b32 ? (b32 ? 1 : 0) : v, CultureInfo.InvariantCulture),
                CanonicalType.Int64 => Convert.ToInt64(v is bool b64 ? (b64 ? 1 : 0) : v, CultureInfo.InvariantCulture),
                CanonicalType.Decimal => v is bool bd ? (bd ? 1m : 0m) : Convert.ToDecimal(v, CultureInfo.InvariantCulture),
                CanonicalType.Float32 => Convert.ToSingle(v, CultureInfo.InvariantCulture),
                CanonicalType.Float64 => Convert.ToDouble(v, CultureInfo.InvariantCulture),
                CanonicalType.Date => ToDateTime(v).Date,
                CanonicalType.DateTime => DateTime.SpecifyKind(ToDateTime(v), DateTimeKind.Unspecified),
                CanonicalType.DateTimeOffset => v switch
                {
                    DateTimeOffset dto => dto,
                    string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
                    _ => new DateTimeOffset(DateTime.SpecifyKind(ToDateTime(v), DateTimeKind.Utc)),
                },
                CanonicalType.Time => v switch
                {
                    TimeSpan ts => ts,
                    TimeOnly to => to.ToTimeSpan(),
                    DateTime dt => dt.TimeOfDay,
                    string s => TimeSpan.Parse(s, CultureInfo.InvariantCulture),
                    _ => TimeSpan.Parse(v.ToString()!, CultureInfo.InvariantCulture),
                },
                CanonicalType.Guid => v switch
                {
                    Guid g => g,
                    byte[] { Length: 16 } bytes => new Guid(bytes),
                    _ => Guid.Parse(v.ToString()!),
                },
                CanonicalType.Binary => v switch
                {
                    byte[] bytes => bytes,
                    Guid g => g.ToByteArray(),
                    string s => Encoding.UTF8.GetBytes(s),
                    _ => Encoding.UTF8.GetBytes(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""),
                },
                _ => ToText(v),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidCastException($"Can't convert value '{Preview(v)}' ({v.GetType().Name}) to {t}: {ex.Message}", ex);
        }
    }

    // Text-ish targets: stringify anything, the way a human would expect to
    // read it back (ISO dates, invariant numbers, JSON for arrays).
    public static string ToText(object v)
    {
        var s = v switch
        {
            string str => str,
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            byte[] bytes => "\\x" + Convert.ToHexString(bytes),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            Array arr => ArrayLiteral(arr),
            _ => v.ToString() ?? "",
        };
        // PostgreSQL rejects NUL (0x00) inside text; other engines don't care
        // but nobody wants them either.
        return s.IndexOf('\0') >= 0 ? s.Replace("\0", "") : s;
    }

    // PostgreSQL array literal ("{1,2,"a b"}") — castable straight back into a
    // PG array column, and still readable anywhere else.
    private static string ArrayLiteral(Array arr)
    {
        var parts = new List<string>();
        foreach (var e in arr)
        {
            if (e is null || e is DBNull) { parts.Add("NULL"); continue; }
            var s = e is Array inner ? ArrayLiteral(inner) : ToText(e);
            parts.Add(e is Array || (s.Length > 0 && s.IndexOfAny(new[] { ',', '"', '{', '}', ' ', '\\' }) < 0 && s != "NULL")
                ? s : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
        }
        return "{" + string.Join(",", parts) + "}";
    }

    private static bool ParseBool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "1" or "t" or "true" or "y" or "yes" or "on" => true,
        "0" or "f" or "false" or "n" or "no" or "off" or "" => false,
        _ => throw new FormatException("not a boolean"),
    };

    private static DateTime ToDateTime(object v) => v switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
        long ticksOrUnix => DateTimeOffset.FromUnixTimeSeconds(ticksOrUnix).UtcDateTime,
        _ => Convert.ToDateTime(v, CultureInfo.InvariantCulture),
    };

    private static string Preview(object v)
    {
        var s = v is byte[] b ? $"<{b.Length} bytes>" : v.ToString() ?? "";
        return s.Length > 40 ? s[..40] + "…" : s;
    }
}

public static class TypeFacts
{
    public static bool IsTextual(CanonicalType t) => t is CanonicalType.String or CanonicalType.FixedString or CanonicalType.Text or CanonicalType.Json or CanonicalType.Xml;
    public static bool IsNumeric(CanonicalType t) => t is CanonicalType.Int16 or CanonicalType.Int32 or CanonicalType.Int64 or CanonicalType.Decimal or CanonicalType.Float32 or CanonicalType.Float64;

    // Summary used in logs/previews: "varchar(50) -> String(50)".
    public static string Describe(ColumnInfo c) => c.Type switch
    {
        CanonicalType.String or CanonicalType.FixedString when c.Length is > 0 => $"{c.Type}({c.Length})",
        CanonicalType.Decimal when c.Precision is > 0 => $"Decimal({c.Precision},{c.Scale ?? 0})",
        _ => c.Type.ToString(),
    };

    public static IEnumerable<string> Keys(TableInfo t) => t.PrimaryKey;
    public static bool IsKey(TableInfo t, ColumnInfo c) => t.PrimaryKey.Any(k => string.Equals(k, c.Name, StringComparison.OrdinalIgnoreCase));
}
