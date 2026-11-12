using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopTimeTracker.Controls;

/// <summary>
/// WPF's Border does not clip child content to its CornerRadius, so a DataGrid inside a
/// rounded card paints square corners over the curve. This attaches a matching geometry clip.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(RoundedClip), new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);
    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        if (e.NewValue is true)
        {
            border.SizeChanged += OnSizeChanged;
            Apply(border);
        }
        else
        {
            border.SizeChanged -= OnSizeChanged;
            border.Clip = null;
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((Border)sender);

    private static void Apply(Border border)
    {
        if (border.ActualWidth <= 0 || border.ActualHeight <= 0) return;
        double radius = border.CornerRadius.TopLeft;
        border.Clip = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), radius, radius);
    }
}