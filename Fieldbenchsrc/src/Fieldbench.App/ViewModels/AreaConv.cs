using Avalonia.Data.Converters;
using Fieldbench.Core.Modbus;

namespace Fieldbench.App.ViewModels;

public static class AreaConv
{
    private sealed class Converter(RegisterArea area) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is RegisterArea a && a == area;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is true ? area : Avalonia.Data.BindingOperations.DoNothing;
    }

    public static readonly IValueConverter Holding = new Converter(RegisterArea.HoldingRegisters);
    public static readonly IValueConverter Input = new Converter(RegisterArea.InputRegisters);
    public static readonly IValueConverter Coils = new Converter(RegisterArea.Coils);
    public static readonly IValueConverter Discrete = new Converter(RegisterArea.DiscreteInputs);
}
