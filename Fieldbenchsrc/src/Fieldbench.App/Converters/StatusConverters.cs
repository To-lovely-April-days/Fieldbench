using Avalonia.Data.Converters;
using Avalonia.Media;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Streams;

namespace Fieldbench.App.Converters;

/// <summary>Row / tag styling driven by frame status and direction.</summary>
public static class StatusConverters
{
    private static IBrush? Res(string key)
    {
        var app = Avalonia.Application.Current!;
        app.TryGetResource(key, app.ActualThemeVariant, out var value);
        return value as IBrush;
    }

    public static readonly IValueConverter IsWarning =
        new FuncValueConverter<FrameStatus, bool>(s => s == FrameStatus.Warning);

    public static readonly IValueConverter IsError =
        new FuncValueConverter<FrameStatus, bool>(s => s == FrameStatus.Error);

    public static readonly IValueConverter DirBrush =
        new FuncValueConverter<Frame?, IBrush?>(f =>
        {
            if (f is null) return null;
            if (f is { RoleInferred: true, Role: FrameRole.MasterToSlave }) return Res("FbAmber");
            return f.Direction == StreamDirection.Tx ? Res("FbAmber") : Res("FbRx");
        });

    public static readonly IValueConverter TagBackground =
        new FuncValueConverter<Frame?, IBrush?>(f => f?.Status switch
        {
            FrameStatus.Warning => Res("FbAmberTint"),
            FrameStatus.Error => Res("FbRedTint"),
            _ => Brushes.Transparent,
        });

    public static readonly IValueConverter TagForeground =
        new FuncValueConverter<Frame?, IBrush?>(f => f?.Status switch
        {
            FrameStatus.Warning => Res("FbAmber"),
            FrameStatus.Error => Res("FbRed"),
            _ => Res("FbGreen"),
        });
}
