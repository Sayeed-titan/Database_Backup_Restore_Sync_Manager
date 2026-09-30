using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.Tests;

public class TypeMappingTests
{
    [Theory]
    [InlineData(4, 0, CanonicalType.Int16)]
    [InlineData(9, 0, CanonicalType.Int32)]
    [InlineData(10, 0, CanonicalType.Int64)]
    [InlineData(18, 0, CanonicalType.Int64)]
    [InlineData(19, 0, CanonicalType.Decimal)]
    [InlineData(12, 2, CanonicalType.Decimal)]
    public void Oracle_Number_Mapping(int p, int s, CanonicalType expected) =>
        Assert.Equal(expected, OracleProvider_Map("NUMBER", null, p, s).Type);

    [Fact]
    public void Oracle_Date_Is_DateTime() => Assert.Equal(CanonicalType.DateTime, OracleProvider_Map("DATE", null, null, null).Type);

    [Fact]
    public void Oracle_TimestampTz() => Assert.Equal(CanonicalType.DateTimeOffset, OracleProvider_Map("TIMESTAMP(6) WITH TIME ZONE", null, null, null).Type);

    private static ColumnInfo OracleProvider_Map(string t, int? len, int? p, int? s) =>
        (ColumnInfo)typeof(OracleProvider).GetMethod("MapColumn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { "c", t, len, p, s, true, false })!;

    [Fact]
    public void Canonical_RoundTrip_NativeTypes()
    {
        var col = new ColumnInfo("c", "", CanonicalType.String, Length: 50);
        Assert.Equal("varchar(50)", DbProviders.For(DbEngine.PostgreSql).NativeType(col, false));
        Assert.Equal("nvarchar(50)", DbProviders.For(DbEngine.SqlServer).NativeType(col, false));
        Assert.Equal("VARCHAR2(50 CHAR)", DbProviders.For(DbEngine.Oracle).NativeType(col, false));
        Assert.Equal("varchar(50)", DbProviders.For(DbEngine.MySql).NativeType(col, false));
        var text = new ColumnInfo("c", "", CanonicalType.Text);
        Assert.Equal("nvarchar(450)", DbProviders.For(DbEngine.SqlServer).NativeType(text, true));
        Assert.Equal("varchar(255)", DbProviders.For(DbEngine.MySql).NativeType(text, true));
    }

    [Fact]
    public void Coercion()
    {
        Assert.Equal(true, ValueCoercer.Coerce(1m, CanonicalType.Bool));
        Assert.Equal((short)1, ValueCoercer.Coerce(true, CanonicalType.Int16));
        Assert.Equal(12L, ValueCoercer.Coerce(12m, CanonicalType.Int64));
        Assert.Equal(new DateTime(2024, 5, 7), ValueCoercer.Coerce("2024-05-07", CanonicalType.Date));
        Assert.Equal("ab", ValueCoercer.Coerce("a\0b", CanonicalType.Text));
        Assert.Equal("{1,2,NULL}", ValueCoercer.Coerce(new int?[] { 1, 2, null }, CanonicalType.Text));
        var g = Guid.NewGuid();
        Assert.Equal(g, ValueCoercer.Coerce(g.ToString(), CanonicalType.Guid));
        Assert.Equal(g.ToString(), ValueCoercer.Coerce(g, CanonicalType.String));
        Assert.Null(ValueCoercer.Coerce(DBNull.Value, CanonicalType.Int32));
        Assert.Throws<InvalidCastException>(() => ValueCoercer.Coerce("abc", CanonicalType.Int32));
    }

    [Fact]
    public void ConnectionStrings()
    {
        var o = new ConnectionProfile { Engine = DbEngine.Oracle, Host = "db", Port = 1521, Database = "ORCLPDB1", Username = "hr" };
        Assert.Contains("Data Source=//db:1521/ORCLPDB1", o.BuildConnectionString("pw"));
        o.OracleUseSid = true;
        Assert.Contains("(SID=ORCLPDB1)", o.BuildConnectionString("pw"));
        var m = new ConnectionProfile { Engine = DbEngine.MySql, Host = "h", Port = 3306, Database = "d", Username = "u", ExtraOptions = "SslMode=Required" };
        Assert.EndsWith(";SslMode=Required", m.BuildConnectionString("p"));
    }
}
