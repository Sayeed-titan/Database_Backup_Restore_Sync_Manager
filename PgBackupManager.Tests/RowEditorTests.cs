using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Services;

namespace PgBackupManager.Tests;

public class RowEditorTests
{
    [Theory]
    [InlineData("SELECT * FROM app.orders WHERE id > 5 ORDER BY id", "app", "orders")]
    [InlineData("select id, name from customers c limit 10;", null, "customers")]
    [InlineData("SELECT id FROM \"Mixed\".\"Case\"", "Mixed", "Case")]
    public void Analyze_SingleTable(string sql, string? schema, string table)
    {
        var (t, reason) = RowEditor.Analyze(sql, ScriptDialect.Postgres);
        Assert.Null(reason);
        Assert.Equal(schema, t!.Schema);
        Assert.Equal(table, t.Table);
    }

    [Theory]
    [InlineData("SELECT * FROM a JOIN b ON a.id = b.id")]
    [InlineData("SELECT * FROM a, b")]
    [InlineData("SELECT DISTINCT x FROM a")]
    [InlineData("SELECT x, count(*) FROM a GROUP BY x")]
    [InlineData("SELECT * FROM (SELECT 1) s")]
    [InlineData("UPDATE a SET x = 1")]
    public void Analyze_NotEditable(string sql) => Assert.Null(RowEditor.Analyze(sql, ScriptDialect.Postgres).Target);

    [Fact]
    public void Analyze_SubqueryInWhereIsFine() =>
        Assert.Equal("orders", RowEditor.Analyze("SELECT * FROM orders WHERE cust IN (SELECT id FROM c)", ScriptDialect.Postgres).Target!.Table);

    [Fact]
    public void ParseCell_TypesAndNull()
    {
        Assert.Null(RowEditor.ParseCell("NULL", new ColumnInfo("x", "", CanonicalType.Int32)));
        Assert.Equal(42, RowEditor.ParseCell("42", new ColumnInfo("x", "", CanonicalType.Int32)));
        Assert.Equal("NULL ", RowEditor.ParseCell("NULL ", new ColumnInfo("x", "", CanonicalType.Text)));
        Assert.Equal(true, RowEditor.ParseCell("true", new ColumnInfo("x", "", CanonicalType.Bool)));
        Assert.True(RowEditor.IsTruncatedDisplay("abc… (12,000 chars)"));
    }
}
