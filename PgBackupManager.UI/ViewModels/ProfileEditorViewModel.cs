using CommunityToolkit.Mvvm.ComponentModel;
using PgBackupManager.Core.Models;

namespace PgBackupManager.UI.ViewModels;

public partial class ProfileEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private DbEngine _engine = DbEngine.PostgreSql;
    [ObservableProperty] private string _host = "localhost";
    [ObservableProperty] private int _port = 5432;
    [ObservableProperty] private string _database = "postgres";
    [ObservableProperty] private string _username = "postgres";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _defaultSchema = "";
    // SQL Server only.
    [ObservableProperty] private bool _sqlIntegratedSecurity = true;

    public bool IsPostgres => Engine == DbEngine.PostgreSql;
    public bool IsSqlServer => Engine == DbEngine.SqlServer;
    public bool ShowCredentials => IsPostgres || !SqlIntegratedSecurity;

    // RadioButton-friendly: setting either one flips Engine; both stay in sync
    // because OnEngineChanged raises change notifications for both.
    public bool IsPostgresChecked
    {
        get => IsPostgres;
        set { if (value) Engine = DbEngine.PostgreSql; }
    }
    public bool IsSqlServerChecked
    {
        get => IsSqlServer;
        set { if (value) Engine = DbEngine.SqlServer; }
    }

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
        SqlIntegratedSecurity = profile.SqlIntegratedSecurity;
        Password = PgBackupManager.Core.Services.SecretProtector.Unprotect(profile.EncryptedPasswordBase64);
    }

    // Swaps the default Port (5432 <-> 1433) when the engine changes, but only
    // while Port is still sitting at the OTHER engine's default — a value the
    // user actually typed is never overwritten.
    partial void OnEngineChanged(DbEngine value)
    {
        OnPropertyChanged(nameof(IsPostgres));
        OnPropertyChanged(nameof(IsSqlServer));
        OnPropertyChanged(nameof(IsPostgresChecked));
        OnPropertyChanged(nameof(IsSqlServerChecked));
        OnPropertyChanged(nameof(ShowCredentials));

        if (value == DbEngine.SqlServer && Port == 5432) Port = 1433;
        else if (value == DbEngine.PostgreSql && Port == 1433) Port = 5432;
    }

    partial void OnSqlIntegratedSecurityChanged(bool value) => OnPropertyChanged(nameof(ShowCredentials));

    public ConnectionProfile Apply()
    {
        Source.Name = Name.Trim();
        Source.Engine = Engine;
        Source.Host = Host.Trim();
        Source.Port = Port;
        Source.Database = Database.Trim();
        Source.Username = Username.Trim();
        Source.DefaultSchema = string.IsNullOrWhiteSpace(DefaultSchema) ? null : DefaultSchema.Trim();
        Source.SqlIntegratedSecurity = SqlIntegratedSecurity;
        Source.EncryptedPasswordBase64 = PgBackupManager.Core.Services.SecretProtector.Protect(Password ?? "");
        return Source;
    }
}
