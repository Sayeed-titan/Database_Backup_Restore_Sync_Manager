using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Sql;

namespace PgBackupManager.Tests;

public class PagingTests
{
    [Fact]
    public void Postgres_WrapsWithLimitOffset()
    {
        var s = QueryPaging.PageSql(ScriptDialect.Postgres, "SELECT * FROM t ORDER BY id;", 5000, 500)!;
        Assert.Equal("SELECT * FROM (\nSELECT * FROM t ORDER BY id\n) AS pgbm_page LIMIT 500 OFFSET 5000", s);
    }

    [Fact]
    public void SqlServer_AppendsOffsetFetch_AddsOrderWhenMissing()
    {
        Assert.EndsWith("ORDER BY id\nOFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY", QueryPaging.PageSql(ScriptDialect.SqlServer, "SELECT * FROM t ORDER BY id", 10, 5));
        Assert.EndsWith("ORDER BY (SELECT NULL)\nOFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY", QueryPaging.PageSql(ScriptDialect.SqlServer, "SELECT * FROM t", 10, 5));
        // ORDER BY inside a subquery doesn't count as the top-level one
        Assert.Contains("ORDER BY (SELECT NULL)", QueryPaging.PageSql(ScriptDialect.SqlServer, "SELECT * FROM (SELECT TOP 5 * FROM t ORDER BY id) x", 0, 5));
    }

    [Fact]
    public void SqlServer_TopIsNotPageable() =>
        Assert.Null(QueryPaging.PageSql(ScriptDialect.SqlServer, "SELECT TOP 100 * FROM t", 0, 10));

    [Fact]
    public void Oracle_AppendsOrWraps()
    {
        Assert.EndsWith("FROM emp\nOFFSET 0 ROWS FETCH NEXT 50 ROWS ONLY", QueryPaging.PageSql(ScriptDialect.Oracle, "SELECT * FROM emp", 0, 50));
        Assert.StartsWith("SELECT * FROM (", QueryPaging.PageSql(ScriptDialect.Oracle, "SELECT * FROM emp FETCH FIRST 10 ROWS ONLY", 0, 5));
    }

    [Fact]
    public void Count_StripsOrderByForSqlServer()
    {
        Assert.Equal("SELECT COUNT(*) FROM (\nSELECT * FROM t\n) pgbm_count", QueryPaging.CountSql(ScriptDialect.SqlServer, "SELECT * FROM t ORDER BY id"));
        Assert.Equal("SELECT COUNT(*) FROM (\nSELECT 1\n) AS pgbm_count", QueryPaging.CountSql(ScriptDialect.Postgres, "SELECT 1;"));
    }

    [Fact]
    public void Count_SqlServer_LiftsCteOutOfDerivedTable() =>
        Assert.Equal("WITH x AS (SELECT 1 AS n)\nSELECT COUNT(*) FROM (\nSELECT n FROM x\n) pgbm_count",
            QueryPaging.CountSql(ScriptDialect.SqlServer, "WITH x AS (SELECT 1 AS n) SELECT n FROM x ORDER BY n"));

    [Theory]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("EXEC sp_who")]
    [InlineData("SHOW TABLES")]
    public void NonQueries_AreNotPageable(string sql) => Assert.Null(QueryPaging.PageSql(ScriptDialect.MySql, sql, 0, 10));
}
