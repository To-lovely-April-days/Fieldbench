using Avalonia.Data.Converters;

namespace Fieldbench.App.ViewModels;

public static class ChartWindowConv
{
    private sealed class WindowConverter(ChartWindow window) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is ChartWindow w && w == window;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is true ? window : Avalonia.Data.BindingOperations.DoNothing;
    }

    public static readonly IValueConverter Sixty = new WindowConverter(ChartWindow.Sixty);
    public static readonly IValueConverter TenMin = new WindowConverter(ChartWindow.TenMinutes);
    public static readonly IValueConverter All = new WindowConverter(ChartWindow.All);
}
