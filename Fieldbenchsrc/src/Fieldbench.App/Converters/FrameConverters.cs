using Avalonia.Data.Converters;
using Avalonia.Media;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Streams;

namespace Fieldbench.App.Converters;

public static class FrameConverters
{
    public static readonly IValueConverter Time =
        new FuncValueConverter<DateTime, string>(utc =>
        {
            var local = utc.ToLocalTime();
            return local.ToString("HH:mm:ss.fff");
        });

    public static readonly IValueConverter Delta =
        new FuncValueConverter<double?, string>(d => d is { } v ? v.ToString("0") : "");

    public static readonly IValueConverter DirText =
        new FuncValueConverter<Frame?, string>(f => f is null ? "" : f switch
        {
            // Passive bus tap: physical direction is all RX, show the inferred roles.
            { RoleInferred: true, Role: FrameRole.MasterToSlave } => "M→S",
            { RoleInferred: true, Role: FrameRole.SlaveToMaster } => "S→M",
            _ => f.Direction == StreamDirection.Tx ? "TX ↑" : "RX ↓",
        });

    public static readonly IValueConverter Bytes =
        new FuncValueConverter<long, string>(n => n switch
        {
            < 1024 => $"{n} B",
            < 1024 * 1024 => $"{n / 1024.0:0.#} KB",
            _ => $"{n / (1024.0 * 1024.0):0.#} MB",
        });

    public static readonly IValueConverter HexPreview =
        new FuncValueConverter<Frame?, string>(f =>
        {
            if (f is null) return "";
            const int max = 24;
            var hex = f.HexString(max);
            return f.Bytes.Length > max ? hex + " …" : hex;
        });

    public static readonly IValueConverter AsciiPreview =
        new FuncValueConverter<Frame?, string>(f => f is null ? "" : RawLens.AsciiPreview(f.Bytes, 20));

    public static readonly IValueConverter Duration =
        new FuncValueConverter<TimeSpan?, string>(t => t is { } v ? v.ToString(v.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss") : "");

    public static readonly IValueConverter IsNotNull =
        new FuncValueConverter<object?, bool>(o => o is not null);

    public static readonly IValueConverter IsNull =
        new FuncValueConverter<object?, bool>(o => o is null);

    public static readonly IValueConverter CountToVisible =
        new FuncValueConverter<int, bool>(c => c > 0);
}

/// <summary>Field kind → the fixed four-color board (list, bytes and legend share it).</summary>
public sealed class FieldKindBrushConverter : IValueConverter
{
    public bool Tint { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not FieldKind kind) return null;
        var app = Avalonia.Application.Current!;
        string key = kind switch
        {
            FieldKind.Address => Tint ? "FbAddrTint" : "FbFieldAddr",
            FieldKind.Function => Tint ? "FbFuncTint" : "FbFieldFunc",
            FieldKind.Length => Tint ? "FbLenTint" : "FbFieldLen",
            FieldKind.Checksum => Tint ? "FbAsh" : "FbFieldData",
            FieldKind.ChecksumBad => Tint ? "FbRedTint" : "FbRed",
            _ => Tint ? "FbAsh" : "FbFieldData",
        };
        app.TryGetResource(key, app.ActualThemeVariant, out var brush);
        return brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
