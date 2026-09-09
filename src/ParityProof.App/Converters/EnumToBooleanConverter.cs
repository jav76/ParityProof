using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace ParityProof.App.Converters;

public sealed class EnumToBooleanConverter : IValueConverter
{
    public static readonly EnumToBooleanConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        if (value is Enum && parameter is string strParam)
        {
            if (Enum.TryParse(value.GetType(), strParam, ignoreCase: true, out object? parsed))
            {
                return value.Equals(parsed);
            }

            return false;
        }

        return value.Equals(parameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            if (targetType.IsEnum && parameter is string strParam)
            {
                return Enum.Parse(targetType, strParam, ignoreCase: true);
            }

            return parameter;
        }

        return BindingOperations.DoNothing;
    }
}
