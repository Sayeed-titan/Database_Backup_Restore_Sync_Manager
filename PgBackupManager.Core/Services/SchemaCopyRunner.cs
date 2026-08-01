using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

// Clones a schema (every table, view, function, sequence, type — plus all
// data) into a NEW schema name, within the same database. PostgreSQL has no
// native "CREATE SCHEMA x AS COPY OF y" — the standard technique is: dump the
// source schema as plain SQL, rewrite every reference to the source schema's
// name to the new name, then replay that SQL back into the same database.
public sealed class SchemaCopyOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string SourceSchema { get; set; } = "";
    public string NewSchemaName { get; set; } = "";
}

public sealed class SchemaCopyRunner
{
    // Two separate ProcessRunners (dump, then restore) so a caller can wire up
    // live log lines for each stage exactly like PgDumpRunner/PgRestoreRunner.
    public ProcessRunner DumpProcess { get; } = new();
    public ProcessRunner RestoreProcess { get; } = new();

    public async Task<int> RunAsync(
        string pgDumpExe, string psqlExe, SchemaCopyOptions opts, string plaintextPassword, CancellationToken ct = default)
    {
        var dumpFile = Path.Combine(Path.GetTempPath(), $"schemacopy_{Guid.NewGuid():N}.sql");
        var renamedFile = Path.Combine(Path.GetTempPath(), $"schemacopy_{Guid.NewGuid():N}_renamed.sql");
        var env = new Dictionary<string, string> { ["PGPASSWORD"] = plaintextPassword };

        try
        {
            var dumpArgs = new List<string>
            {
                $"--host={opts.Host}",
                $"--port={opts.Port}",
                $"--username={opts.Username}",
                $"--dbname={opts.Database}",
                $"--file={dumpFile}",
                "--format=plain",
                $"--schema=\"{opts.SourceSchema}\"",
                "--no-owner",
                "--no-privileges",
                "--verbose",
                "--no-password",
            };
            var dumpExit = await DumpProcess.RunAsync(pgDumpExe, dumpArgs, env, ct: ct);
            if (dumpExit != 0) return dumpExit;

            RenameSchema(dumpFile, renamedFile, opts.SourceSchema, opts.NewSchemaName);

            var restoreArgs = new List<string>
            {
                $"--host={opts.Host}",
                $"--port={opts.Port}",
                $"--username={opts.Username}",
                $"--dbname={opts.Database}",
                "--no-password",
                "--no-psqlrc",
                "--single-transaction",
                "-v", "ON_ERROR_STOP=1",
                $"--file={renamedFile}",
            };
            return await RestoreProcess.RunAsync(psqlExe, restoreArgs, env, ct: ct);
        }
        finally
        {
            TryDelete(dumpFile);
            TryDelete(renamedFile);
        }
    }

    // Renames every occurrence of the source schema's name to the new name,
    // streamed line-by-line rather than loaded whole into memory (a schema's
    // dump can run hundreds of MB). Word-boundary-safe so e.g. renaming
    // "dcci_migration" never touches "dcci_migration_2023".
    //
    // Critically SKIPS raw rows inside "COPY ... FROM stdin;" blocks — that's
    // user data, not SQL, and confirmed empirically (see the schema-copy
    // feature's dev notes) that a table column can legitimately contain the
    // schema's name as literal text. A blind find-replace would silently
    // corrupt that data instead of just renaming the schema.
    internal static void RenameSchema(string inputPath, string outputPath, string sourceSchema, string newSchema)
    {
        var pattern = new Regex($@"\b{Regex.Escape(sourceSchema)}\b", RegexOptions.Compiled);
        var copyHeaderRx = new Regex(@"^COPY\s", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var inCopyData = false;

        using var reader = new StreamReader(inputPath);
        using var writer = new StreamWriter(outputPath);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (inCopyData)
            {
                writer.WriteLine(line);
                if (line == @"\.") inCopyData = false;
                continue;
            }

            var renamed = pattern.Replace(line, newSchema);
            writer.WriteLine(renamed);

            if (copyHeaderRx.IsMatch(renamed)) inCopyData = true;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
