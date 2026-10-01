using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Sql;

namespace PgBackupManager.Core.Services;

public enum CompareStatus { OnlyInSource, OnlyInTarget, Different, Same }

public sealed record SchemaDiffItem(string ObjectType, string Name, string? Child, CompareStatus Status, string Detail, string? SourceDef = null, string? TargetDef = null)
{
    public string Display => Child == null ? Name : $"{Name}.{Child}";
}

public sealed class SchemaCompareOptions
{
    public required ConnectionProfile Source { get; init; }
    public required ConnectionProfile Target { get; init; }
    public required string SourceSchema { get; init; }
    public required string TargetSchema { get; init; }
    public NameCase NameCase { get; init; } = NameCase.TargetDefault;
    public bool CompareCode { get; init; } = true;
    // Same engine only: also compare view/routine definitions text.
    public bool CompareDefinitions { get; init; } = true;
}

public sealed class SchemaCompareResult
{
    public List<SchemaDiffItem> Items { get; } = new();
    public int TablesCompared { get; set; }
    public int Count(CompareStatus s) => Items.Count(i => i.Status == s);
}

// Structural diff between two schemas on ANY two connections (same or
// different engines), plus a script that makes the target look like the
// source. The script is only generated — never executed here.
public sealed class SchemaCompareRunner
{
    public event EventHandler<string>? Progress;

    private sealed class Snapshot
    {
        public Dictionary<string, TableInfo> Tables = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<IndexInfo>> Indexes = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<ForeignKeyInfo>> Fks = new(StringComparer.OrdinalIgnoreCase);
        public List<DbObjectInfo> Code = new();
        public Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<SchemaCompareResult> CompareAsync(SchemaCompareOptions o, CancellationToken ct = default)
    {
        var sp = DbProviders.For(o.Source);
        var tp = DbProviders.For(o.Target);
        bool sameEngine = sp.Engine == tp.Engine;
        string Map(string n) => NameCasing.Apply(n, o.NameCase, tp.NormalizeName);

        Progress?.Invoke(this, "reading both schemas…");
        var readCode = o.CompareCode && sameEngine && o.CompareDefinitions;
        var srcTask = ReadAsync(sp, o.Source, o.SourceSchema, readCode, "source", ct);
        var tgtTask = ReadAsync(tp, o.Target, o.TargetSchema, readCode, "target", ct);
        await Task.WhenAll(srcTask, tgtTask);
        var s = srcTask.Result; var t = tgtTask.Result;

        var r = new SchemaCompareResult();
        // ----- tables
        var tgtByName = t.Tables.Values.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var st in s.Tables.Values.OrderBy(x => x.Name))
        {
            var mapped = Map(st.Name);
            if (!tgtByName.TryGetValue(mapped, out var tt) && !tgtByName.TryGetValue(st.Name, out tt))
            {
                r.Items.Add(new SchemaDiffItem("Table", st.Name, null, CompareStatus.OnlyInSource, $"{st.Columns.Count} column(s)", sp.BuildCreateTable(o.SourceSchema, st)));
                continue;
            }
            r.TablesCompared++;
            tgtByName.Remove(tt.Name);
            CompareColumns(r, st, tt, sameEngine, sp, tp, o);
            var sPk = string.Join(",", st.PrimaryKey.Select(x => Map(x).ToLowerInvariant()));
            var tPk = string.Join(",", tt.PrimaryKey.Select(x => x.ToLowerInvariant()));
            if (sPk != tPk)
                r.Items.Add(new SchemaDiffItem("Primary key", st.Name, null, CompareStatus.Different, $"({sPk}) vs ({tPk})", sPk, tPk));

            string IxKey(IndexInfo i, Func<string, string> m) => (i.Unique ? "U:" : "I:") + string.Join(",", i.Columns.Select(c => m(c).ToLowerInvariant()));
            var sIx = s.Indexes.GetValueOrDefault(st.Name) ?? new();
            var tIx = t.Indexes.GetValueOrDefault(tt.Name) ?? new();
            foreach (var ix in sIx.Where(i => !tIx.Any(x => IxKey(x, n => n) == IxKey(i, Map))))
                r.Items.Add(new SchemaDiffItem("Index", st.Name, ix.Name, CompareStatus.OnlyInSource, $"{(ix.Unique ? "unique " : "")}({string.Join(", ", ix.Columns)})"));
            foreach (var ix in tIx.Where(i => !sIx.Any(x => IxKey(x, Map) == IxKey(i, n => n))))
                r.Items.Add(new SchemaDiffItem("Index", st.Name, ix.Name, CompareStatus.OnlyInTarget, $"{(ix.Unique ? "unique " : "")}({string.Join(", ", ix.Columns)})"));

            string FkKey(ForeignKeyInfo f, Func<string, string> m) => string.Join(",", f.Columns.Select(c => m(c).ToLowerInvariant())) + "->" + m(f.RefTable).ToLowerInvariant() + "(" + string.Join(",", f.RefColumns.Select(c => m(c).ToLowerInvariant())) + ")";
            var sFk = s.Fks.GetValueOrDefault(st.Name) ?? new();
            var tFk = t.Fks.GetValueOrDefault(tt.Name) ?? new();
            foreach (var fk in sFk.Where(f => !tFk.Any(x => FkKey(x, n => n) == FkKey(f, Map))))
                r.Items.Add(new SchemaDiffItem("Foreign key", st.Name, fk.Name, CompareStatus.OnlyInSource, $"({string.Join(", ", fk.Columns)}) → {fk.RefTable}({string.Join(", ", fk.RefColumns)}) {fk.OnDelete}"));
            foreach (var fk in tFk.Where(f => !sFk.Any(x => FkKey(x, Map) == FkKey(f, n => n))))
                r.Items.Add(new SchemaDiffItem("Foreign key", st.Name, fk.Name, CompareStatus.OnlyInTarget, $"({string.Join(", ", fk.Columns)}) → {fk.RefTable}({string.Join(", ", fk.RefColumns)})"));
        }
        foreach (var tt in tgtByName.Values.OrderBy(x => x.Name))
            r.Items.Add(new SchemaDiffItem("Table", tt.Name, null, CompareStatus.OnlyInTarget, $"{tt.Columns.Count} column(s)", null, tp.BuildCreateTable(o.TargetSchema, tt)));

        // ----- code objects
        if (o.CompareCode)
        {
            string CodeKey(DbObjectInfo x, Func<string, string> m) => x.Type + ":" + m(Regex.Replace(x.Name, @"\(.*$", "")).ToLowerInvariant();
            var tCode = t.Code.ToDictionary(x => CodeKey(x, n => n), StringComparer.OrdinalIgnoreCase);
            foreach (var so in s.Code.OrderBy(x => x.Type).ThenBy(x => x.Name))
            {
                if (!tCode.Remove(CodeKey(so, Map), out var to))
                {
                    r.Items.Add(new SchemaDiffItem(so.Type.ToString(), so.Name, null, CompareStatus.OnlyInSource, "", s.Sources.GetValueOrDefault(so.Name)));
                    continue;
                }
                if (!readCode) continue;
                var a = Normalize(s.Sources.GetValueOrDefault(so.Name), o.SourceSchema);
                var b = Normalize(t.Sources.GetValueOrDefault(to.Name), o.TargetSchema);
                if (a != null && b != null && a != b)
                    r.Items.Add(new SchemaDiffItem(so.Type.ToString(), so.Name, null, CompareStatus.Different, "definition differs", s.Sources[so.Name], t.Sources[to.Name]));
            }
            foreach (var to in tCode.Values.OrderBy(x => x.Type).ThenBy(x => x.Name))
                r.Items.Add(new SchemaDiffItem(to.Type.ToString(), to.Name, null, CompareStatus.OnlyInTarget, "", null, t.Sources.GetValueOrDefault(to.Name)));

            // Code missing in the target: fetch its source (any engine) so the sync script can convert it.
            var codeTypes = Enum.GetNames<DbObjectType>().Where(n => n != nameof(DbObjectType.Table)).ToHashSet();
            var missing = r.Items.Where(i => i.Status == CompareStatus.OnlyInSource && codeTypes.Contains(i.ObjectType) && i.SourceDef == null).ToList();
            if (missing.Count > 0)
            {
                await using var c = await sp.OpenAsync(o.Source, ct);
                foreach (var i in missing)
                {
                    var obj = s.Code.FirstOrDefault(x => x.Name == i.Name && x.Type.ToString() == i.ObjectType);
                    if (obj == null) continue;
                    string? src = null;
                    try { src = await sp.GetObjectSourceAsync(c, obj, ct); } catch { }
                    if (src != null) r.Items[r.Items.IndexOf(i)] = i with { SourceDef = src };
                }
            }
        }
        Progress?.Invoke(this, $"done — {r.TablesCompared} table(s) in both, {r.Count(CompareStatus.OnlyInSource)} only in source, {r.Count(CompareStatus.OnlyInTarget)} only in target, {r.Count(CompareStatus.Different)} different");
        _last = (o, s, t, r);
        return r;
    }

    private void CompareColumns(SchemaCompareResult r, TableInfo st, TableInfo tt, bool sameEngine, IDbProvider sp, IDbProvider tp, SchemaCompareOptions o)
    {
        string Map(string n) => NameCasing.Apply(n, o.NameCase, tp.NormalizeName);
        foreach (var sc in st.Columns)
        {
            var tc = tt.Columns.FirstOrDefault(c => string.Equals(c.Name, Map(sc.Name), StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, sc.Name, StringComparison.OrdinalIgnoreCase));
            if (tc == null)
            {
                r.Items.Add(new SchemaDiffItem("Column", st.Name, sc.Name, CompareStatus.OnlyInSource, $"{sc.NativeType}{(sc.Nullable ? "" : " not null")}"));
                continue;
            }
            var typeDiff = sameEngine
                ? !string.Equals(Norm(sc.NativeType), Norm(tc.NativeType), StringComparison.OrdinalIgnoreCase)
                : sc.Type != tc.Type
                  || (sc.Type is CanonicalType.String or CanonicalType.FixedString && sc.Length != tc.Length)
                  || (sc.Type == CanonicalType.Decimal && (sc.Precision != tc.Precision || (sc.Scale ?? 0) != (tc.Scale ?? 0)));
            if (typeDiff)
                r.Items.Add(new SchemaDiffItem("Column", st.Name, sc.Name, CompareStatus.Different,
                    sameEngine ? $"type {sc.NativeType} vs {tc.NativeType}" : $"type {sc.NativeType} ({TypeFacts.Describe(sc)}) vs {tc.NativeType} ({TypeFacts.Describe(tc)})", sc.NativeType, tc.NativeType));
            if (sc.Nullable != tc.Nullable && !TypeFacts.IsKey(st, sc))
                r.Items.Add(new SchemaDiffItem("Column", st.Name, sc.Name, CompareStatus.Different, sc.Nullable ? "nullable vs NOT NULL" : "NOT NULL vs nullable"));
        }
        foreach (var tc in tt.Columns.Where(tc => !st.Columns.Any(sc => string.Equals(Map(sc.Name), tc.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(sc.Name, tc.Name, StringComparison.OrdinalIgnoreCase))))
            r.Items.Add(new SchemaDiffItem("Column", st.Name, tc.Name, CompareStatus.OnlyInTarget, $"{tc.NativeType}{(tc.Nullable ? "" : " not null")}"));
    }

    private static string Norm(string native) => Regex.Replace(native, @"\s+", " ").Trim();

    private static string? Normalize(string? src, string schema) =>
        src == null ? null : Regex.Replace(Regex.Replace(src, Regex.Escape(schema) + @"\.", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim().TrimEnd(';').ToLowerInvariant();

    private async Task<Snapshot> ReadAsync(IDbProvider p, ConnectionProfile profile, string schema, bool readSources, string side, CancellationToken ct)
    {
        var snap = new Snapshot();
        await using var c = await p.OpenAsync(profile, ct);
        var objs = await p.ListObjectsAsync(c, schema, ct);
        var tables = objs.Where(x => x.Type == DbObjectType.Table).ToList();
        int n = 0;
        foreach (var o in tables)
        {
            ct.ThrowIfCancellationRequested();
            var t = await p.GetTableAsync(c, schema, o.Name, ct);
            if (t == null) continue;
            snap.Tables[o.Name] = t;
            try { snap.Indexes[o.Name] = await p.GetIndexesAsync(c, schema, o.Name, ct); } catch { snap.Indexes[o.Name] = new(); }
            try { snap.Fks[o.Name] = await p.GetForeignKeysAsync(c, schema, o.Name, ct); } catch { snap.Fks[o.Name] = new(); }
            if (++n % 25 == 0) Progress?.Invoke(this, $"{side}: {n}/{tables.Count} table(s) read…");
        }
        snap.Code = objs.Where(x => x.Type != DbObjectType.Table).ToList();
        if (readSources)
            foreach (var o in snap.Code)
            {
                ct.ThrowIfCancellationRequested();
                try { snap.Sources[o.Name] = await p.GetObjectSourceAsync(c, o, ct) ?? ""; } catch { }
            }
        Progress?.Invoke(this, $"{side}: {snap.Tables.Count} table(s), {snap.Code.Count} code object(s)");
        return snap;
    }

    private (SchemaCompareOptions O, Snapshot S, Snapshot T, SchemaCompareResult R)? _last;

    // DDL (target dialect) that brings the target in line with the source.
    // Destructive steps (drops) are emitted commented out.
    public string BuildSyncScript(IEnumerable<SchemaDiffItem>? only = null)
    {
        if (_last is not { } last) return "-- run a compare first";
        var (o, s, t, r) = last;
        var sp = DbProviders.For(o.Source);
        var tp = DbProviders.For(o.Target);
        string Map(string n) => NameCasing.Apply(n, o.NameCase, tp.NormalizeName);
        var items = (only ?? r.Items).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"-- Sync script: make [{tp.Name}] {o.Target}.{o.TargetSchema} match [{sp.Name}] {o.Source}.{o.SourceSchema}");
        sb.AppendLine($"-- Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Review before running — drops are commented out on purpose.");
        sb.AppendLine();
        var laterFks = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Tgt(string srcTable) => t.Tables.Keys.FirstOrDefault(k => k.Equals(Map(srcTable), StringComparison.OrdinalIgnoreCase) || k.Equals(srcTable, StringComparison.OrdinalIgnoreCase)) ?? Map(srcTable);

        foreach (var i in items)
        {
            switch (i.ObjectType, i.Status)
            {
                case ("Table", CompareStatus.OnlyInSource):
                    {
                        var st = s.Tables[i.Name];
                        var design = new TableInfo { Schema = o.TargetSchema, Name = Map(st.Name), Columns = st.Columns.Select(c => c.WithName(Map(c.Name))).ToList(), PrimaryKey = st.PrimaryKey.Select(Map).ToList() };
                        sb.AppendLine(tp.BuildCreateTable(o.TargetSchema, design) + ";");
                        foreach (var ix in s.Indexes.GetValueOrDefault(st.Name) ?? new())
                            sb.AppendLine(tp.BuildCreateIndex(o.TargetSchema, design, new IndexInfo(TransferRunner.UniqueName(Map(ix.Name), design.Name, tp.MaxIdentifierLength, used), ix.Columns.Select(Map).ToList(), ix.Unique)) + ";");
                        foreach (var fk in s.Fks.GetValueOrDefault(st.Name) ?? new())
                            if (FkDdl(fk, design.Name) is { } f) laterFks.Add(f);
                        sb.AppendLine();
                        break;
                    }
                case ("Table", CompareStatus.OnlyInTarget):
                    sb.AppendLine($"-- DROP TABLE {tp.Qualify(o.TargetSchema, i.Name)};  -- only in target");
                    break;
                case ("Column", CompareStatus.OnlyInSource):
                    {
                        var col = s.Tables[i.Name].Columns.First(c => c.Name == i.Child);
                        if (!col.Nullable) sb.AppendLine("-- note: NOT NULL without a default fails if the table already has rows");
                        sb.AppendLine(tp.BuildAddColumn(o.TargetSchema, Tgt(i.Name), col.WithName(Map(col.Name))) + ";");
                        break;
                    }
                case ("Column", CompareStatus.OnlyInTarget):
                    sb.AppendLine($"-- ALTER TABLE {tp.Qualify(o.TargetSchema, Tgt(i.Name))} DROP COLUMN {tp.Quote(i.Child!)};  -- only in target");
                    break;
                case ("Column", CompareStatus.Different):
                    {
                        var col = s.Tables[i.Name].Columns.First(c => c.Name == i.Child).WithName(Map(i.Child!));
                        if (i.Detail.StartsWith("type"))
                            sb.AppendLine(tp.BuildAlterColumnType(o.TargetSchema, Tgt(i.Name), col) is { } alter ? alter + ";" : $"-- {tp.Name} can't change a column type in place ({i.Name}.{i.Child}: {i.Detail}) — rebuild the table");
                        else if (tp.Engine == DbEngine.PostgreSql)
                            sb.AppendLine($"ALTER TABLE {tp.Qualify(o.TargetSchema, Tgt(i.Name))} ALTER COLUMN {tp.Quote(col.Name)} {(col.Nullable ? "DROP" : "SET")} NOT NULL;");
                        else if (tp.BuildAlterColumnType(o.TargetSchema, Tgt(i.Name), col) is { } alter2 && tp.Engine is DbEngine.SqlServer or DbEngine.MySql)
                            sb.AppendLine(alter2 + ";");
                        else
                            sb.AppendLine($"-- change nullability of {i.Name}.{i.Child} by hand ({i.Detail})");
                        break;
                    }
                case ("Index", CompareStatus.OnlyInSource):
                    {
                        var ix = s.Indexes[i.Name].First(x => x.Name == i.Child);
                        var tt = t.Tables.GetValueOrDefault(Tgt(i.Name)) ?? s.Tables[i.Name];
                        sb.AppendLine(tp.BuildCreateIndex(o.TargetSchema, tt, new IndexInfo(TransferRunner.UniqueName(Map(ix.Name), tt.Name, tp.MaxIdentifierLength, used), ix.Columns.Select(Map).ToList(), ix.Unique)) + ";");
                        break;
                    }
                case ("Index", CompareStatus.OnlyInTarget):
                    sb.AppendLine($"-- DROP INDEX {tp.Quote(i.Child!)};  -- only in target ({i.Name})");
                    break;
                case ("Foreign key", CompareStatus.OnlyInSource):
                    {
                        var fk = s.Fks[i.Name].First(x => x.Name == i.Child);
                        if (FkDdl(fk, Tgt(i.Name)) is { } f) laterFks.Add(f);
                        break;
                    }
                case ("Foreign key", CompareStatus.OnlyInTarget):
                    sb.AppendLine($"-- ALTER TABLE {tp.Qualify(o.TargetSchema, Tgt(i.Name))} DROP CONSTRAINT {tp.Quote(i.Child!)};  -- only in target");
                    break;
                case ("Primary key", CompareStatus.Different):
                    sb.AppendLine($"-- primary key of {i.Name} differs: {i.Detail} — change it by hand");
                    break;
                case (_, CompareStatus.OnlyInSource or CompareStatus.Different) when i.SourceDef != null:
                    {
                        var conv = SqlCodeConverter.Convert(i.SourceDef, sp.Engine, tp.Engine, new ConvertContext { SourceSchema = o.SourceSchema, TargetSchema = o.TargetSchema, NameCase = o.NameCase });
                        sb.AppendLine($"-- ===== {i.ObjectType} {i.Name} ({(i.Status == CompareStatus.Different ? "definition differs" : "missing in target")})");
                        sb.AppendLine(Terminate(conv.Sql.Trim(), tp.Dialect)).AppendLine();
                        break;
                    }
                case (_, CompareStatus.OnlyInSource):
                    sb.AppendLine($"-- {i.ObjectType} {i.Name} is missing in target (source text not read — enable definition compare, same engine only, or use Transfer / Converter)");
                    break;
                case (_, CompareStatus.OnlyInTarget):
                    sb.AppendLine($"-- {i.ObjectType} {i.Name} exists only in target");
                    break;
            }
        }
        if (laterFks.Count > 0)
        {
            sb.AppendLine().AppendLine("-- foreign keys last, once every table exists");
            foreach (var f in laterFks) sb.AppendLine(f + ";");
        }
        return sb.ToString();

        // Catalog DDL (e.g. pg_get_functiondef) has no terminator; add the one each engine's splitter expects.
        static string Terminate(string sql, ScriptDialect d)
        {
            var body = sql.TrimEnd();
            if (d == ScriptDialect.SqlServer) return body + "\nGO";
            if (d == ScriptDialect.Oracle)
            {
                // PL/SQL units end with "/" on its own line; plain DDL with ";"
                if (body.EndsWith("/")) return body;
                var plsql = body.Contains("BEGIN", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(body, @"\bEND\b[^;]*;\s*$", RegexOptions.IgnoreCase);
                return plsql ? body + "\n/" : body.TrimEnd(';') + ";";
            }
            return body.EndsWith(";") ? body : body + ";";
        }

        string? FkDdl(ForeignKeyInfo fk, string tgtTable)
        {
            var same = string.Equals(fk.RefSchema, o.SourceSchema, StringComparison.OrdinalIgnoreCase);
            var mapped = fk with
            {
                Name = TransferRunner.UniqueName(Map(fk.Name), tgtTable, tp.MaxIdentifierLength, used),
                Columns = fk.Columns.Select(Map).ToList(),
                RefSchema = same ? o.TargetSchema : Map(fk.RefSchema),
                RefTable = Map(fk.RefTable),
                RefColumns = fk.RefColumns.Select(Map).ToList(),
            };
            return tp.BuildAddForeignKey(o.TargetSchema, tgtTable, mapped) ?? $"-- {tp.Name} can't add foreign key {fk.Name} to an existing table";
        }
    }
}
