using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

public sealed class PsqlRestoreOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string SqlFile { get; set; } = "";

    // psql itself supports --single-transaction (wraps the whole script in
    // BEGIN/COMMIT), matching pg_restore's option of the same name.
    public bool SingleTransaction { get; set; } = true;
}

// Executes a plain-text SQL dump (one pg_restore can't read at all — no TOC)
// by feeding it straight to psql, statement by statement.
public sealed class PsqlRestoreRunner
{
    public ProcessRunner Process { get; } = new();

    public async Task<int> RunAsync(string psqlExe, PsqlRestoreOptions opts, string plaintextPassword, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            $"--host={opts.Host}",
            $"--port={opts.Port}",
            $"--username={opts.Username}",
            $"--dbname={opts.Database}",
            "--no-password",
            // Stop at the first error instead of plowing on through a script
            // that's already failed — mirrors --single-transaction's rollback
            // safety net for the (uncommon) case that flag is off.
            "--variable=ON_ERROR_STOP=1",
        };
        if (opts.SingleTransaction) args.Add("--single-transaction");
        args.Add($"--file={opts.SqlFile}");

        var env = new Dictionary<string, string> { ["PGPASSWORD"] = plaintextPassword };
        return await Process.RunAsync(psqlExe, args, env, ct: ct);
    }
}
