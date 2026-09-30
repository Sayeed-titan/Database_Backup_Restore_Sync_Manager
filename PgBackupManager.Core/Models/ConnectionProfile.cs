using System;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace PgBackupManager.Core.Models;

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DbEngine Engine { get; set; } = DbEngine.PostgreSql;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "postgres";
    public string Username { get; set; } = "postgres";

    // SQL Server only — Windows/Integrated auth vs Username/Password login.
    // Meaningless (ignored) when Engine == PostgreSql.
    public bool SqlIntegratedSecurity { get; set; } = true;

    public string EncryptedPasswordBase64 { get; set; } = "";

    public string? DefaultSchema { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    // Oracle only — Database holds a SERVICE_NAME by default; tick this when
    // it's an old-style SID instead.
    public bool OracleUseSid { get; set; }

    // Extra "key=value;..." pairs appended to the generated connection string
    // (any engine) — escape hatch for SSL modes, timeouts, wallets, etc.
    public string? ExtraOptions { get; set; }

    public static int DefaultPort(DbEngine engine) => engine switch
    {
        DbEngine.SqlServer => 1433,
        DbEngine.Oracle => 1521,
        DbEngine.MySql => 3306,
        DbEngine.Sqlite => 0,
        _ => 5432,
    };

    public string BuildConnectionString(string plaintextPassword)
    {
        var cs = Engine switch
        {
            DbEngine.SqlServer => BuildSqlServerConnectionString(plaintextPassword),
            DbEngine.Oracle => BuildOracleConnectionString(plaintextPassword),
            DbEngine.MySql => $"Server={Host};Port={Port};Database={Database};User ID={Username};Password={plaintextPassword};" +
                              "Connection Timeout=15;Default Command Timeout=0;Allow Zero DateTime=True;Convert Zero DateTime=True;" +
                              "AllowUserVariables=True;AllowLoadLocalInfile=False;Treat Tiny As Boolean=True",
            DbEngine.Sqlite => $"Data Source={Database}",
            _ => $"Host={Host};Port={Port};Database={Database};Username={Username};Password={plaintextPassword};Timeout=10;CommandTimeout=60;Include Error Detail=true",
        };
        var extra = ExtraOptions?.Trim().Trim(';');
        return string.IsNullOrEmpty(extra) ? cs : cs.TrimEnd(';') + ";" + extra;
    }

    private string BuildOracleConnectionString(string plaintextPassword)
    {
        var port = Port > 0 ? Port : 1521;
        var dataSource = OracleUseSid
            ? $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={Host})(PORT={port}))(CONNECT_DATA=(SID={Database})))"
            : $"//{Host}:{port}/{Database}";
        return $"User Id={Username};Password=\"{plaintextPassword.Replace("\"", "")}\";Data Source={dataSource};Connection Timeout=15;Statement Cache Size=0";
    }

    // The schema a fresh object browser/transfer should start in.
    public string ResolveDefaultSchema() => !string.IsNullOrWhiteSpace(DefaultSchema) ? DefaultSchema! : Engine switch
    {
        DbEngine.SqlServer => "dbo",
        DbEngine.Oracle => Username.ToUpperInvariant(),
        DbEngine.MySql => Database,
        DbEngine.Sqlite => "main",
        _ => "public",
    };

    private string BuildSqlServerConnectionString(string plaintextPassword)
    {
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = Port is > 0 and not 1433 ? $"{Host},{Port}" : Host,
            InitialCatalog = Database,
            TrustServerCertificate = true,
            // Modern Microsoft.Data.SqlClient defaults Encrypt to Mandatory, which
            // fails the pre-login TLS handshake against a typical local/dev SQL
            // Server instance that isn't set up for forced encryption (confirmed
            // against a live default instance this session — sqlcmd works fine
            // because it doesn't force encryption either). Matches the un-encrypted
            // default every other client tool here already assumes.
            Encrypt = false,
            ConnectTimeout = 15,
        };
        if (SqlIntegratedSecurity) csb.IntegratedSecurity = true;
        else
        {
            csb.UserID = Username;
            csb.Password = plaintextPassword;
        }
        return csb.ConnectionString;
    }

    // Shown in profile dropdowns (custom ComboBox template falls back to ToString()).
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? $"{Database}@{Host}" : Name;

    [JsonIgnore] public string EngineLabel => Engine switch
    {
        DbEngine.SqlServer => "MSSQL",
        DbEngine.Oracle => "ORACLE",
        DbEngine.MySql => "MYSQL",
        DbEngine.Sqlite => "SQLITE",
        _ => "PG",
    };

    // "[PG] name" — for dropdowns that mix engines.
    [JsonIgnore] public string DisplayWithEngine => $"[{EngineLabel}]  {this}";
}
