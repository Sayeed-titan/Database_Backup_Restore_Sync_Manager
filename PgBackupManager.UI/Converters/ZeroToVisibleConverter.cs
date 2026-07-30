using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PgBackupManager.UI.Converters;

/// <summary>Visible when a bound integer count is 0; collapsed otherwise — the
/// inverse of ZeroToCollapsedConverter, used to show an empty-state message
/// exactly when the list it stands in for has nothing in it.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var n = value is int i ? i : 0;
        return n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
