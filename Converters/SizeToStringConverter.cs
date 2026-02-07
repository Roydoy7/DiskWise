using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DiskWise.Models;

namespace DiskWise.Converters;

/// <summary>
/// Proxy to forward DataContext into resources that exist outside the visual tree (e.g. ContextMenu)
/// </summary>
public class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public object Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));
}

/// <summary>
/// Converts byte size to human-readable string
/// </summary>
public class SizeToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long size)
        {
            if (size < 0) return "--";
            return FileSystemItem.FormatSize(size);
        }
        return "--";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts advice level to color brush
/// </summary>
public class AdviceLevelToColorConverter : IValueConverter
{
    private static readonly System.Windows.Media.SolidColorBrush SafeBrush =
        new(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly System.Windows.Media.SolidColorBrush CautionBrush =
        new(System.Windows.Media.Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly System.Windows.Media.SolidColorBrush DangerBrush =
        new(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36));
    private static readonly System.Windows.Media.SolidColorBrush UnknownBrush =
        new(System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AdviceLevel level)
        {
            return level switch
            {
                AdviceLevel.Safe => SafeBrush,
                AdviceLevel.Caution => CautionBrush,
                AdviceLevel.Danger => DangerBrush,
                _ => UnknownBrush
            };
        }
        return UnknownBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean or object (null check) to visibility
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter?.ToString() == "Invert";
        bool isVisible;

        if (value is bool b)
        {
            isVisible = b;
        }
        else if (value is int count)
        {
            isVisible = count > 0;
        }
        else
        {
            // For object types, check if not null
            isVisible = value != null;
        }

        if (invert) isVisible = !isVisible;
        return isVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts percentage to progress bar width
/// </summary>
public class PercentageToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percentage)
        {
            double maxWidth = 100; // Default max width
            if (parameter is double max)
            {
                maxWidth = max;
            }
            return percentage / 100 * maxWidth;
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
