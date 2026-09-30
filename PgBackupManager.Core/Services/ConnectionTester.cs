using System;
using System.Threading.Tasks;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Services;

public sealed record TestResult(bool Ok, string Message, string? ServerVersion, TimeSpan Elapsed);

public static class ConnectionTester
{
    public static async Task<TestResult> TestAsync(ConnectionProfile profile, string plaintextPassword)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var provider = DbProviders.For(profile);
            await using var conn = provider.CreateConnection(profile.BuildConnectionString(plaintextPassword));
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = profile.Engine switch
            {
                DbEngine.SqlServer => "SELECT @@VERSION",
                DbEngine.Oracle => "SELECT banner FROM v$version WHERE ROWNUM = 1",
                DbEngine.MySql => "SELECT CONCAT('MySQL/MariaDB ', VERSION())",
                DbEngine.Sqlite => "SELECT 'SQLite ' || sqlite_version()",
                _ => "SELECT version()",
            };
            string? version;
            try { version = (await cmd.ExecuteScalarAsync())?.ToString(); }
            catch { version = $"{provider.Name} {conn.ServerVersion}"; } // e.g. no access to v$version
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
