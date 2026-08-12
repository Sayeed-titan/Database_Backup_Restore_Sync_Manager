using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Npgsql;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public sealed record TestResult(bool Ok, string Message, string? ServerVersion, TimeSpan Elapsed);

public static class ConnectionTester
{
    public static Task<TestResult> TestAsync(ConnectionProfile profile, string plaintextPassword) => profile.Engine switch
    {
        DbEngine.SqlServer => TestSqlServerAsync(profile, plaintextPassword),
        _ => TestPostgresAsync(profile, plaintextPassword),
    };

    private static async Task<TestResult> TestPostgresAsync(ConnectionProfile profile, string plaintextPassword)
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

    private static async Task<TestResult> TestSqlServerAsync(ConnectionProfile profile, string plaintextPassword)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(profile.BuildConnectionString(plaintextPassword));
            await conn.OpenAsync();

            await using var cmd = new SqlCommand("SELECT @@VERSION", conn);
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
