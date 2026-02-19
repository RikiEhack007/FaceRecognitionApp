using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FaceRecApp.WPF.Converters;

/// <summary>
/// Static converter instances for use in XAML via {x:Static local:Converters.XXX}.
/// This avoids declaring converters as resources in every window.
/// </summary>
public static class Converters
{
    public static readonly IValueConverter CameraButtonText = new CameraButtonTextConverter();
    public static readonly IValueConverter InverseBoolToVis = new InverseBoolToVisibilityConverter();
    public static readonly IValueConverter ZeroToVisible = new ZeroToVisibleConverter();
    public static readonly IValueConverter BoolToVis = new BoolToVisibilityConverter();
    public static readonly IValueConverter NullToVis = new NullToVisibilityConverter();
}

/// <summary>
/// Converts bool (IsCameraRunning) to button text: "Stop Camera" / "Start Camera".
/// </summary>
public class CameraButtonTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? "Stop Camera" : "Start Camera";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts bool to Visibility: true = Collapsed, false = Visible (inverse).
/// Used for showing placeholder when camera is off.
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts bool to Visibility: true = Visible, false = Collapsed.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Shows element when count is zero: 0 = Visible, 1+ = Collapsed.
/// Used for "No faces detected" placeholder text.
/// </summary>
public class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count)
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Shows element when value is not null: null = Collapsed, non-null = Visible.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
