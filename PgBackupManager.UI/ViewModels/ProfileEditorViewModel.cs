using CommunityToolkit.Mvvm.ComponentModel;
using PgBackupManager.Core.Models;

namespace PgBackupManager.UI.ViewModels;

public partial class ProfileEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "localhost";
    [ObservableProperty] private int _port = 5432;
    [ObservableProperty] private string _database = "postgres";
    [ObservableProperty] private string _username = "postgres";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _defaultSchema = "";

    public ConnectionProfile Source { get; }

    public ProfileEditorViewModel(ConnectionProfile profile)
    {
        Source = profile;
        Name = profile.Name;
        Host = profile.Host;
        Port = profile.Port;
        Database = profile.Database;
        Username = profile.Username;
        DefaultSchema = profile.DefaultSchema ?? "";
        Password = PgBackupManager.Core.Services.SecretProtector.Unprotect(profile.EncryptedPasswordBase64);
    }

    public ConnectionProfile Apply()
    {
        Source.Name = Name.Trim();
        Source.Host = Host.Trim();
        Source.Port = Port;
        Source.Database = Database.Trim();
        Source.Username = Username.Trim();
        Source.DefaultSchema = string.IsNullOrWhiteSpace(DefaultSchema) ? null : DefaultSchema.Trim();
        Source.EncryptedPasswordBase64 = PgBackupManager.Core.Services.SecretProtector.Protect(Password ?? "");
        return Source;
    }
}
