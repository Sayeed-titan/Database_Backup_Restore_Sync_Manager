using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Sql;

public sealed class ConvertContext
{
    public string? SourceSchema { get; init; }
    public string? TargetSchema { get; init; }
    public NameCase NameCase { get; init; } = NameCase.TargetDefault;
    public string? ObjectName { get; init; }
    // Oracle packages: true = one PG schema per package (calls like
    // pkg.proc(...) keep working unchanged); false = targetSchema.pkg_proc.
    public bool PackageAsSchema { get; init; } = true;
    // Upper-cased names (NAME or PKG.NAME) known to be procedures, so calls
    // to them become CALL rather than PERFORM.
    public HashSet<string> KnownProcedures { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ConversionResult(string Sql, List<string> Warnings);

// Assisted SQL / procedural-code translation between engines. It handles the
// mechanical 80-90% (types, built-in functions, block structure, packages,
// triggers, sequences) and flags whatever it can't do safely as WARNINGs and
// "TODO(convert)" comments for a human to finish. It never claims a
// translation is complete — review before applying to production.
public static class SqlCodeConverter
{
    public static ConversionResult Convert(string source, DbEngine from, DbEngine to, ConvertContext? ctx = null)
    {
        ctx ??= new ConvertContext();
        if (string.IsNullOrWhiteSpace(source)) return new ConversionResult("", new List<string>());

        if (from == to) return new ConversionResult(RenameSchema(source, from, ctx), new List<string>());

        if (to == DbEngine.PostgreSql)
        {
            return from switch
            {
                DbEngine.Oracle => new OracleToPostgres(ctx).Convert(source),
                DbEngine.SqlServer => new TSqlToPostgres(ctx).Convert(source),
                DbEngine.MySql => new MySqlToPostgres(ctx).Convert(source),
                _ => new ConversionResult(RenameSchema(source, from, ctx), new List<string> { "SQLite -> PostgreSQL: copied with schema rename only; SQLite SQL is mostly ANSI." }),
            };
        }

        if (from == DbEngine.PostgreSql) return new PostgresToOther(ctx, to).Convert(source);

        // X -> Y with neither being PostgreSQL: go through PostgreSQL.
        var step1 = Convert(source, from, DbEngine.PostgreSql, ctx);
        var step2 = new PostgresToOther(ctx, to).Convert(step1.Sql);
        var warnings = step1.Warnings.Concat(step2.Warnings).Append($"Converted {from} -> PostgreSQL -> {to} (two steps); review carefully.").Distinct().ToList();
        return new ConversionResult(step2.Sql, warnings);
    }

    public static IReadOnlyList<DbEngine> SupportedTargets(DbEngine from) =>
        Enum.GetValues<DbEngine>().Where(e => e != from).ToList();

    // Same engine: only the schema qualifier changes.
    internal static string RenameSchema(string source, DbEngine engine, ConvertContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.SourceSchema) || string.IsNullOrEmpty(ctx.TargetSchema) ||
            string.Equals(ctx.SourceSchema, ctx.TargetSchema, StringComparison.Ordinal)) return source;
        var d = DbProviders.For(engine).Dialect;
        var toks = SqlTokenizer.Tokenize(source, d);
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (!t.IsIdent || !string.Equals(t.Ident, ctx.SourceSchema, StringComparison.OrdinalIgnoreCase)) continue;
            var nx = TokenOps.NextSig(toks, i);
            if (nx < 0 || !toks[nx].IsSym(".")) continue;
            toks[i] = new Tok(t.Kind, t.Kind == TokKind.QuotedIdent ? QuoteLike(t.Text[0], ctx.TargetSchema) : ctx.TargetSchema);
        }
        return SqlTokenizer.Render(toks);
    }

    private static string QuoteLike(char open, string name) => open switch
    {
        '[' => "[" + name + "]",
        '`' => "`" + name + "`",
        _ => "\"" + name + "\"",
    };

    internal static string Header(string from, string to, IEnumerable<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- Converted {from} -> {to} by PgBackupManager (assisted conversion — review before use)");
        foreach (var w in warnings.Distinct()) sb.AppendLine("-- WARNING: " + w);
        return sb.ToString();
    }
}
