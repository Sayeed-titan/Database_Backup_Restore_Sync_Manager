using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Npgsql;
using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Sql;

namespace PgBackupManager.Core.Services;

public sealed class StatementResult
{
    public int Index { get; init; }
    public int Line { get; init; }
    public string Sql { get; init; } = "";
    public List<DataTable> Tables { get; } = new();
    public bool Truncated { get; set; }
    public long RowsAffected { get; set; } = -1;
    public TimeSpan Elapsed { get; set; }
    public string? Error { get; set; }
    public bool Skipped { get; set; }
    public bool Ok => Error is null;

    public string Summary => Error != null ? $"ERROR (line {Line}): {Error}"
        : Skipped ? $"skipped (client command): {FirstLine}"
        : Tables.Count > 0 ? $"{Tables.Sum(t => t.Rows.Count):N0} row(s){(Truncated ? " (truncated)" : "")} in {Elapsed.TotalMilliseconds:N0} ms"
        : RowsAffected >= 0 ? $"{RowsAffected:N0} row(s) affected in {Elapsed.TotalMilliseconds:N0} ms"
        : $"done in {Elapsed.TotalMilliseconds:N0} ms";

    public string FirstLine => Sql.Split('\n')[0].Trim() is var l && l.Length > 80 ? l[..80] + "…" : Sql.Split('\n')[0].Trim();
}

// Runs a whole script (any engine, any dialect) on ONE open connection so
// session state — temp tables, SET search_path, open transactions — carries
// across statements exactly like psql/SSMS/SQL*Plus.
public sealed class QueryExecutor
{
    public event EventHandler<string>? Message;
    public event EventHandler<StatementResult>? StatementCompleted;

    public int MaxRows { get; set; } = 5000;
    public int MaxCellChars { get; set; } = 10000;
    public bool StopOnError { get; set; } = true;

    public async Task<List<StatementResult>> RunScriptAsync(DbConnection conn, IDbProvider provider, string script, DbTransaction? tx, CancellationToken ct = default)
    {
        var statements = SqlScriptSplitter.SplitDetailed(script, provider.Dialect);
        var results = new List<StatementResult>();

        void OnPgNotice(object? s, NpgsqlNoticeEventArgs e) => Message?.Invoke(this, $"{e.Notice.Severity}: {e.Notice.MessageText}");
        void OnSqlInfo(object? s, SqlInfoMessageEventArgs e) => Message?.Invoke(this, e.Message);
        if (conn is NpgsqlConnection pg) pg.Notice += OnPgNotice;
        if (conn is SqlConnection ms) ms.InfoMessage += OnSqlInfo;
        try
        {
            for (int i = 0; i < statements.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var st = statements[i];
                var r = new StatementResult { Index = i, Line = st.StartLine, Sql = st.Text };
                var sw = Stopwatch.StartNew();
                try
                {
                    switch (st.Kind)
                    {
                        case StatementKind.Meta:
                            r.Skipped = true;
                            break;
                        case StatementKind.CopyIn when conn is NpgsqlConnection npg:
                            {
                                long rows = 0;
                                await using (var w = await npg.BeginTextImportAsync(st.Text, ct))
                                {
                                    foreach (var line in (st.CopyData ?? "").Split('\n'))
                                    {
                                        if (line.Length == 0) continue;
                                        await w.WriteAsync(line.TrimEnd('\r') + "\n");
                                        rows++;
                                    }
                                }
                                r.RowsAffected = rows;
                                break;
                            }
                        default:
                            await ExecuteOneAsync(conn, tx, st.Text, r, ct);
                            break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { r.Error = ex.Message; }
                r.Elapsed = sw.Elapsed;
                results.Add(r);
                StatementCompleted?.Invoke(this, r);
                if (r.Error != null && StopOnError) break;
            }
        }
        finally
        {
            if (conn is NpgsqlConnection pg2) pg2.Notice -= OnPgNotice;
            if (conn is SqlConnection ms2) ms2.InfoMessage -= OnSqlInfo;
        }
        return results;
    }

    private async Task ExecuteOneAsync(DbConnection conn, DbTransaction? tx, string sql, StatementResult r, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        if (cmd is Oracle.ManagedDataAccess.Client.OracleCommand oc) { oc.InitialLOBFetchSize = -1; oc.InitialLONGFetchSize = -1; oc.BindByName = true; }
        await using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { } });

        var reader = await cmd.ExecuteReaderAsync(ct);
        try
        {
        do
        {
            if (reader.FieldCount == 0) continue;
            var dt = new DataTable();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < reader.FieldCount; c++)
            {
                var n = reader.GetName(c);
                if (string.IsNullOrWhiteSpace(n)) n = $"column{c + 1}";
                var unique = n; int k = 2;
                while (!names.Add(unique)) unique = $"{n}_{k++}";
                // Display-oriented: every cell becomes a readable string so the
                // grid never chokes on provider-specific types (arrays, LOBs, intervals...).
                dt.Columns.Add(unique.Replace(".", "·"), typeof(string));
            }
            if (MaxRows > 0)
            {
                while (await reader.ReadAsync(ct))
                {
                    if (dt.Rows.Count >= MaxRows) { r.Truncated = true; try { cmd.Cancel(); } catch { } break; }
                    var row = new object[reader.FieldCount];
                    for (int c = 0; c < reader.FieldCount; c++) row[c] = Display(reader, c);
                    dt.Rows.Add(row);
                }
            }
            r.Tables.Add(dt);
            if (r.Truncated) break;
        } while (await reader.NextResultAsync(ct));
        if (!r.Truncated && reader.RecordsAffected >= 0) r.RowsAffected = reader.RecordsAffected;
        }
        finally
        {
            // After a truncating Cancel() some drivers throw "canceled" while
            // closing the reader — expected, and the rows we kept are fine.
            try { await reader.DisposeAsync(); }
            catch when (r.Truncated) { }
        }
    }

    public const string NullText = "NULL";

    private object Display(DbDataReader reader, int c)
    {
        if (reader.IsDBNull(c)) return NullText;
        object v;
        try { v = reader.GetValue(c); }
        catch (Exception ex) { return $"<unreadable: {ex.Message}>"; }
        var s = v switch
        {
            byte[] b => b.Length > 64 ? "0x" + Convert.ToHexString(b, 0, 64) + $"… ({b.Length:N0} bytes)" : "0x" + Convert.ToHexString(b),
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF"),
            _ => ValueCoercer.ToText(v),
        };
        return s.Length > MaxCellChars ? s[..MaxCellChars] + $"… ({s.Length:N0} chars)" : s;
    }
}
