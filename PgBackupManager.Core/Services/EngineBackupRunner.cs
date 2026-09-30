using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Services;

public sealed record EngineBackupResult(bool Ok, string Message, string? OutputPath);

// Native backup/restore for the engines that don't have their own tab:
//   SQLite  — online backup API (no tool needed), both directions
//   MySQL   — mysqldump (if installed) to .sql; restore replays the .sql in-app
//   Oracle  — Data Pump expdp/impdp (Instant Client tools); the .dmp lives in a
//             server-side DIRECTORY object, as Data Pump requires.
public sealed class EngineBackupRunner
{
    public event EventHandler<string>? LogLine;
    private void Log(string s) => LogLine?.Invoke(this, s);

    // --------------------------------------------------------------- SQLite

    public Task<EngineBackupResult> SqliteBackupAsync(ConnectionProfile p, string outFile, CancellationToken ct = default) => Task.Run(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        using var src = new SqliteConnection(p.BuildConnectionString(""));
        src.Open();
        using var dst = new SqliteConnection($"Data Source={outFile}");
        dst.Open();
        Log($">> SQLite online backup {p.Database} -> {outFile}");
        src.BackupDatabase(dst);
        Log(">> SUCCESS");
        return new EngineBackupResult(true, "SQLite backup complete.", outFile);
    }, ct);

    public Task<EngineBackupResult> SqliteRestoreAsync(string backupFile, ConnectionProfile target, CancellationToken ct = default) => Task.Run(() =>
    {
        using var src = new SqliteConnection($"Data Source={backupFile};Mode=ReadOnly");
        src.Open();
        using var dst = new SqliteConnection(target.BuildConnectionString(""));
        dst.Open();
        Log($">> SQLite restore {backupFile} -> {target.Database} (target contents are replaced)");
        src.BackupDatabase(dst);
        Log(">> SUCCESS");
        return new EngineBackupResult(true, "SQLite restore complete.", target.Database);
    }, ct);

    // ---------------------------------------------------------------- MySQL

    public async Task<EngineBackupResult> MySqlDumpAsync(ConnectionProfile p, string? schema, string outFile, bool schemaOnly, CancellationToken ct = default)
    {
        var exe = DependencyManager.Find("mysqldump.exe", Array.Empty<string>()) ?? DependencyManager.Scan().First(t => t.Key == "mysql").Path
                  ?? throw new InvalidOperationException("mysqldump.exe not found — install the MySQL/MariaDB client (Settings > Dependencies) or use Transfer to copy into a SQLite file instead.");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        // Password goes in a temp option file, never on the command line (visible in the process list).
        var cnf = Path.Combine(Path.GetTempPath(), $"pgbm_{Guid.NewGuid():N}.cnf");
        await File.WriteAllTextAsync(cnf, $"[client]\npassword=\"{SecretProtector.Unprotect(p.EncryptedPasswordBase64).Replace("\"", "\\\"")}\"\n", ct);
        try
        {
            var args = new List<string>
            {
                $"--defaults-extra-file={cnf}", $"--host={p.Host}", $"--port={p.Port}", $"--user={p.Username}",
                "--single-transaction", "--routines", "--triggers", "--events", "--hex-blob", "--default-character-set=utf8mb4",
                $"--result-file={outFile}", "--databases", schema ?? p.Database,
            };
            if (schemaOnly) args.Insert(4, "--no-data");
            var pr = new ProcessRunner();
            pr.StdoutLine += (_, l) => Log(l);
            pr.StderrLine += (_, l) => Log(l);
            Log($">> mysqldump {schema ?? p.Database} -> {outFile}");
            var code = await pr.RunAsync(exe, args, ct: ct);
            return code == 0 ? new EngineBackupResult(true, "MySQL dump complete.", outFile) : new EngineBackupResult(false, $"mysqldump exited with code {code}.", outFile);
        }
        finally { try { File.Delete(cnf); } catch { } }
    }

    // Restores any .sql script (mysqldump output included) in-app.
    public async Task<EngineBackupResult> RunSqlFileAsync(ConnectionProfile target, string sqlFile, CancellationToken ct = default)
    {
        var provider = DbProviders.For(target);
        await using var conn = await provider.OpenAsync(target, ct);
        var exec = new QueryExecutor { MaxRows = 0, StopOnError = false };
        int ok = 0, failed = 0;
        exec.StatementCompleted += (_, r) =>
        {
            if (r.Ok) ok++; else { failed++; Log($"  line {r.Line}: {r.Error}"); }
            if ((ok + failed) % 200 == 0) Log($"  … {ok + failed:N0} statement(s)");
        };
        exec.Message += (_, m) => Log(m);
        Log($">> running {sqlFile} on {target} ({provider.Name})");
        var text = await File.ReadAllTextAsync(sqlFile, ct);
        await exec.RunScriptAsync(conn, provider, text, null, ct);
        Log($">> {ok:N0} statement(s) OK, {failed:N0} failed");
        return new EngineBackupResult(failed == 0, $"{ok:N0} statement(s) OK, {failed:N0} failed.", null);
    }

    // --------------------------------------------------------------- Oracle

    public async Task<EngineBackupResult> OracleDataPumpAsync(ConnectionProfile p, bool export, string directoryObject, string dumpFile,
        string? schemas, string? remapSchema, string? tableExistsAction, CancellationToken ct = default)
    {
        var tool = export ? "expdp.exe" : "impdp.exe";
        var exe = DependencyManager.Find(tool, DependencyManager.OracleSearchDirs())
                  ?? throw new InvalidOperationException($"{tool} not found — download Oracle Instant Client from Settings > Dependencies.");
        var pwd = SecretProtector.Unprotect(p.EncryptedPasswordBase64);
        var connect = p.OracleUseSid
            ? $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={p.Host})(PORT={p.Port}))(CONNECT_DATA=(SID={p.Database})))"
            : $"//{p.Host}:{p.Port}/{p.Database}";
        var par = Path.Combine(Path.GetTempPath(), $"pgbm_{Guid.NewGuid():N}.par");
        var lines = new List<string>
        {
            $"userid='{p.Username}/\"{pwd.Replace("\"", "")}\"@{connect}'",
            $"directory={directoryObject}",
            $"dumpfile={dumpFile}",
            $"logfile={Path.GetFileNameWithoutExtension(dumpFile)}_{(export ? "exp" : "imp")}.log",
        };
        if (!string.IsNullOrWhiteSpace(schemas)) lines.Add($"schemas={schemas}");
        if (!export && !string.IsNullOrWhiteSpace(remapSchema)) lines.Add($"remap_schema={remapSchema}");
        if (!export && !string.IsNullOrWhiteSpace(tableExistsAction)) lines.Add($"table_exists_action={tableExistsAction}");
        await File.WriteAllLinesAsync(par, lines, ct);
        try
        {
            var pr = new ProcessRunner();
            pr.StdoutLine += (_, l) => Log(l);
            pr.StderrLine += (_, l) => Log(l);
            Log($">> {tool} directory={directoryObject} dumpfile={dumpFile}{(schemas != null ? " schemas=" + schemas : "")}");
            var env = new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(exe) + ";" + Environment.GetEnvironmentVariable("PATH") };
            var code = await pr.RunAsync(exe, new[] { $"parfile={par}" }, env, ct: ct);
            // Data Pump returns 5 for "completed with warnings".
            return code is 0 or 5
                ? new EngineBackupResult(true, $"Data Pump {(export ? "export" : "import")} finished{(code == 5 ? " with warnings" : "")}. File is on the SERVER in directory {directoryObject}.", dumpFile)
                : new EngineBackupResult(false, $"{tool} exited with code {code}.", dumpFile);
        }
        finally { try { File.Delete(par); } catch { } }
    }

    // Lists DIRECTORY objects the user can see (for the Data Pump picker).
    public static async Task<List<(string Name, string Path)>> OracleDirectoriesAsync(ConnectionProfile p, CancellationToken ct = default)
    {
        var provider = DbProviders.For(p);
        await using var conn = await provider.OpenAsync(p, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT directory_name, directory_path FROM all_directories ORDER BY directory_name";
        var list = new List<(string, string)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        return list;
    }
}
