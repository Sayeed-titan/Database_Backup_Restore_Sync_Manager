using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Services;

namespace PgBackupManager.UI.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly ProfileStore _store = new();

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();

    [ObservableProperty] private ConnectionProfile? _selected;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public ProfilesViewModel() { Reload(); }

    public void Reload()
    {
        Profiles.Clear();
        foreach (var p in _store.LoadAll().OrderBy(p => p.Name))
            Profiles.Add(p);

        if (Profiles.Count == 0)
        {
            var defaultProfile = new ConnectionProfile
            {
                Name = "dcci (default)",
                Host = "180.94.20.106",
                Port = 5432,
                Database = "dcci",
                Username = "mkl_25",
                DefaultSchema = "mediklaud_dcci",
            };
            _store.Upsert(defaultProfile);
            Profiles.Add(defaultProfile);
            StatusText = "Seeded default 'dcci' profile (password not set). Click Edit to add the password.";
        }

        Selected ??= Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void Add()
    {
        var dlg = new Views.ProfileEditorWindow(new ConnectionProfile { Name = "New profile" }, isEdit: false) { Owner = App.Current.MainWindow };
        if (dlg.ShowDialog() == true)
        {
            _store.Upsert(dlg.Profile);
            Reload();
            Selected = Profiles.FirstOrDefault(p => p.Id == dlg.Profile.Id);
            StatusText = $"Saved profile '{dlg.Profile.Name}'.";
        }
    }

    [RelayCommand]
    private void Edit()
    {
        if (Selected is null) return;
        var dlg = new Views.ProfileEditorWindow(Selected) { Owner = App.Current.MainWindow };
        if (dlg.ShowDialog() == true)
        {
            _store.Upsert(dlg.Profile);
            var id = dlg.Profile.Id;
            Reload();
            Selected = Profiles.FirstOrDefault(p => p.Id == id);
            StatusText = $"Updated profile '{dlg.Profile.Name}'.";
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is null) return;
        var name = Selected.Name;
        _store.Delete(Selected.Id);
        Reload();
        StatusText = $"Deleted profile '{name}'.";
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Selected is null) return;
        try
        {
            IsBusy = true;
            StatusText = $"Testing connection to {Selected.Host}:{Selected.Port}/{Selected.Database}...";
            var password = SecretProtector.Unprotect(Selected.EncryptedPasswordBase64);
            var result = await ConnectionTester.TestAsync(Selected, password);
            if (result.Ok)
                StatusText = $"OK in {result.Elapsed.TotalMilliseconds:F0} ms — {result.ServerVersion}";
            else
                StatusText = $"FAILED: {result.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"ERROR: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
