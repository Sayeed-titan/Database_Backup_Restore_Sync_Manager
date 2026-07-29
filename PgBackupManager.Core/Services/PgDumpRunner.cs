using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public sealed class PgDumpRunner
{
    public ProcessRunner Process { get; } = new();

    public async Task<int> RunAsync(
        string pgDumpExe,
        BackupJob job,
        string plaintextPassword,
        CancellationToken ct = default)
    {
        var folder = Path.GetDirectoryName(job.FullOutputPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var args = new List<string>
        {
            $"--host={job.Host}",
            $"--port={job.Port}",
            $"--username={job.Username}",
            $"--dbname={job.Database}",
            $"--file={job.FullOutputPath}",
            $"--format={job.FormatChar}",
            "--verbose",
            "--no-password",
        };

        if (job.Content == DumpContent.SchemaOnly) args.Add("--schema-only");
        if (job.Content == DumpContent.DataOnly) args.Add("--data-only");

        foreach (var s in job.IncludeSchemas) args.Add($"--schema=\"{s}\"");
        foreach (var t in job.IncludeTables) args.Add($"--table={QuoteQualifiedName(t)}");

        var env = new Dictionary<string, string> { ["PGPASSWORD"] = plaintextPassword };
        return await Process.RunAsync(pgDumpExe, args, env, ct: ct);
    }

    // Splits on the LAST dot so a schema name that itself contains a dot
    // (e.g. "singularity.membership") isn't misread as database.schema.
    private static string QuoteQualifiedName(string fullName)
    {
        var idx = fullName.LastIndexOf('.');
        return idx < 0
            ? $"\"{fullName}\""
            : $"\"{fullName[..idx]}\".\"{fullName[(idx + 1)..]}\"";
    }
}
