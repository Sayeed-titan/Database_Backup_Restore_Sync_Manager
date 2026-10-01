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

    // Transport security. Disable | Prefer | Require | VerifyCA | VerifyFull
    // (null = the engine's own default, which is what older profiles keep).
    public string? SslMode { get; set; }

    // SSH local port forwarding: connect to Host:Port through an SSH server.
    public bool SshEnabled { get; set; }
    public string? SshHost { get; set; }
    public int SshPort { get; set; } = 22;
    public string? SshUser { get; set; }
    public string? SshEncryptedPassword { get; set; }
    public string? SshKeyFile { get; set; }
    public string? SshEncryptedKeyPassphrase { get; set; }

    // Same connection (same Id, password, SSL/SSH) pointed at another database
    // on the server — lets a page pick the database without editing the profile.
    public ConnectionProfile WithDatabase(string? database)
    {
        if (string.IsNullOrWhiteSpace(database) || Engine == DbEngine.Sqlite || string.Equals(database, Database, StringComparison.Ordinal)) return this;
        var copy = System.Text.Json.JsonSerializer.Deserialize<ConnectionProfile>(System.Text.Json.JsonSerializer.Serialize(this))!;
        copy.Database = database;
        return copy;
    }

    // Where clients actually connect: the SSH tunnel's local end when the
    // tunnel is on (opened on first use), otherwise Host:Port. Use these for
    // connection strings and for external tools; keep Host for display and
    // local-vs-remote safety checks.
    [JsonIgnore] public string EndpointHost => SshEnabled ? "127.0.0.1" : Host;
    [JsonIgnore] public int EndpointPort => SshEnabled ? PgBackupManager.Core.Services.SshTunnels.Ensure(this) : Port;

    // libpq spelling for PGSSLMODE (pg_dump / pg_restore / psql).
    [JsonIgnore] public string? LibpqSslMode => SslMode switch
    {
        "Disable" => "disable", "Prefer" => "prefer", "Require" => "require", "VerifyCA" => "verify-ca", "VerifyFull" => "verify-full", _ => null,
    };

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
        if (Engine == DbEngine.Sqlite) return AppendExtra($"Data Source={Database}");
        var host = EndpointHost;
        var port = EndpointPort;
        var cs = Engine switch
        {
            DbEngine.SqlServer => BuildSqlServerConnectionString(plaintextPassword, host, port),
            DbEngine.Oracle => BuildOracleConnectionString(plaintextPassword, host, port),
            DbEngine.MySql => $"Server={host};Port={port};Database={Database};User ID={Username};Password={plaintextPassword};" +
                              "Connection Timeout=15;Default Command Timeout=0;Allow Zero DateTime=True;Convert Zero DateTime=True;" +
                              "AllowUserVariables=True;AllowLoadLocalInfile=False;Treat Tiny As Boolean=True" +
                              (SslMode is { } m ? ";SslMode=" + (m switch { "Disable" => "None", "Prefer" => "Preferred", "Require" => "Required", _ => m }) : ""),
            _ => $"Host={host};Port={port};Database={Database};Username={Username};Password={plaintextPassword};Timeout=10;CommandTimeout=60;Include Error Detail=true" +
                 (SslMode is { } s ? ";SSL Mode=" + s : ""),   // Npgsql uses the same names
        };
        return AppendExtra(cs);
    }

    private string AppendExtra(string cs)
    {
        var extra = ExtraOptions?.Trim().Trim(';');
        return string.IsNullOrEmpty(extra) ? cs : cs.TrimEnd(';') + ";" + extra;
    }

    private string BuildOracleConnectionString(string plaintextPassword, string host, int port)
    {
        if (port <= 0) port = 1521;
        // TCPS (TLS) needs a full descriptor; the server certificate is checked
        // against the Windows store / wallet as configured for ODP.NET.
        var tls = SslMode is "Require" or "VerifyCA" or "VerifyFull";
        var dataSource = OracleUseSid || tls
            ? $"(DESCRIPTION=(ADDRESS=(PROTOCOL={(tls ? "TCPS" : "TCP")})(HOST={host})(PORT={port}))(CONNECT_DATA=({(OracleUseSid ? "SID" : "SERVICE_NAME")}={Database})){(SslMode == "VerifyFull" ? "(SECURITY=(SSL_SERVER_DN_MATCH=TRUE))" : "")})"
            : $"//{host}:{port}/{Database}";
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

    private string BuildSqlServerConnectionString(string plaintextPassword, string host, int port)
    {
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = port is > 0 and not 1433 ? $"{host},{port}" : host,
            InitialCatalog = Database,
            TrustServerCertificate = SslMode is not ("VerifyCA" or "VerifyFull"),
            // Modern Microsoft.Data.SqlClient defaults Encrypt to Mandatory, which
            // fails the pre-login TLS handshake against a typical local/dev SQL
            // Server instance that isn't set up for forced encryption (confirmed
            // against a live default instance this session — sqlcmd works fine
            // because it doesn't force encryption either). Matches the un-encrypted
            // default every other client tool here already assumes.
            Encrypt = SslMode is "Require" or "VerifyCA" or "VerifyFull" ? SqlConnectionEncryptOption.Mandatory
                    : SslMode == "Prefer" ? SqlConnectionEncryptOption.Optional
                    : SqlConnectionEncryptOption.Optional,
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
