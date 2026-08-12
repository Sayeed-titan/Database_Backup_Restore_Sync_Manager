using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public sealed record MsSqlBackupResult(bool Ok, string Message);

// SQL Server's BACKUP DATABASE is plain T-SQL — no external tool to locate,
// unlike PgDumpRunner which shells out to pg_dump.exe. Progress ("N percent
// processed") arrives over the same SqlConnection.InfoMessage channel that
// sqlcmd prints those lines from (confirmed against a live server this
// session) — callers subscribe to LogLine and parse it the same way
// BackupViewModel parses pg_dump's stderr today.
public sealed class MsSqlBackupRunner
{
    public event EventHandler<string>? LogLine;

    public async Task<MsSqlBackupResult> RunAsync(
        ConnectionProfile profile, string plaintextPassword, MsSqlBackupJob job, CancellationToken ct = default)
    {
        try
        {
            var folder = Path.GetDirectoryName(job.FullOutputPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            await using var conn = new SqlConnection(profile.BuildConnectionString(plaintextPassword));
            conn.FireInfoMessageEventOnUserErrors = false;
            conn.InfoMessage += OnInfoMessage;
            await conn.OpenAsync(ct);

            Log($">> BACKUP DATABASE [{job.Database}] TO DISK = '{job.FullOutputPath}'");

            var safeName = job.Database.Replace("]", "]]");
            var escapedPath = job.FullOutputPath.Replace("'", "''");
            var sql = $"BACKUP DATABASE [{safeName}] TO DISK = N'{escapedPath}' WITH STATS = 5";

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
            // Belt-and-braces: ExecuteNonQueryAsync(ct) already wires cancellation
            // in modern Microsoft.Data.SqlClient, but a multi-GB BACKUP is exactly
            // the kind of long-running server-side op worth double-covering.
            await using (ct.Register(() => { try { cmd.Cancel(); } catch { /* already done/disposed */ } }))
                await cmd.ExecuteNonQueryAsync(ct);

            Log(">> SUCCESS");
            return new MsSqlBackupResult(true, "Backup complete.");
        }
        catch (OperationCanceledException)
        {
            Log(">> Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log($">> ERROR: {ex.Message}");
            return new MsSqlBackupResult(false, ex.Message);
        }
    }

    private void OnInfoMessage(object? sender, SqlInfoMessageEventArgs e)
    {
        foreach (SqlError err in e.Errors) Log(err.Message);
    }

    private void Log(string line) => LogLine?.Invoke(this, line);
}
