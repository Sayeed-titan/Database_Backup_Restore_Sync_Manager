using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

// One row from RESTORE FILELISTONLY — the file layout recorded INSIDE the
// backup, not whatever exists (or doesn't) on this server.
public sealed record MsSqlFileListEntry(string LogicalName, string PhysicalName, string Type);

// SQL Server analogue of BackupInspector: read-only metadata pulled straight
// from the backup file's own header, via RESTORE HEADERONLY / FILELISTONLY —
// no restore happens here.
public sealed record MsSqlBackupHeader(
    string DatabaseName,
    DateTime BackupFinishDate,
    int CompatibilityLevel,
    long BackupSizeBytes,
    IReadOnlyList<MsSqlFileListEntry> Files);

public sealed record MsSqlRestoreResult(bool Ok, string Message);

public sealed class MsSqlRestoreRunner
{
    public event EventHandler<string>? LogLine;

    // Connects to "master" — same reasoning as MsSqlAdmin: RESTORE/BACKUP
    // metadata calls aren't scoped to any particular user database.
    private static string MasterConnectionString(ConnectionProfile profile, string plaintextPassword)
    {
        var csb = new SqlConnectionStringBuilder(profile.BuildConnectionString(plaintextPassword)) { InitialCatalog = "master" };
        return csb.ConnectionString;
    }

    public static async Task<MsSqlBackupHeader> InspectAsync(
        ConnectionProfile profile, string plaintextPassword, string backupFile, CancellationToken ct = default)
    {
        var escapedPath = backupFile.Replace("'", "''");
        await using var conn = new SqlConnection(MasterConnectionString(profile, plaintextPassword));
        await conn.OpenAsync(ct);

        string dbName; DateTime finishDate; int compatLevel; long size;
        await using (var cmd = new SqlCommand($"RESTORE HEADERONLY FROM DISK = N'{escapedPath}'", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Backup file has no backup sets — could not read header.");
            dbName = reader.GetString(reader.GetOrdinal("DatabaseName"));
            finishDate = reader.GetDateTime(reader.GetOrdinal("BackupFinishDate"));
            // Exact numeric column types (tinyint/smallint/int/numeric) vary across
            // SQL Server versions — convert loosely instead of using a typed
            // getter that throws InvalidCastException on a mismatch.
            compatLevel = Convert.ToInt32(reader.GetValue(reader.GetOrdinal("CompatibilityLevel")));
            size = Convert.ToInt64(reader.GetValue(reader.GetOrdinal("BackupSize")));
        }

        var files = new List<MsSqlFileListEntry>();
        await using (var cmd = new SqlCommand($"RESTORE FILELISTONLY FROM DISK = N'{escapedPath}'", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            var lIdx = reader.GetOrdinal("LogicalName");
            var pIdx = reader.GetOrdinal("PhysicalName");
            var tIdx = reader.GetOrdinal("Type");
            while (await reader.ReadAsync(ct))
                files.Add(new MsSqlFileListEntry(reader.GetString(lIdx), reader.GetString(pIdx), reader.GetString(tIdx)));
        }

        return new MsSqlBackupHeader(dbName, finishDate, compatLevel, size, files);
    }

    public async Task<MsSqlRestoreResult> RunAsync(
        ConnectionProfile profile, string plaintextPassword, MsSqlRestoreOptions opts, CancellationToken ct = default)
    {
        try
        {
            Log($">> inspecting '{Path.GetFileName(opts.BackupFile)}'...");
            var header = await InspectAsync(profile, plaintextPassword, opts.BackupFile, ct);
            Log($"  original database: '{header.DatabaseName}' · backed up {header.BackupFinishDate:yyyy-MM-dd HH:mm} · {header.Files.Count} file(s)");

            var targetExists = await MsSqlAdmin.DatabaseExistsAsync(profile, plaintextPassword, opts.TargetDatabase, ct);
            if (targetExists && !opts.OverwriteExisting)
                return new MsSqlRestoreResult(false,
                    $"Database '{opts.TargetDatabase}' already exists. Tick 'Overwrite existing' to replace it, or restore under a different name.");

            var moves = await ResolveMoveTargetsAsync(profile, plaintextPassword, opts.TargetDatabase, header.Files, targetExists, ct);
            foreach (var (logical, physical) in moves) Log($"  MOVE '{logical}' -> '{physical}'");

            await using var conn = new SqlConnection(MasterConnectionString(profile, plaintextPassword));
            conn.FireInfoMessageEventOnUserErrors = false;
            conn.InfoMessage += OnInfoMessage;
            await conn.OpenAsync(ct);

            var safeTarget = opts.TargetDatabase.Replace("]", "]]");
            var escapedPath = opts.BackupFile.Replace("'", "''");
            var withParts = new List<string>();
            if (opts.OverwriteExisting) withParts.Add("REPLACE");
            foreach (var (logical, physical) in moves)
                withParts.Add($"MOVE N'{logical.Replace("'", "''")}' TO N'{physical.Replace("'", "''")}'");
            withParts.Add("STATS = 5");

            var sql = $"RESTORE DATABASE [{safeTarget}] FROM DISK = N'{escapedPath}' WITH {string.Join(", ", withParts)}";
            Log($">> {sql}");

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
            await using (ct.Register(() => { try { cmd.Cancel(); } catch { } }))
                await cmd.ExecuteNonQueryAsync(ct);

            Log(">> SUCCESS");
            return new MsSqlRestoreResult(true, $"Restored into '{opts.TargetDatabase}'.");
        }
        catch (OperationCanceledException)
        {
            Log(">> Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log($">> ERROR: {ex.Message}");
            return new MsSqlRestoreResult(false, ex.Message);
        }
    }

    // Overwriting an existing DB reuses its current physical file paths (matched
    // positionally against the backup's file list, mirroring what was verified
    // manually against sys.master_files this session); a fresh target gets fresh
    // paths under the server's default data/log directories.
    private static async Task<List<(string Logical, string Physical)>> ResolveMoveTargetsAsync(
        ConnectionProfile profile, string pwd, string targetDatabase,
        IReadOnlyList<MsSqlFileListEntry> sourceFiles, bool targetExists, CancellationToken ct)
    {
        var result = new List<(string, string)>();

        if (targetExists)
        {
            var existing = await MsSqlAdmin.GetExistingFilePathsAsync(profile, pwd, targetDatabase, ct);
            for (int i = 0; i < sourceFiles.Count; i++)
            {
                var physical = i < existing.Count ? existing[i].PhysicalName : sourceFiles[i].PhysicalName;
                result.Add((sourceFiles[i].LogicalName, physical));
            }
            return result;
        }

        var (dataPath, logPath) = await MsSqlAdmin.GetDefaultDataLogPathsAsync(profile, pwd, ct);
        int dataIdx = 0, logIdx = 0;
        foreach (var f in sourceFiles)
        {
            var isLog = f.Type.Equals("L", StringComparison.OrdinalIgnoreCase);
            var basePath = isLog ? logPath : dataPath;
            var ext = isLog ? "ldf" : (dataIdx == 0 ? "mdf" : "ndf");
            var suffix = isLog ? (logIdx == 0 ? "_log" : $"_log{logIdx}") : (dataIdx == 0 ? "" : $"_{dataIdx}");
            result.Add((f.LogicalName, Path.Combine(basePath, $"{targetDatabase}{suffix}.{ext}")));
            if (isLog) logIdx++; else dataIdx++;
        }
        return result;
    }

    private void OnInfoMessage(object? sender, SqlInfoMessageEventArgs e)
    {
        foreach (SqlError err in e.Errors) Log(err.Message);
    }

    private void Log(string line) => LogLine?.Invoke(this, line);
}
