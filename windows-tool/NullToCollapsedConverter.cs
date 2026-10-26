using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OmenPrep;

/// <summary>Collapses a TextBlock when its bound string is null/empty (used for the optional "Detail" line in ResultRow).</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
