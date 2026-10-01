using System;
using System.Globalization;
using System.Windows.Data;

namespace PgBackupManager.UI.Converters;

/// <summary>true -> false and back (e.g. IsReadOnly = !IsEditing).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
