using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PgBackupManager.UI.Converters;

/// <summary>Collapses an element when a bound integer count is 0; visible otherwise.</summary>
public sealed class ZeroToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var n = value is int i ? i : 0;
        return n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
