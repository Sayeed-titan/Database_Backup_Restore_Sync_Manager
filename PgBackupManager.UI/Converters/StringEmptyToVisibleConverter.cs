using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PgBackupManager.UI.Converters;

/// <summary>Visible when the bound string is null/empty (used for watermark overlays); Collapsed otherwise.
/// ConverterParameter="invert" flips it (visible only when there IS text).</summary>
public sealed class StringEmptyToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = string.IsNullOrEmpty(value as string);
        if (parameter as string == "invert") empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
