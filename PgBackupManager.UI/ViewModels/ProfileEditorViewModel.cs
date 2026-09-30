using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PgBackupManager.Core.Models;

namespace PgBackupManager.UI.ViewModels;

public sealed record EngineOption(DbEngine Engine, string Label)
{
    public override string ToString() => Label;
}

public partial class ProfileEditorViewModel : ObservableObject
{
    public static IReadOnlyList<EngineOption> AllEngines { get; } = new EngineOption[]
    {
        new(DbEngine.PostgreSql, "PostgreSQL"),
        new(DbEngine.SqlServer, "SQL Server"),
        new(DbEngine.Oracle, "Oracle"),
        new(DbEngine.MySql, "MySQL / MariaDB"),
        new(DbEngine.Sqlite, "SQLite (file)"),
    };
    public IReadOnlyList<EngineOption> Engines => AllEngines;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private DbEngine _engine = DbEngine.PostgreSql;
    [ObservableProperty] private string _host = "localhost";
    [ObservableProperty] private int _port = 5432;
    [ObservableProperty] private string _database = "postgres";
    [ObservableProperty] private string _username = "postgres";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _defaultSchema = "";
    [ObservableProperty] private string _extraOptions = "";
    // SQL Server only.
    [ObservableProperty] private bool _sqlIntegratedSecurity = true;
    // Oracle only.
    [ObservableProperty] private bool _oracleUseSid;

    public EngineOption? SelectedEngine
    {
        get => AllEngines.FirstOrDefault(e => e.Engine == Engine);
        set { if (value != null) Engine = value.Engine; }
    }

    public bool IsPostgres => Engine == DbEngine.PostgreSql;
    public bool IsSqlServer => Engine == DbEngine.SqlServer;
    public bool IsOracle => Engine == DbEngine.Oracle;
    public bool IsSqlite => Engine == DbEngine.Sqlite;
    public bool IsServer => !IsSqlite;
    public bool ShowCredentials => Engine switch
    {
        DbEngine.Sqlite => false,
        DbEngine.SqlServer => !SqlIntegratedSecurity,
        _ => true,
    };
    public bool ShowDefaultSchema => !IsSqlite;

    public string DatabaseLabel => Engine switch
    {
        DbEngine.Oracle => OracleUseSid ? "SID" : "SERVICE NAME",
        DbEngine.Sqlite => "DATABASE FILE",
        _ => "DATABASE",
    };

    public ConnectionProfile Source { get; }

    public ProfileEditorViewModel(ConnectionProfile profile)
    {
        Source = profile;
        Name = profile.Name;
        Engine = profile.Engine;
        Host = profile.Host;
        Port = profile.Port;
        Database = profile.Database;
        Username = profile.Username;
        DefaultSchema = profile.DefaultSchema ?? "";
        ExtraOptions = profile.ExtraOptions ?? "";
        SqlIntegratedSecurity = profile.SqlIntegratedSecurity;
        OracleUseSid = profile.OracleUseSid;
        Password = PgBackupManager.Core.Services.SecretProtector.Unprotect(profile.EncryptedPasswordBase64);
    }

    // Swaps the default Port when the engine changes, but only while Port is
    // still sitting at SOME engine's default — a value the user actually
    // typed is never overwritten.
    partial void OnEngineChanged(DbEngine oldValue, DbEngine newValue)
    {
        foreach (var n in new[] { nameof(SelectedEngine), nameof(IsPostgres), nameof(IsSqlServer), nameof(IsOracle), nameof(IsSqlite),
                                  nameof(IsServer), nameof(ShowCredentials), nameof(ShowDefaultSchema), nameof(DatabaseLabel) })
            OnPropertyChanged(n);

        if (Port == ConnectionProfile.DefaultPort(oldValue) || Port == 0)
            Port = ConnectionProfile.DefaultPort(newValue);

        // Swap obviously-engine-specific placeholder defaults too.
        if (Database is "postgres" or "master" or "ORCLPDB1" or "mysql" or "")
            Database = newValue switch { DbEngine.SqlServer => "master", DbEngine.Oracle => "ORCLPDB1", DbEngine.MySql => "mysql", DbEngine.Sqlite => "", _ => "postgres" };
        if (Username is "postgres" or "sa" or "system" or "root" or "")
            Username = newValue switch { DbEngine.SqlServer => "sa", DbEngine.Oracle => "system", DbEngine.MySql => "root", DbEngine.Sqlite => "", _ => "postgres" };
    }

    partial void OnSqlIntegratedSecurityChanged(bool value) => OnPropertyChanged(nameof(ShowCredentials));
    partial void OnOracleUseSidChanged(bool value) => OnPropertyChanged(nameof(DatabaseLabel));

    [RelayCommand]
    private void BrowseSqliteFile()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Pick (or name a new) SQLite database file",
            Filter = "SQLite database (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*",
            OverwritePrompt = false,
            CheckFileExists = false,
        };
        if (dlg.ShowDialog() == true) Database = dlg.FileName;
    }

    public ConnectionProfile Apply()
    {
        Source.Name = Name.Trim();
        Source.Engine = Engine;
        Source.Host = Host.Trim();
        Source.Port = Port;
        Source.Database = Database.Trim();
        Source.Username = Username.Trim();
        Source.DefaultSchema = string.IsNullOrWhiteSpace(DefaultSchema) ? null : DefaultSchema.Trim();
        Source.ExtraOptions = string.IsNullOrWhiteSpace(ExtraOptions) ? null : ExtraOptions.Trim();
        Source.SqlIntegratedSecurity = SqlIntegratedSecurity;
        Source.OracleUseSid = OracleUseSid;
        Source.EncryptedPasswordBase64 = PgBackupManager.Core.Services.SecretProtector.Protect(Password ?? "");
        return Source;
    }
}
