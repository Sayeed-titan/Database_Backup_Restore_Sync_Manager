using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Services;

// Executes a saved preset with no UI — used by scheduled tasks and the
// "--run-preset" command line. Every run lands in JobHistory with a log file.
public static class HeadlessJobRunner
{
    public static async Task<(bool Ok, string Summary)> RunAsync(JobPreset preset, Action<string>? log = null, bool scheduled = false, CancellationToken ct = default)
    {
        var lines = new List<string>();
        void L(string s) { lines.Add($"[{DateTime.Now:HH:mm:ss}] {s}"); log?.Invoke(s); }
        var sw = Stopwatch.StartNew();
        bool ok; string summary;
        try
        {
            (ok, summary) = preset.Kind switch
            {
                PresetKind.Transfer => await RunTransferAsync(preset.Transfer!, L, ct),
                PresetKind.Backup => await RunBackupAsync(preset.Backup!, L, ct),
                PresetKind.Script => await RunScriptAsync(preset.Script!, L, ct),
                _ => (false, "unknown preset kind"),
            };
        }
        catch (Exception ex)
        {
            ok = false; summary = ex.Message;
            L("ERROR: " + ex);
        }
        L(">> " + summary);
        var logPath = JobHistory.SaveLog(preset.Name, lines);
        JobHistory.Add(preset.Kind.ToString(), preset.Name, summary, ok, sw.Elapsed, logPath, scheduled);
        return (ok, summary);
    }

    private static ConnectionProfile Profile(Guid id) =>
        new ProfileStore().LoadAll().FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException($"connection profile {id} no longer exists.");

    private static async Task<(bool, string)> RunTransferAsync(TransferPreset t, Action<string> L, CancellationToken ct)
    {
        var src = Profile(t.SourceProfileId);
        var tgt = Profile(t.TargetProfileId);
        var tables = t.Tables;
        if (tables.Count == 0)
        {
            var sp = DbProviders.For(src);
            await using var c = await sp.OpenAsync(src, ct);
            tables = (await sp.ListObjectsAsync(c, t.SourceSchema, ct)).Where(o => o.Type == DbObjectType.Table).Select(o => o.Name).ToList();
            L($"preset has no table list — using all {tables.Count} table(s) in {t.SourceSchema}");
        }
        var runner = new TransferRunner();
        runner.LogLine += (_, s) => L(s);
        var r = await runner.RunAsync(new TransferOptions
        {
            Source = src, Target = tgt, SourceSchema = t.SourceSchema, TargetSchema = t.TargetSchema,
            Tables = tables, CodeObjects = t.CodeObjects, Mode = t.Mode, NameCase = t.NameCase,
            DryRun = false, ApplyCode = t.ApplyCode, RowFilter = t.RowFilter, CommitEveryRows = t.CommitEveryRows,
        }, ct);
        return (r.Ok, r.Summary);
    }

    private static async Task<(bool, string)> RunBackupAsync(BackupPreset b, Action<string> L, CancellationToken ct)
    {
        var p = Profile(b.ProfileId);
        var settings = new SettingsStore().Load();
        var root = string.IsNullOrWhiteSpace(b.DestinationRoot) ? settings.DefaultBackupRoot : b.DestinationRoot!;
        var now = DateTime.Now;
        var folder = FilenameBuilder.BuildFolder(root, Path.GetFileNameWithoutExtension(p.Database), b.UseAutoFolders, now);
        var stamp = now.ToString("yyyyMMdd_HHmmss");
        var pwd = SecretProtector.Unprotect(p.EncryptedPasswordBase64);
        (bool, string) result;

        switch (p.Engine)
        {
            case DbEngine.PostgreSql:
                {
                    var tools = PgToolsLocator.Locate(settings.PgBinDirOverride);
                    if (tools.PgDump is null) return (false, "pg_dump not found — set it up in Settings.");
                    var job = new BackupJob
                    {
                        Host = p.Host, Port = p.Port, Database = p.Database, Username = p.Username,
                        Format = b.Format, Scope = b.Scope, Content = b.Content,
                        IncludeSchemas = b.Scope == BackupScope.SpecificSchemas ? b.Schemas : new(),
                        IncludeTables = b.Scope == BackupScope.SpecificTables ? b.Tables : new(),
                        Jobs = b.Format == BackupFormat.Directory ? Math.Max(1, b.Jobs) : 1,
                        DestinationRoot = root, UseAutoFolders = b.UseAutoFolders,
                    };
                    job.FullOutputPath = FilenameBuilder.BuildFullPath(job, now);
                    var runner = new PgDumpRunner();
                    runner.Process.StderrLine += (_, s) => L(s);
                    runner.Process.StdoutLine += (_, s) => L(s);
                    L($">> pg_dump {p.Database} -> {job.FullOutputPath}");
                    var code = await runner.RunAsync(tools.PgDump, job, pwd, ct);
                    result = code == 0 ? (true, $"Backup complete: {job.FullOutputPath}") : (false, $"pg_dump exited with code {code}");
                    break;
                }
            case DbEngine.SqlServer:
                {
                    var job = new MsSqlBackupJob { Host = p.Host, Port = p.Port, Database = p.Database, DestinationRoot = root, UseAutoFolders = b.UseAutoFolders, FullOutputPath = Path.Combine(folder, $"{p.Database}_full_{stamp}.bak") };
                    var runner = new MsSqlBackupRunner();
                    runner.LogLine += (_, s) => L(s);
                    var r = await runner.RunAsync(p, pwd, job, ct);
                    result = (r.Ok, r.Ok ? $"Backup complete: {job.FullOutputPath}" : r.Message);
                    break;
                }
            case DbEngine.Sqlite:
                {
                    var eb = new EngineBackupRunner(); eb.LogLine += (_, s) => L(s);
                    var r = await eb.SqliteBackupAsync(p, Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(p.Database)}_{stamp}.db"), ct);
                    result = (r.Ok, r.Message + " " + r.OutputPath);
                    break;
                }
            case DbEngine.MySql:
                {
                    var eb = new EngineBackupRunner(); eb.LogLine += (_, s) => L(s);
                    var r = await eb.MySqlDumpAsync(p, b.Schemas.FirstOrDefault(), Path.Combine(folder, $"{p.Database}_{stamp}.sql"), b.Content == DumpContent.SchemaOnly, ct);
                    result = (r.Ok, r.Message + " " + r.OutputPath);
                    break;
                }
            default:
                return (false, "Scheduled Oracle backups use Data Pump — run them from the Engine Backup tab (server-side DIRECTORY needed).");
        }

        if (result.Item1 && b.ApplyRetention)
        {
            var deleted = RetentionPolicy.DeleteExpired(root, settings.RetentionDays, DateTime.Now);
            L($">> retention: deleted {deleted} file(s) older than {settings.RetentionDays} days");
        }
        return result;
    }

    private static async Task<(bool, string)> RunScriptAsync(ScriptPreset s, Action<string> L, CancellationToken ct)
    {
        var p = Profile(s.ProfileId);
        var provider = DbProviders.For(p);
        await using var conn = await provider.OpenAsync(p, ct);
        var exec = new QueryExecutor { MaxRows = 0, StopOnError = s.StopOnError };
        exec.Message += (_, m) => L(m);
        exec.StatementCompleted += (_, r) => L($"#{r.Index + 1} line {r.Line}: {r.Summary}");
        var results = await exec.RunScriptAsync(conn, provider, await File.ReadAllTextAsync(s.ScriptPath, ct), null, ct);
        var failed = results.Count(r => !r.Ok);
        return (failed == 0, $"{results.Count} statement(s), {failed} failed.");
    }
}
