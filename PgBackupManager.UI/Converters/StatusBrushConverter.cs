using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PgBackupManager.UI.Converters;

/// <summary>Colors the footer status text/dot: OK=green, ERROR/FAIL=red, cancel=amber, else teal.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush Teal = new SolidColorBrush(Color.FromRgb(0x0A, 0x7D, 0x9A));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var t = value as string ?? "";
        if (t.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0) return Red;
        if (t.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0) return Amber;
        if (t.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("complete", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0) return Green;
        return Teal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
