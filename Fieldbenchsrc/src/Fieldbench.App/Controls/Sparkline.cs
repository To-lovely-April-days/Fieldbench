using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Fieldbench.App.Controls;

/// <summary>Tiny inline trend line for the register grid (64×16, single stroke).</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (values is null || values.Count < 2 || Bounds.Width < 4 || Bounds.Height < 4) return;

        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in values)
        {
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (min > max) return;
        double range = max - min;
        if (range < 1e-9) range = 1;

        double w = Bounds.Width, h = Bounds.Height - 2;
        var brush = Stroke ?? Brushes.SteelBlue;
        var pen = new Pen(brush, 1.25);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < values.Count; i++)
            {
                double x = values.Count == 1 ? 0 : i * w / (values.Count - 1);
                double y = 1 + h - (values[i] - min) / range * h;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false);
                else ctx.LineTo(new Point(x, y));
            }

            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
