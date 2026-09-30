using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Sql;

namespace PgBackupManager.Tests;

public class SplitterTests
{
    [Fact]
    public void Postgres_DollarQuotes_And_Strings_DoNotSplit()
    {
        var sql = "SELECT 'a;b';\nCREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql;\n-- c; d\nSELECT 2;";
        var parts = SqlScriptSplitter.Split(sql, ScriptDialect.Postgres);
        Assert.Equal(3, parts.Count);
        Assert.Contains("RETURN 1; END;", parts[1]);
    }

    [Fact]
    public void Postgres_CopyFromStdin_CapturesData()
    {
        var sql = "COPY public.t (a, b) FROM stdin;\n1\tx'y\n2\tz\n\\.\nSELECT 1;";
        var parts = SqlScriptSplitter.SplitDetailed(sql, ScriptDialect.Postgres);
        Assert.Equal(StatementKind.CopyIn, parts[0].Kind);
        Assert.Equal("1\tx'y\n2\tz\n", parts[0].CopyData);
        Assert.Equal("SELECT 1", parts[1].Text);
    }

    [Fact]
    public void Postgres_MetaCommands_AreSeparated()
    {
        var parts = SqlScriptSplitter.SplitDetailed("\\connect db\nSELECT 1;", ScriptDialect.Postgres);
        Assert.Equal(StatementKind.Meta, parts[0].Kind);
        Assert.Equal(StatementKind.Sql, parts[1].Kind);
    }

    [Fact]
    public void SqlServer_SplitsOnGoOnly()
    {
        var sql = "SELECT 1; SELECT 2\nGO\nCREATE PROC p AS BEGIN SELECT 3; END\ngo\n";
        var parts = SqlScriptSplitter.Split(sql, ScriptDialect.SqlServer);
        Assert.Equal(2, parts.Count);
        Assert.StartsWith("SELECT 1; SELECT 2", parts[0]);
    }

    [Fact]
    public void Oracle_PlSqlBlocks_EndAtSlash_PlainSqlDropsSemicolon()
    {
        var sql = "CREATE TABLE t (a NUMBER);\nCREATE OR REPLACE PROCEDURE p IS\nBEGIN\n  NULL;\nEND;\n/\nSELECT 1 FROM dual;";
        var parts = SqlScriptSplitter.Split(sql, ScriptDialect.Oracle);
        Assert.Equal(3, parts.Count);
        Assert.Equal("CREATE TABLE t (a NUMBER)", parts[0]);
        Assert.EndsWith("END;", parts[1]);
        Assert.Equal("SELECT 1 FROM dual", parts[2]);
    }

    [Fact]
    public void MySql_Delimiter()
    {
        var sql = "DELIMITER //\nCREATE PROCEDURE p() BEGIN SELECT 1; END //\nDELIMITER ;\nSELECT 2;";
        var parts = SqlScriptSplitter.Split(sql, ScriptDialect.MySql);
        Assert.Equal(2, parts.Count);
        Assert.Contains("SELECT 1;", parts[0]);
    }

    [Fact]
    public void Sqlite_TriggerBody_NotSplit()
    {
        var sql = "CREATE TRIGGER tr AFTER INSERT ON t BEGIN UPDATE x SET a = 1; DELETE FROM y; END;\nSELECT 1;";
        var parts = SqlScriptSplitter.Split(sql, ScriptDialect.Sqlite);
        Assert.Equal(2, parts.Count);
    }
}
