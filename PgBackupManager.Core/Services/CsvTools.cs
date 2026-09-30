using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Core.Services;

public enum ExportFormat { Csv, Tsv, Json, SqlInserts }

// CSV/TSV/JSON/INSERT-script export (streamed straight from a query, so it
// writes every row — not just what the grid shows) and CSV import into any
// engine's table (types inferred from a sample, table created if missing).
public static class CsvTools
{
    public static async Task<long> ExportQueryAsync(DbConnection conn, IDbProvider provider, string sql, string path, ExportFormat fmt,
        string insertTableName = "exported", IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        provider.PrepareReadCommand(cmd);
        await using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { } });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
        await using var w = new StreamWriter(path, false, new UTF8Encoding(fmt is ExportFormat.Csv or ExportFormat.Tsv));
        long n = 0;
        switch (fmt)
        {
            case ExportFormat.Csv:
            case ExportFormat.Tsv:
                {
                    var sep = fmt == ExportFormat.Csv ? "," : "\t";
                    await w.WriteLineAsync(string.Join(sep, cols.Select(c => Field(c, sep))));
                    while (await r.ReadAsync(ct))
                    {
                        var vals = new string[cols.Length];
                        for (int i = 0; i < cols.Length; i++) { var v = provider.ReadValue(r, i); vals[i] = v is null ? "" : Field(ValueCoercer.ToText(v), sep); }
                        await w.WriteLineAsync(string.Join(sep, vals));
                        if (++n % 5000 == 0) progress?.Report(n);
                    }
                    break;
                }
            case ExportFormat.Json:
                {
                    await w.WriteLineAsync("[");
                    bool first = true;
                    while (await r.ReadAsync(ct))
                    {
                        var obj = new Dictionary<string, object?>();
                        for (int i = 0; i < cols.Length; i++)
                        {
                            var v = provider.ReadValue(r, i);
                            obj[cols[i]] = v switch { null => null, bool or int or long or short or decimal or double or float => v, _ => ValueCoercer.ToText(v) };
                        }
                        await w.WriteAsync((first ? "  " : ",\n  ") + JsonSerializer.Serialize(obj));
                        first = false;
                        if (++n % 5000 == 0) progress?.Report(n);
                    }
                    await w.WriteLineAsync("\n]");
                    break;
                }
            case ExportFormat.SqlInserts:
                {
                    var colList = string.Join(", ", cols.Select(c => "\"" + c.Replace("\"", "\"\"") + "\""));
                    while (await r.ReadAsync(ct))
                    {
                        var vals = new string[cols.Length];
                        for (int i = 0; i < cols.Length; i++) vals[i] = SqlLiteral(provider.ReadValue(r, i));
                        await w.WriteLineAsync($"INSERT INTO {insertTableName} ({colList}) VALUES ({string.Join(", ", vals)});");
                        if (++n % 5000 == 0) progress?.Report(n);
                    }
                    break;
                }
        }
        progress?.Report(n);
        return n;
    }

    public static void ExportTable(DataTable t, string path, ExportFormat fmt)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        var sep = fmt == ExportFormat.Tsv ? "\t" : ",";
        w.WriteLine(string.Join(sep, t.Columns.Cast<DataColumn>().Select(c => Field(c.ColumnName, sep))));
        foreach (DataRow row in t.Rows)
            w.WriteLine(string.Join(sep, row.ItemArray.Select(v => v is string s && s == QueryExecutor.NullText ? "" : Field(v?.ToString() ?? "", sep))));
    }

    private static string Field(string s, string sep) =>
        s.Contains(sep) || s.Contains('"') || s.Contains('\n') || s.Contains('\r') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string SqlLiteral(object? v) => v switch
    {
        null => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        byte or short or int or long or decimal or double or float => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        _ => "'" + ValueCoercer.ToText(v).Replace("'", "''") + "'",
    };

    // ------------------------------------------------------------- import

    public static List<string[]> ReadCsv(TextReader reader, char sep, int maxRows = int.MaxValue)
    {
        var rows = new List<string[]>();
        var field = new StringBuilder();
        var cur = new List<string>();
        bool inQuotes = false;
        int ch;
        while ((ch = reader.Read()) != -1)
        {
            var c = (char)ch;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) { inQuotes = true; continue; }
            if (c == sep) { cur.Add(field.ToString()); field.Clear(); continue; }
            if (c == '\r') continue;
            if (c == '\n')
            {
                cur.Add(field.ToString()); field.Clear();
                rows.Add(cur.ToArray()); cur.Clear();
                if (rows.Count >= maxRows) return rows;
                continue;
            }
            field.Append(c);
        }
        if (field.Length > 0 || cur.Count > 0) { cur.Add(field.ToString()); rows.Add(cur.ToArray()); }
        return rows;
    }

    public static char DetectSeparator(string headerLine)
    {
        var counts = new[] { ',', ';', '\t', '|' }.ToDictionary(c => c, c => headerLine.Count(x => x == c));
        return counts.OrderByDescending(kv => kv.Value).First().Key;
    }

    // Guess a column type from sample values (empty = NULL).
    public static ColumnInfo InferColumn(string name, IEnumerable<string> samples)
    {
        var vals = samples.Where(s => !string.IsNullOrEmpty(s)).Take(2000).ToList();
        int maxLen = vals.Count == 0 ? 0 : vals.Max(v => v.Length);
        if (vals.Count == 0) return new ColumnInfo(name, "", CanonicalType.Text);
        if (vals.All(v => long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
            return new ColumnInfo(name, "", vals.All(v => int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) ? CanonicalType.Int32 : CanonicalType.Int64);
        if (vals.All(v => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
            return new ColumnInfo(name, "", CanonicalType.Decimal);
        if (vals.All(v => v.ToLowerInvariant() is "true" or "false" or "t" or "f" or "yes" or "no"))
            return new ColumnInfo(name, "", CanonicalType.Bool);
        if (vals.All(v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)))
            return new ColumnInfo(name, "", vals.All(v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.TimeOfDay == TimeSpan.Zero) ? CanonicalType.Date : CanonicalType.DateTime);
        if (vals.All(v => Guid.TryParse(v, out _))) return new ColumnInfo(name, "", CanonicalType.Guid);
        return maxLen <= 255 ? new ColumnInfo(name, "", CanonicalType.String, Length: Math.Max(50, (int)Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(1, maxLen))))))
                             : new ColumnInfo(name, "", CanonicalType.Text);
    }

    public static async Task<long> ImportCsvAsync(ConnectionProfile target, string schema, string table, string csvPath, bool hasHeader,
        char? separator, bool truncateFirst, Action<string>? log = null, CancellationToken ct = default)
    {
        var tp = DbProviders.For(target);
        string firstLine;
        using (var peek = new StreamReader(csvPath, detectEncodingFromByteOrderMarks: true)) firstLine = peek.ReadLine() ?? "";
        var sep = separator ?? DetectSeparator(firstLine);

        List<string[]> sample;
        using (var sr = new StreamReader(csvPath, true)) sample = ReadCsv(sr, sep, 2001);
        if (sample.Count == 0) throw new InvalidOperationException("the file is empty.");
        var header = hasHeader ? sample[0] : Enumerable.Range(1, sample[0].Length).Select(i => $"column{i}").ToArray();
        var data = hasHeader ? sample.Skip(1).ToList() : sample;
        header = header.Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"column{i + 1}" : tp.NormalizeName(h.Trim())).ToArray();

        await using var conn = await tp.OpenAsync(target, ct);
        var t = await tp.GetTableAsync(conn, schema, table, ct);
        if (t == null)
        {
            var design = new TableInfo
            {
                Schema = schema,
                Name = table,
                Columns = header.Select((h, i) => InferColumn(h, data.Select(r => i < r.Length ? r[i] : ""))).ToList(),
            };
            var ddl = tp.BuildCreateTable(schema, design);
            log?.Invoke("creating table:\n" + ddl);
            await using (var cmd = conn.CreateCommand()) { cmd.CommandText = ddl; await cmd.ExecuteNonQueryAsync(ct); }
            t = await tp.GetTableAsync(conn, schema, table, ct) ?? design;
        }

        // Map CSV columns -> target columns by name (or position when no header).
        var map = header.Select((h, i) => (Csv: i, Col: hasHeader ? t.Columns.FindIndex(c => string.Equals(c.Name, h, StringComparison.OrdinalIgnoreCase)) : (i < t.Columns.Count ? i : -1)))
                        .Where(x => x.Col >= 0).ToList();
        if (map.Count == 0) throw new InvalidOperationException("no CSV column matches a column of the target table.");
        var writeTable = new TableInfo { Schema = schema, Name = t.Name, Columns = map.Select(m => t.Columns[m.Col]).ToList(), PrimaryKey = t.PrimaryKey };
        log?.Invoke($"importing {map.Count} column(s): {string.Join(", ", writeTable.Columns.Select(c => c.Name))}");

        long total = 0;
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (truncateFirst) await tp.TruncateAsync(conn, tx, schema, t.Name, ct);
        var batchSize = tp.MaxBatchRows(writeTable.Columns.Count);
        var batch = new List<object?[]>(batchSize);
        using (var sr = new StreamReader(csvPath, true))
        {
            var all = ReadCsv(sr, sep);
            foreach (var rowStrings in hasHeader ? all.Skip(1) : all)
            {
                ct.ThrowIfCancellationRequested();
                if (rowStrings.Length == 1 && rowStrings[0].Length == 0) continue; // blank line
                var row = new object?[map.Count];
                for (int k = 0; k < map.Count; k++)
                {
                    var s = map[k].Csv < rowStrings.Length ? rowStrings[map[k].Csv] : "";
                    row[k] = s.Length == 0 && writeTable.Columns[k].Type != CanonicalType.Text && writeTable.Columns[k].Type != CanonicalType.String ? null
                           : ValueCoercer.Coerce(s, writeTable.Columns[k].Type);
                }
                batch.Add(row);
                if (batch.Count >= batchSize) { await tp.WriteBatchAsync(conn, tx, schema, writeTable, batch, WriteMode.Insert, ct); total += batch.Count; batch.Clear(); }
            }
        }
        if (batch.Count > 0) { await tp.WriteBatchAsync(conn, tx, schema, writeTable, batch, WriteMode.Insert, ct); total += batch.Count; }
        await tx.CommitAsync(ct);
        log?.Invoke($"imported {total:N0} row(s).");
        return total;
    }
}
