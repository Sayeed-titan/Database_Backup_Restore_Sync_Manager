using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PgBackupManager.UI.Converters;

/// <summary>Colors a terminal log line: success=green, ">>"=amber, errors=red, else muted grey.</summary>
public sealed class LogLineBrushConverter : IValueConverter
{
    private static readonly Brush Success = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? "";
        if (text.IndexOf("SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0) return Success;
        if (text.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0) return Error;
        if (text.StartsWith(">>")) return Warn;
        return Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
