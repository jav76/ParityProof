using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ParityProof.App.Converters;

public sealed class TabActiveBackgroundConverter : IValueConverter
{
    public static readonly TabActiveBackgroundConverter Instance = new();

    private static readonly IBrush InactiveBrush = new SolidColorBrush(Color.Parse("#1E293B"));

    private static readonly Dictionary<string, IBrush> ActiveBrushes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["All"] = new SolidColorBrush(Color.Parse("#1E3A8A")),
        ["Verified"] = new SolidColorBrush(Color.Parse("#064E3B")),
        ["Missing"] = new SolidColorBrush(Color.Parse("#881337")),
        ["Corrupt"] = new SolidColorBrush(Color.Parse("#78350F")),
        ["Duplicates"] = new SolidColorBrush(Color.Parse("#7C2D12"))
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string currentTab && parameter is string targetTab)
        {
            if (string.Equals(currentTab, targetTab, StringComparison.OrdinalIgnoreCase))
            {
                if (ActiveBrushes.TryGetValue(targetTab, out IBrush? activeBrush))
                {
                    return activeBrush;
                }
                return new SolidColorBrush(Color.Parse("#334155"));
            }
        }

        return InactiveBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class TabActiveBorderBrushConverter : IValueConverter
{
    public static readonly TabActiveBorderBrushConverter Instance = new();

    private static readonly IBrush InactiveBrush = new SolidColorBrush(Color.Parse("#334155"));

    private static readonly Dictionary<string, IBrush> ActiveBrushes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["All"] = new SolidColorBrush(Color.Parse("#3B82F6")),
        ["Verified"] = new SolidColorBrush(Color.Parse("#10B981")),
        ["Missing"] = new SolidColorBrush(Color.Parse("#F43F5E")),
        ["Corrupt"] = new SolidColorBrush(Color.Parse("#F59E0B")),
        ["Duplicates"] = new SolidColorBrush(Color.Parse("#F97316"))
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string currentTab && parameter is string targetTab)
        {
            if (string.Equals(currentTab, targetTab, StringComparison.OrdinalIgnoreCase))
            {
                if (ActiveBrushes.TryGetValue(targetTab, out IBrush? activeBrush))
                {
                    return activeBrush;
                }
                return new SolidColorBrush(Color.Parse("#3B82F6"));
            }
        }

        return InactiveBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
