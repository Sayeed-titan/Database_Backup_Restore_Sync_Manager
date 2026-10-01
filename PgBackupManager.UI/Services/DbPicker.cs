using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using PgBackupManager.Core.Models;
using PgBackupManager.Core.Providers;

namespace PgBackupManager.UI.Services;

// Fills a "database" dropdown for a connection (SQL Server / PostgreSQL list
// every database on the server; other engines have no such notion).
public static class DbPicker
{
    // Returns the database to select: `prefer` if the server has it, else the
    // profile's own, else the first one listed.
    public static async Task<string?> LoadAsync(ConnectionProfile? p, ObservableCollection<string> into, string? prefer)
    {
        into.Clear();
        if (p == null) return null;
        if (p.Engine is not (DbEngine.SqlServer or DbEngine.PostgreSql)) return p.Database;
        try
        {
            var prov = DbProviders.For(p);
            await using var c = await prov.OpenAsync(p);
            foreach (var d in await prov.ListDatabasesAsync(c)) into.Add(d);
        }
        catch { /* offline / no permission: fall back to the profile's own database */ }
        if (!string.IsNullOrEmpty(p.Database) && !into.Contains(p.Database, StringComparer.OrdinalIgnoreCase)) into.Insert(0, p.Database);
        string? Find(string? n) => string.IsNullOrEmpty(n) ? null : into.FirstOrDefault(d => string.Equals(d, n, StringComparison.OrdinalIgnoreCase));
        return Find(prefer) ?? Find(p.Database) ?? into.FirstOrDefault();
    }
}
