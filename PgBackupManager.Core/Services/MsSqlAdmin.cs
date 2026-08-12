using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public sealed record MsSqlFile(string LogicalName, string PhysicalName, string Type);

// SQL Server counterpart to DatabaseAdmin — everything here talks straight
// T-SQL over Microsoft.Data.SqlClient, so (unlike the Postgres side) there is
// no external client-tool version to be mismatched against the server.
public static class MsSqlAdmin
{
    // The profile's own Database is irrelevant for these catalog-level checks —
    // connect to "master" instead, same role "postgres" plays for DatabaseAdmin.
    private static string MasterConnectionString(ConnectionProfile profile, string plaintextPassword)
    {
        var csb = new SqlConnectionStringBuilder(profile.BuildConnectionString(plaintextPassword))
        {
            InitialCatalog = "master"
        };
        return csb.ConnectionString;
    }

    public static async Task<int?> GetServerMajorVersionAsync(
        ConnectionProfile profile, string plaintextPassword, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(MasterConnectionString(profile, plaintextPassword));
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT SERVERPROPERTY('ProductMajorVersion')", conn);
            var raw = await cmd.ExecuteScalarAsync(ct);
            return raw is null or System.DBNull ? null : int.Parse(raw.ToString()!);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> DatabaseExistsAsync(
        ConnectionProfile profile, string plaintextPassword, string databaseName, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(MasterConnectionString(profile, plaintextPassword));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT 1 FROM sys.databases WHERE name = @n", conn);
        cmd.Parameters.AddWithValue("@n", databaseName);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    public static async Task<IReadOnlyList<string>> ListDatabasesAsync(
        ConnectionProfile profile, string plaintextPassword, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(MasterConnectionString(profile, plaintextPassword));
        await conn.OpenAsync(ct);
        // database_id > 4 skips master/tempdb/model/msdb.
        await using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name", conn);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    public static async Task<(string DataPath, string LogPath)> GetDefaultDataLogPathsAsync(
        ConnectionProfile profile, string plaintextPassword, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(MasterConnectionString(profile, plaintextPassword));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512)), " +
            "CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(512))", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct)) return (reader.GetString(0), reader.GetString(1));
        return ("", "");
    }

    // Physical file paths of an EXISTING target database, so an overwrite
    // restore can MOVE onto the same files in place instead of orphaning them.
    public static async Task<IReadOnlyList<MsSqlFile>> GetExistingFilePathsAsync(
        ConnectionProfile profile, string plaintextPassword, string databaseName, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(MasterConnectionString(profile, plaintextPassword));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT name, physical_name, type_desc FROM sys.master_files WHERE database_id = DB_ID(@n)", conn);
        cmd.Parameters.AddWithValue("@n", databaseName);
        var list = new List<MsSqlFile>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new MsSqlFile(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }
}
