using System;
using System.Globalization;
using System.Windows.Data;
using PgBackupManager.Core.Models;

namespace PgBackupManager.UI.Converters;

/// <summary>DbEngine -> a short badge label ("PG" / "MSSQL") for the Profiles list.</summary>
public sealed class DbEngineBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DbEngine.SqlServer => "MSSQL",
        DbEngine.PostgreSql => "PG",
        _ => "?"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
