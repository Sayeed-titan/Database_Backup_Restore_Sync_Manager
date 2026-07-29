using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

public sealed record TocEntry(int DumpId, int CatalogOid, int ObjectOid, string Kind, string Schema, string Name, string Owner, string RawLine)
{
    public string FullName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

public static class BackupInspector
{
    private static readonly Regex LineRx = new(@"^(\d+);\s+(\d+)\s+(\d+)\s+(.+)$", RegexOptions.Compiled);

    public static IReadOnlyList<TocEntry> Parse(IEnumerable<string> lines)
    {
        var list = new List<TocEntry>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';')) continue;
            var m = LineRx.Match(line);
            if (!m.Success) continue;
            var rest = m.Groups[4].Value.Trim();

            var ownerIdx = rest.LastIndexOf(' ');
            if (ownerIdx < 0) continue;
            var owner = rest[(ownerIdx + 1)..];
            var head = rest[..ownerIdx].TrimEnd();

            // FUNCTION/PROCEDURE/AGGREGATE entries embed an argument signature
            // directly after the name (e.g. "func(text, text)" or even
            // "func(timestamp with time zone)"), and that signature can contain
            // spaces of its own. Splitting the whole line on spaces shifts
            // schema/name/kind off by however many extra words the signature
            // has, and even in the simple case leaves "(args)" stuck onto the
            // name — which then never matches the bare proname read back from
            // the live database. Split at the signature's opening paren first.
            var parenIdx = head.IndexOf('(');
            string schema, name, kind;
            if (parenIdx >= 0)
            {
                var beforeTokens = head[..parenIdx].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (beforeTokens.Length < 2) continue;
                name = beforeTokens[^1];
                schema = beforeTokens[^2];
                kind = string.Join(' ', beforeTokens, 0, beforeTokens.Length - 2);
            }
            else
            {
                var tokens = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3) continue;
                name = tokens[^1];
                schema = tokens[^2];
                kind = string.Join(' ', tokens, 0, tokens.Length - 2);
            }

            list.Add(new TocEntry(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value),
                kind, schema, name, owner, line));
        }
        return list;
    }

    public static async Task<IReadOnlyList<TocEntry>> InspectAsync(string pgRestoreExe, string backupFile, CancellationToken ct = default)
    {
        if (!File.Exists(pgRestoreExe))
            throw new FileNotFoundException("pg_restore not found", pgRestoreExe);
        if (!File.Exists(backupFile) && !Directory.Exists(backupFile))
            throw new FileNotFoundException("Backup file not found", backupFile);

        var psi = new ProcessStartInfo(pgRestoreExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--list");
        psi.ArgumentList.Add(backupFile);

        using var proc = Process.Start(psi)!;
        var lines = new List<string>();
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            lines.Add(line);

        await proc.WaitForExitAsync(ct);
        return Parse(lines);
    }
}
