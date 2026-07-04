using Avalonia.Data.Converters;

namespace Fieldbench.App.ViewModels;

/// <summary>Two-way int↔bool converters for radio-button tab strips.</summary>
public static class TabIndex
{
    private sealed class IndexConverter(int index) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is int i && i == index;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is true ? index : Avalonia.Data.BindingOperations.DoNothing;
    }

    public static readonly IValueConverter Zero = new IndexConverter(0);
    public static readonly IValueConverter One = new IndexConverter(1);
    public static readonly IValueConverter Two = new IndexConverter(2);
    public static readonly IValueConverter Three = new IndexConverter(3);
}
