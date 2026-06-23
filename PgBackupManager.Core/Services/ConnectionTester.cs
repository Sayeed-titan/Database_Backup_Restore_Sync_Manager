using System;
using System.Threading.Tasks;
using Npgsql;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public sealed record TestResult(bool Ok, string Message, string? ServerVersion, TimeSpan Elapsed);

public static class ConnectionTester
{
    public static async Task<TestResult> TestAsync(ConnectionProfile profile, string plaintextPassword)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = new NpgsqlConnection(profile.BuildConnectionString(plaintextPassword));
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand("SELECT version();", conn);
            var version = (await cmd.ExecuteScalarAsync())?.ToString();
            sw.Stop();

            return new TestResult(true, "Connection OK", version, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult(false, ex.Message, null, sw.Elapsed);
        }
    }
}
