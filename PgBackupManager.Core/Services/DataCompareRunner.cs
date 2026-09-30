using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Npgsql;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

// One table's comparison, source (SQL Server) vs. target (Postgres). Column
// and row counts are null when the table doesn't exist on that side at all —
// distinct from "0 rows", which is a real, matchable value.
public sealed record TableCompareRow(
    string Table,
    int? SourceColumns, long? SourceRows,
    int? TargetColumns, long? TargetRows)
{
    public bool ExistsInSource => SourceColumns.HasValue;
    public bool ExistsInTarget => TargetColumns.HasValue;
    public bool ColumnsMatch => ExistsInSource && ExistsInTarget && SourceColumns == TargetColumns;
    public bool RowsMatch => ExistsInSource && ExistsInTarget && SourceRows == TargetRows;
    public bool FullyMatched => ExistsInSource && ExistsInTarget && ColumnsMatch && RowsMatch;
}

public sealed record CompareSummary(
    IReadOnlyList<TableCompareRow> Rows,
    int SourceTableCount, int TargetTableCount,
    long SourceTotalRows, long TargetTotalRows,
    int MatchedCount, int MismatchedCount)
{
    public double MatchedPercent => Rows.Count == 0 ? 0 : Math.Round(100.0 * MatchedCount / Rows.Count, 1);
    public double MismatchedPercent => Rows.Count == 0 ? 0 : Math.Round(100.0 * MismatchedCount / Rows.Count, 1);
}

// Read-only, both sides — never writes to either connection. Used after an
// Import/Sync run (or on demand) to answer "did everything actually land
// correctly": table-by-table column count and real row count, source vs.
// target, with a match/mismatch verdict per table and an overall summary.
public static class DataCompareRunner
{
    public static async Task<CompareSummary> CompareAsync(
        MsSqlSource source, string sourceSchema,
        ConnectionProfile targetProfile, string targetPassword, string targetSchema,
        IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        await using var src = new SqlConnection(source.BuildConnectionString());
        await src.OpenAsync(ct);
        await using var tgt = new NpgsqlConnection(targetProfile.BuildConnectionString(targetPassword));
        await tgt.OpenAsync(ct);

        var rows = new List<TableCompareRow>();
        int srcTableCount = 0, tgtTableCount = 0;
        long srcTotalRows = 0, tgtTotalRows = 0;
        int matched = 0, mismatched = 0;

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();

            var (srcCols, srcRows) = await GetSqlServerStatsAsync(src, sourceSchema, table, ct);
            var (tgtCols, tgtRows) = await GetPostgresStatsAsync(tgt, targetSchema, table, ct);

            if (srcCols.HasValue) { srcTableCount++; srcTotalRows += srcRows ?? 0; }
            if (tgtCols.HasValue) { tgtTableCount++; tgtTotalRows += tgtRows ?? 0; }

            var row = new TableCompareRow(table, srcCols, srcRows, tgtCols, tgtRows);
            rows.Add(row);
            if (row.FullyMatched) matched++; else mismatched++;
        }

        return new CompareSummary(rows, srcTableCount, tgtTableCount, srcTotalRows, tgtTotalRows, matched, mismatched);
    }

    private static async Task<(int? Cols, long? Rows)> GetSqlServerStatsAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string existsSql = "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=@s AND TABLE_NAME=@t";
        await using (var existsCmd = new SqlCommand(existsSql, conn))
        {
            existsCmd.Parameters.AddWithValue("@s", schema);
            existsCmd.Parameters.AddWithValue("@t", table);
            if (await existsCmd.ExecuteScalarAsync(ct) is null) return (null, null);
        }

        const string colSql = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=@s AND TABLE_NAME=@t";
        await using var colCmd = new SqlCommand(colSql, conn);
        colCmd.Parameters.AddWithValue("@s", schema);
        colCmd.Parameters.AddWithValue("@t", table);
        var cols = (int)await colCmd.ExecuteScalarAsync(ct)!;

        // Real COUNT(*) — NOT sys.partitions.rows, which can be stale after
        // deletes until stats/index maintenance runs and has misreported a
        // real table's row count before.
        await using var rowCmd = new SqlCommand($"SELECT COUNT(*) FROM [{schema}].[{table}]", conn);
        var rows = Convert.ToInt64(await rowCmd.ExecuteScalarAsync(ct));

        return (cols, rows);
    }

    private static async Task<(int? Cols, long? Rows)> GetPostgresStatsAsync(NpgsqlConnection conn, string schema, string table, CancellationToken ct)
    {
        // Case-insensitive lookup: depending on how/when a table was created,
        // it may be stored as the SQL Server source's original mixed-case
        // name (quoted identifier) or lowercased — never assume one or the
        // other.
        const string existsSql = "SELECT table_name FROM information_schema.tables WHERE table_schema=@s AND lower(table_name)=lower(@t)";
        string? realName;
        await using (var existsCmd = new NpgsqlCommand(existsSql, conn))
        {
            existsCmd.Parameters.AddWithValue("s", schema);
            existsCmd.Parameters.AddWithValue("t", table);
            var result = await existsCmd.ExecuteScalarAsync(ct);
            if (result is null) return (null, null);
            realName = (string)result;
        }

        const string colSql = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=@s AND table_name=@t";
        await using var colCmd = new NpgsqlCommand(colSql, conn);
        colCmd.Parameters.AddWithValue("s", schema);
        colCmd.Parameters.AddWithValue("t", realName);
        var cols = Convert.ToInt32(await colCmd.ExecuteScalarAsync(ct));

        await using var rowCmd = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"{realName}\"", conn);
        var rows = Convert.ToInt64(await rowCmd.ExecuteScalarAsync(ct));

        return (cols, rows);
    }
}
