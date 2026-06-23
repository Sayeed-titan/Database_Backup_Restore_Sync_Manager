using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PgBackupManager.UI.Controls;

public partial class Spinner : UserControl
{
    public Spinner() { InitializeComponent(); }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Spinner),
            new PropertyMetadata(Brushes.White, OnStrokeChanged));

    private static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Spinner s) s.arc.Stroke = (Brush)e.NewValue;
    }
}
