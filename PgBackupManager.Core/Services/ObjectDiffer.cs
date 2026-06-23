using System;
using System.Collections.Generic;
using System.Linq;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public enum DiffStatus { Existing, NewInBackup, MissingFromBackup }

public sealed record DiffEntry(string Kind, string Schema, string Name, DiffStatus Status, int? DumpId)
{
    public string FullName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

public static class ObjectDiffer
{
    private static readonly HashSet<string> RealKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCHEMA", "TABLE", "VIEW", "FUNCTION", "PROCEDURE", "TYPE", "SEQUENCE", "DOMAIN", "MATERIALIZED VIEW"
    };

    public static IReadOnlyList<DiffEntry> Diff(IEnumerable<TocEntry> backup, IEnumerable<DbObject> live)
    {
        var backupItems = new Dictionary<string, TocEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in backup)
        {
            if (!RealKinds.Contains(e.Kind)) continue;
            // pg_restore --list prints SCHEMA rows with schema "-"; normalize so they
            // match the live side (which keys a schema by its own name).
            var sch = NormalizeSchema(e.Kind, e.Schema, e.Name);
            var key = Key(e.Kind, sch, e.Name);
            backupItems[key] = e;
        }

        var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in live)
        {
            var kind = MapKind(o.Kind);
            if (kind == null) continue;
            liveKeys.Add(Key(kind, o.Schema, o.Name));
        }

        var results = new List<DiffEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, entry) in backupItems)
        {
            seen.Add(key);
            var status = liveKeys.Contains(key) ? DiffStatus.Existing : DiffStatus.NewInBackup;
            var sch = NormalizeSchema(entry.Kind, entry.Schema, entry.Name);
            results.Add(new DiffEntry(entry.Kind, sch, entry.Name, status, entry.DumpId));
        }

        foreach (var key in liveKeys.Where(k => !seen.Contains(k)))
        {
            var (kind, schema, name) = SplitKey(key);
            results.Add(new DiffEntry(kind, schema, name, DiffStatus.MissingFromBackup, null));
        }

        return results
            .OrderBy(d => d.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeSchema(string kind, string schema, string name)
    {
        // A SCHEMA object lives "in itself": group/key it by its own name, not "-".
        if (kind.Equals("SCHEMA", StringComparison.OrdinalIgnoreCase)) return name;
        return schema;
    }

    private static string Key(string kind, string schema, string name) => $"{kind.ToUpperInvariant()}::{schema}::{name}";

    private static (string Kind, string Schema, string Name) SplitKey(string key)
    {
        var parts = key.Split("::", 3);
        return (parts[0], parts[1], parts[2]);
    }

    private static string? MapKind(DbObjectKind k) => k switch
    {
        DbObjectKind.Schema => "SCHEMA",
        DbObjectKind.Table => "TABLE",
        DbObjectKind.View => "VIEW",
        DbObjectKind.Function => "FUNCTION",
        DbObjectKind.Procedure => "PROCEDURE",
        DbObjectKind.Type => "TYPE",
        DbObjectKind.Sequence => "SEQUENCE",
        _ => null
    };
}
