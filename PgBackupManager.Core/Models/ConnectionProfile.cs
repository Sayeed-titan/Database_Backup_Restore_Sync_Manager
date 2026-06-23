using System;

namespace PgBackupManager.Core.Models;

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "postgres";
    public string Username { get; set; } = "postgres";

    public string EncryptedPasswordBase64 { get; set; } = "";

    public string? DefaultSchema { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public string BuildConnectionString(string plaintextPassword) =>
        $"Host={Host};Port={Port};Database={Database};Username={Username};Password={plaintextPassword};Timeout=10;CommandTimeout=60;Include Error Detail=true";

    // Shown in profile dropdowns (custom ComboBox template falls back to ToString()).
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? $"{Database}@{Host}" : Name;
}
