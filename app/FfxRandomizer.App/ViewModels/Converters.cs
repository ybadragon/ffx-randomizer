using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FfxRandomizer.App.ViewModels;

public static class Converters
{
    public static IValueConverter HideWhenTrue { get; } = new Lambda(v => v is true ? Visibility.Collapsed : Visibility.Visible);
    public static IValueConverter ShowWhenNotNull { get; } = new Lambda(v => v is null ? Visibility.Collapsed : Visibility.Visible);
    public static IValueConverter ShowWhenZero { get; } = new Lambda(v => v is 0 ? Visibility.Visible : Visibility.Collapsed);

    private sealed class Lambda(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
