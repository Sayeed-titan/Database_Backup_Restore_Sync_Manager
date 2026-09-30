using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;
using PgBackupManager.Core.Services;
using PgBackupManager.Core.Sql;
using PgBackupManager.UI.Services;

namespace PgBackupManager.UI.ViewModels;

public partial class ConverterViewModel : ObservableObject
{
    public IReadOnlyList<EngineOption> Engines => ProfileEditorViewModel.AllEngines;
    [ObservableProperty] private EngineOption? _from;
    [ObservableProperty] private EngineOption? _to;
    [ObservableProperty] private string _sourceSchema = "";
    [ObservableProperty] private string _targetSchema = "";
    [ObservableProperty] private bool _packageAsSchema = true;
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _statusText = "Paste Oracle / SQL Server / MySQL / PostgreSQL code on the left, or load objects straight from a database.";
    public ObservableCollection<string> Warnings { get; } = new();

    // Load-from-database
    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();
    [ObservableProperty] private ConnectionProfile? _profile;
    public ObservableCollection<string> Schemas { get; } = new();
    [ObservableProperty] private string? _schema;
    public ObservableCollection<TransferItem> Objects { get; } = new();
    [ObservableProperty] private bool _isBusy;

    public ConverterViewModel()
    {
        From = Engines.First(e => e.Engine == DbEngine.Oracle);
        To = Engines.First(e => e.Engine == DbEngine.PostgreSql);
        foreach (var p in new ProfileStore().LoadAll().OrderBy(p => p.Name)) Profiles.Add(p);
        Profile = Profiles.FirstOrDefault(p => p.Engine == DbEngine.Oracle) ?? Profiles.FirstOrDefault();
        ProfileStore.Changed += (_, _) => Application.Current?.Dispatcher.Invoke(() =>
        {
            var id = Profile?.Id;
            Profiles.Clear();
            foreach (var p in new ProfileStore().LoadAll().OrderBy(p => p.Name)) Profiles.Add(p);
            Profile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault();
        });
        ConverterNavigator.Requested += (_, r) => Application.Current?.Dispatcher.Invoke(() =>
        {
            From = Engines.First(e => e.Engine == r.From);
            if (To?.Engine == r.From) To = Engines.First(e => e.Engine != r.From);
            SourceSchema = r.Schema;
            Source = r.Source;
            Convert();
        });
    }

    partial void OnProfileChanged(ConnectionProfile? value)
    {
        Schemas.Clear(); Objects.Clear();
        if (value != null) From = Engines.First(e => e.Engine == value.Engine);
    }

    partial void OnSchemaChanged(string? value) => _ = LoadObjectListAsync();

    [RelayCommand]
    private async Task LoadSchemasAsync()
    {
        if (Profile == null) return;
        try
        {
            IsBusy = true;
            var p = DbProviders.For(Profile);
            await using var c = await p.OpenAsync(Profile);
            Schemas.Clear();
            foreach (var s in await p.ListSchemasAsync(c)) Schemas.Add(s);
            Schema = Schemas.FirstOrDefault(s => string.Equals(s, Profile.ResolveDefaultSchema(), StringComparison.OrdinalIgnoreCase)) ?? Schemas.FirstOrDefault();
        }
        catch (Exception ex) { StatusText = "ERROR: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task LoadObjectListAsync()
    {
        if (Profile == null || Schema == null) return;
        try
        {
            IsBusy = true;
            var p = DbProviders.For(Profile);
            await using var c = await p.OpenAsync(Profile);
            Objects.Clear();
            foreach (var o in (await p.ListObjectsAsync(c, Schema)).Where(o => o.Type != DbObjectType.Table))
                Objects.Add(new TransferItem { Obj = o });
            StatusText = $"{Objects.Count} code object(s) in {Schema} — tick the ones to convert, then Load Source.";
        }
        catch (Exception ex) { StatusText = "ERROR: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LoadSourceAsync()
    {
        if (Profile == null || Schema == null) return;
        var picked = Objects.Where(o => o.IsChecked).ToList();
        if (picked.Count == 0) { StatusText = "Tick at least one object."; return; }
        try
        {
            IsBusy = true;
            var p = DbProviders.For(Profile);
            await using var c = await p.OpenAsync(Profile);
            var parts = new List<string>();
            foreach (var o in picked)
            {
                var src = await p.GetObjectSourceAsync(c, o.Obj);
                if (string.IsNullOrWhiteSpace(src)) { parts.Add($"-- {o.Obj.Type} {o.Name}: no source available (permissions?)"); continue; }
                // Oracle units must be separated by "/" so the converter sees each one.
                parts.Add(p.Engine == DbEngine.Oracle && !src.TrimEnd().EndsWith("/") ? src.TrimEnd() + "\n/" : p.Engine == DbEngine.SqlServer ? src.TrimEnd() + "\nGO" : src);
            }
            Source = string.Join("\n\n", parts);
            SourceSchema = Schema;
            Convert();
        }
        catch (Exception ex) { StatusText = "ERROR: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Convert()
    {
        if (From == null || To == null) return;
        if (string.IsNullOrWhiteSpace(Source)) { StatusText = "Nothing to convert."; return; }
        var r = SqlCodeConverter.Convert(Source, From.Engine, To.Engine, new ConvertContext
        {
            SourceSchema = string.IsNullOrWhiteSpace(SourceSchema) ? null : SourceSchema.Trim(),
            TargetSchema = string.IsNullOrWhiteSpace(TargetSchema) ? null : TargetSchema.Trim(),
            PackageAsSchema = PackageAsSchema,
        });
        Output = r.Sql;
        Warnings.Clear();
        foreach (var w in r.Warnings) Warnings.Add(w);
        var todos = r.Sql.Split("TODO(convert)").Length - 1;
        StatusText = $"Converted {From.Label} → {To.Label}: {r.Warnings.Count} warning(s), {todos} TODO marker(s) to review.";
    }

    [RelayCommand] private void SwapEngines() { (From, To) = (To, From); (Source, Output) = (Output, ""); }

    [RelayCommand] private void CopyOutput() { if (Output.Length > 0) Clipboard.SetText(Output); StatusText = "Copied to clipboard."; }

    [RelayCommand] private void OpenInEditor() { if (Output.Length > 0) Navigator.OpenSql($"Converted ({To?.Label})", Output); }

    [RelayCommand]
    private void OpenSourceFile()
    {
        var dlg = new OpenFileDialog { Filter = "SQL / PL/SQL (*.sql;*.pks;*.pkb;*.prc;*.fnc;*.trg;*.txt)|*.sql;*.pks;*.pkb;*.prc;*.fnc;*.trg;*.txt|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true) { Source = File.ReadAllText(dlg.FileName); Convert(); }
    }

    [RelayCommand]
    private void SaveOutput()
    {
        if (Output.Length == 0) return;
        var dlg = new SaveFileDialog { Filter = "SQL (*.sql)|*.sql", FileName = "converted.sql" };
        if (dlg.ShowDialog() == true) { File.WriteAllText(dlg.FileName, Output); StatusText = $"Saved {dlg.FileName}"; }
    }
}
