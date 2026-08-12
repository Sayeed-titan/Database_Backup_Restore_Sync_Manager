using System;
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

    public string BuildConnectionString(string plaintextPassword) => Engine switch
    {
        DbEngine.SqlServer => BuildSqlServerConnectionString(plaintextPassword),
        _ => $"Host={Host};Port={Port};Database={Database};Username={Username};Password={plaintextPassword};Timeout=10;CommandTimeout=60;Include Error Detail=true",
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
}
