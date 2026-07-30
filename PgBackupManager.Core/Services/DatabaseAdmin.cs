using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

public static class DatabaseAdmin
{
    /// <summary>
    /// Connects to the maintenance DB ("postgres") on the same host and creates the
    /// profile's target database if it does not already exist.
    /// </summary>
    public static async Task<(bool Ok, string Message)> CreateDatabaseAsync(
        ConnectionProfile profile, string plaintextPassword, CancellationToken ct = default)
    {
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(profile.BuildConnectionString(plaintextPassword))
            {
                Database = "postgres"
            };

            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(ct);

            await using (var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", conn))
            {
                check.Parameters.AddWithValue("n", profile.Database);
                var exists = await check.ExecuteScalarAsync(ct);
                if (exists != null)
                    return (true, $"Database \"{profile.Database}\" already exists on {profile.Host}.");
            }

            // DB name cannot be parameterized; quote it to be safe.
            var safeName = profile.Database.Replace("\"", "\"\"");
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{safeName}\"", conn))
                await create.ExecuteNonQueryAsync(ct);

            return (true, $"Created database \"{profile.Database}\" on {profile.Host}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Creates any of the given schemas that don't already exist in the profile's
    /// target database. pg_restore's --schema filter only selects objects BELONGING
    /// TO the named schema — never the CREATE SCHEMA statement itself (confirmed via
    /// pg_restore --list: a --schema filter never returns a SCHEMA-type TOC entry,
    /// regardless of name). A schema-scoped restore into a fresh target would
    /// otherwise fail on its very first CREATE TABLE with "schema does not exist",
    /// so this must run before pg_restore whenever schemas are being filtered.
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnsureSchemasExistAsync(
        ConnectionProfile profile, string plaintextPassword, IEnumerable<string> schemas, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(profile.BuildConnectionString(plaintextPassword));
            await conn.OpenAsync(ct);
            foreach (var schema in schemas)
            {
                var safeName = schema.Replace("\"", "\"\"");
                await using var cmd = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS \"{safeName}\"", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Lists database names available on the host (via the maintenance DB).</summary>
    public static async Task<IReadOnlyList<string>> ListDatabasesAsync(
        ConnectionProfile profile, string plaintextPassword, CancellationToken ct = default)
    {
        var csb = new NpgsqlConnectionStringBuilder(profile.BuildConnectionString(plaintextPassword))
        {
            Database = "postgres"
        };
        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }
}
