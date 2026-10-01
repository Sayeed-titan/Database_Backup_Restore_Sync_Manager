using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Sql;

namespace PgBackupManager.Tests;

public class SqlLiteralTests
{
    private static string F(object? v, CanonicalType t, ScriptDialect d) => SqlLiterals.Format(v, t, d);

    [Fact]
    public void Null_And_Numbers()
    {
        Assert.Equal("NULL", F(null, CanonicalType.Int32, ScriptDialect.Oracle));
        Assert.Equal("1.5", F(1.5m, CanonicalType.Decimal, ScriptDialect.SqlServer));
        Assert.Equal("-42", F(-42L, CanonicalType.Int64, ScriptDialect.MySql));
    }

    [Fact]
    public void Bool_PerDialect()
    {
        Assert.Equal("TRUE", F(true, CanonicalType.Bool, ScriptDialect.Postgres));
        Assert.Equal("0", F(false, CanonicalType.Bool, ScriptDialect.SqlServer));
    }

    [Fact]
    public void Strings_Escaped()
    {
        Assert.Equal("'O''Brien'", F("O'Brien", CanonicalType.String, ScriptDialect.Postgres));
        Assert.Equal("N'O''Brien'", F("O'Brien", CanonicalType.String, ScriptDialect.SqlServer));
        Assert.Equal(@"'C:\\dir'", F(@"C:\dir", CanonicalType.String, ScriptDialect.MySql));
        Assert.Equal(@"'C:\dir'", F(@"C:\dir", CanonicalType.String, ScriptDialect.Postgres));
    }

    [Fact]
    public void Oracle_LongText_BecomesClobPieces()
    {
        var s = F(new string('x', 2500), CanonicalType.Text, ScriptDialect.Oracle);
        Assert.Equal(3, s.Split(" || ").Length);
        Assert.StartsWith("TO_CLOB('", s);
    }

    [Fact]
    public void Dates_PerDialect()
    {
        var day = new DateTime(2024, 3, 5);
        var ts = new DateTime(2024, 3, 5, 14, 7, 9, 120);
        Assert.Equal("DATE '2024-03-05'", F(day, CanonicalType.Date, ScriptDialect.Oracle));
        Assert.Equal("TO_TIMESTAMP('2024-03-05 14:07:09.12', 'YYYY-MM-DD HH24:MI:SS.FF')", F(ts, CanonicalType.DateTime, ScriptDialect.Oracle));
        Assert.Equal("CAST('2024-03-05T14:07:09.1200000' AS datetime2)", F(ts, CanonicalType.DateTime, ScriptDialect.SqlServer));
        Assert.Equal("'2024-03-05 14:07:09.12'", F(ts, CanonicalType.DateTime, ScriptDialect.Postgres));
        Assert.Equal("'2024-03-05 00:00:00'", F(day, CanonicalType.DateTime, ScriptDialect.MySql));
    }

    [Fact]
    public void Binary_PerDialect()
    {
        var b = new byte[] { 0xAB, 0x01 };
        Assert.Equal(@"'\xAB01'::bytea", F(b, CanonicalType.Binary, ScriptDialect.Postgres));
        Assert.Equal("0xAB01", F(b, CanonicalType.Binary, ScriptDialect.SqlServer));
        Assert.Equal("HEXTORAW('AB01')", F(b, CanonicalType.Binary, ScriptDialect.Oracle));
        Assert.Equal("X'AB01'", F(b, CanonicalType.Binary, ScriptDialect.Sqlite));
    }
}
