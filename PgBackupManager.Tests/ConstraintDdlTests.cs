using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Tests;

public class ConstraintDdlTests
{
    private static readonly ForeignKeyInfo Fk = new("fk_o_c", new[] { "cust_id" }, "app", "customers", new[] { "id" }, "CASCADE", "RESTRICT");

    [Fact]
    public void Postgres_FullReferentialActions() =>
        Assert.Equal("ALTER TABLE \"app\".\"orders\" ADD CONSTRAINT \"fk_o_c\" FOREIGN KEY (\"cust_id\") REFERENCES \"app\".\"customers\" (\"id\") ON DELETE CASCADE ON UPDATE RESTRICT",
            DbProviders.For(DbEngine.PostgreSql).BuildAddForeignKey("app", "orders", Fk));

    [Fact]
    public void SqlServer_DropsRestrict() =>
        Assert.EndsWith("REFERENCES [app].[customers] ([id]) ON DELETE CASCADE", DbProviders.For(DbEngine.SqlServer).BuildAddForeignKey("app", "orders", Fk));

    [Fact]
    public void Oracle_OnlyDeleteCascadeOrSetNull()
    {
        var p = DbProviders.For(DbEngine.Oracle);
        Assert.EndsWith("(\"id\") ON DELETE CASCADE", p.BuildAddForeignKey("APP", "ORDERS", Fk));
        Assert.EndsWith("(\"id\")", p.BuildAddForeignKey("APP", "ORDERS", Fk with { OnDelete = "RESTRICT" }));
    }

    [Fact]
    public void Sqlite_CannotAddForeignKeyLater() =>
        Assert.Null(DbProviders.For(DbEngine.Sqlite).BuildAddForeignKey("main", "orders", Fk));

    [Fact]
    public void MySql_PrefixesTextColumnsInIndexes()
    {
        var t = new TableInfo { Schema = "db", Name = "t", Columns = { new ColumnInfo("title", "longtext", CanonicalType.Text), new ColumnInfo("n", "int", CanonicalType.Int32) } };
        Assert.Equal("CREATE UNIQUE INDEX `ix` ON `db`.`t` (`title`(191), `n`)", DbProviders.For(DbEngine.MySql).BuildCreateIndex("db", t, new IndexInfo("ix", new[] { "title", "n" }, true)));
    }

    [Fact]
    public void Sqlite_SchemaGoesOnIndexName()
    {
        var t = new TableInfo { Schema = "main", Name = "t" };
        Assert.Equal("CREATE INDEX \"main\".\"ix\" ON \"t\" (\"a\")", DbProviders.For(DbEngine.Sqlite).BuildCreateIndex("main", t, new IndexInfo("ix", new[] { "a" }, false)));
    }

    [Theory]
    [InlineData("SET_NULL", "SET NULL")]
    [InlineData("c", "CASCADE")]
    [InlineData("NO_ACTION", "NO ACTION")]
    [InlineData("a", "NO ACTION")]
    public void ActionNames_AreNormalized(string raw, string expected) => Assert.Equal(expected, DbProviderBase.NormalizeAction(raw));
}
