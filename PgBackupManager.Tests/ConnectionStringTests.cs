using Microsoft.Data.SqlClient;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Tests;

public class ConnectionStringTests
{
    private static ConnectionProfile P(DbEngine e, string? ssl = null) => new()
    {
        Engine = e, Host = "db.example", Port = ConnectionProfile.DefaultPort(e), Database = "app", Username = "u", SslMode = ssl, SqlIntegratedSecurity = false,
    };

    [Fact]
    public void Postgres_NoSslMode_LeavesDriverDefault()
    {
        var cs = P(DbEngine.PostgreSql).BuildConnectionString("pw");
        Assert.DoesNotContain("SSL Mode", cs);
        Assert.Contains("Host=db.example;Port=5432", cs);
    }

    [Theory]
    [InlineData("Require")]
    [InlineData("VerifyFull")]
    public void Postgres_SslMode_Passed(string mode) =>
        Assert.Contains("SSL Mode=" + mode, P(DbEngine.PostgreSql, mode).BuildConnectionString("pw"));

    [Theory]
    [InlineData("Disable", "None")]
    [InlineData("Prefer", "Preferred")]
    [InlineData("Require", "Required")]
    [InlineData("VerifyCA", "VerifyCA")]
    public void MySql_SslMode_Mapped(string mode, string expected) =>
        Assert.Contains("SslMode=" + expected, P(DbEngine.MySql, mode).BuildConnectionString("pw"));

    [Fact]
    public void SqlServer_Require_EncryptsButTrustsCert()
    {
        var b = new SqlConnectionStringBuilder(P(DbEngine.SqlServer, "Require").BuildConnectionString("pw"));
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.True(b.TrustServerCertificate);
    }

    [Fact]
    public void SqlServer_VerifyFull_ChecksCert()
    {
        var b = new SqlConnectionStringBuilder(P(DbEngine.SqlServer, "VerifyFull").BuildConnectionString("pw"));
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.False(b.TrustServerCertificate);
    }

    [Fact]
    public void SqlServer_Default_StaysOptional()
    {
        var b = new SqlConnectionStringBuilder(P(DbEngine.SqlServer).BuildConnectionString("pw"));
        Assert.Equal(SqlConnectionEncryptOption.Optional, b.Encrypt);
    }

    [Fact]
    public void Oracle_Require_UsesTcpsDescriptor()
    {
        var cs = P(DbEngine.Oracle, "Require").BuildConnectionString("pw");
        Assert.Contains("(PROTOCOL=TCPS)", cs);
        Assert.Contains("(SERVICE_NAME=app)", cs);
    }

    [Fact]
    public void Oracle_Default_UsesEzConnect()
    {
        var cs = P(DbEngine.Oracle).BuildConnectionString("pw");
        Assert.Contains("Data Source=//db.example:1521/app", cs);
    }

    [Fact]
    public void Libpq_SslMode_Spelling()
    {
        Assert.Equal("verify-ca", P(DbEngine.PostgreSql, "VerifyCA").LibpqSslMode);
        Assert.Null(P(DbEngine.PostgreSql).LibpqSslMode);
    }

    [Fact]
    public void ExtraOptions_Appended()
    {
        var p = P(DbEngine.PostgreSql); p.ExtraOptions = "Pooling=false;";
        Assert.EndsWith(";Pooling=false", p.BuildConnectionString("pw"));
    }

    [Fact]
    public void SshOff_EndpointIsHost()
    {
        var p = P(DbEngine.PostgreSql);
        Assert.Equal("db.example", p.EndpointHost);
        Assert.Equal(5432, p.EndpointPort);
    }
}
