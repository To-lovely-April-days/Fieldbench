using Avalonia.Data.Converters;

namespace Fieldbench.App.ViewModels;

/// <summary>Tiny layout converters used by gauges and meters.</summary>
public static class UiMath
{
    /// <summary>0..1 ratio → width of the 44px status-bar buffer gauge.</summary>
    public static readonly IValueConverter Times44 =
        new FuncValueConverter<double, double>(v => Math.Clamp(v, 0, 1) * 44);

    /// <summary>0..1 ratio → width of the 56px AI cause meter.</summary>
    public static readonly IValueConverter Times56 =
        new FuncValueConverter<double, double>(v => Math.Clamp(v, 0, 1) * 56);

    /// <summary>0..1 ratio → width of the 220px settings usage meter.</summary>
    public static readonly IValueConverter Times220 =
        new FuncValueConverter<double, double>(v => Math.Clamp(v, 0, 1) * 220);

    /// <summary>0..1 ratio → width of the 480px scan progress bar.</summary>
    public static readonly IValueConverter Times480 =
        new FuncValueConverter<double, double>(v => Math.Clamp(v, 0, 1) * 480);
}
