using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Fieldbench.App.ViewModels;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Master;

namespace Fieldbench.App.Controls;

/// <summary>
/// The Tesla-minimal multi-channel line chart: hairline grid, categorical
/// channel colors, red markers on abnormal frames; clicking a marker raises
/// MarkerClicked → timeline jump (the "value wrong → which frame" link).
/// Custom-drawn — three channels at poll rate are trivial for immediate mode.
/// </summary>
public sealed class FieldChart : Control
{
    public static readonly StyledProperty<ChartPanelViewModel?> ChartProperty =
        AvaloniaProperty.Register<FieldChart, ChartPanelViewModel?>(nameof(Chart));

    public static readonly StyledProperty<long> RevisionProperty =
        AvaloniaProperty.Register<FieldChart, long>(nameof(Revision));

    private readonly List<(Rect Hit, Frame Frame)> _markerHits = new();

    static FieldChart()
    {
        AffectsRender<FieldChart>(ChartProperty, RevisionProperty);
    }

    public ChartPanelViewModel? Chart
    {
        get => GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    /// <summary>Bump to trigger a redraw (bound to the VM's Revision counter).</summary>
    public long Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public event Action<Frame>? MarkerClicked;

    public override void Render(DrawingContext context)
    {
        _markerHits.Clear();
        var chart = Chart;
        if (chart is null || Bounds.Width < 20 || Bounds.Height < 20) return;

        double w = Bounds.Width, h = Bounds.Height;
        var app = Avalonia.Application.Current!;
        app.TryGetResource("FbCloud", app.ActualThemeVariant, out var cloudObj);
        app.TryGetResource("FbRed", app.ActualThemeVariant, out var redObj);
        var gridPen = new Pen(cloudObj as IBrush ?? Brushes.LightGray, 1);
        var redBrush = redObj as IBrush ?? Brushes.Red;

        // Hairline horizontal grid, thirds.
        for (int i = 1; i <= 3; i++)
        {
            double y = h * i / 4;
            context.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        var window = chart.WindowSpan;
        var now = DateTime.UtcNow;
        DateTime tMin, tMax;
        if (window == TimeSpan.MaxValue)
        {
            tMin = DateTime.MaxValue;
            tMax = DateTime.MinValue;
            foreach (var ch in chart.Channels)
            {
                var s = ch.Samples(window);
                if (s.Count > 0)
                {
                    if (s[0].TimestampUtc < tMin) tMin = s[0].TimestampUtc;
                    if (s[^1].TimestampUtc > tMax) tMax = s[^1].TimestampUtc;
                }
            }

            if (tMin >= tMax) return;
        }
        else
        {
            tMax = now;
            tMin = now - window;

            // Young capture: grow from the left instead of hugging the right edge.
            DateTime earliest = DateTime.MaxValue;
            foreach (var ch in chart.Channels)
            {
                var s = ch.Samples(window);
                if (s.Count > 0 && s[0].TimestampUtc < earliest) earliest = s[0].TimestampUtc;
            }

            if (earliest != DateTime.MaxValue && earliest > tMin) tMin = earliest;
        }

        double timeSpanSec = Math.Max(0.001, (tMax - tMin).TotalSeconds);

        foreach (var channel in chart.Channels)
        {
            var samples = channel.Samples(window);
            if (samples.Count < 2) continue;

            double min = double.MaxValue, max = double.MinValue;
            foreach (var s in samples)
            {
                if (s.Value < min) min = s.Value;
                if (s.Value > max) max = s.Value;
            }

            double range = max - min;
            if (range < 1e-9) range = Math.Abs(max) > 1e-9 ? Math.Abs(max) * 0.1 : 1;
            min -= range * 0.12;
            range *= 1.24;

            var brush = new SolidColorBrush(Color.Parse(channel.ColorHex));
            var pen = new Pen(brush, 1.6);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool first = true;
                foreach (var s in samples)
                {
                    double x = (s.TimestampUtc - tMin).TotalSeconds / timeSpanSec * w;
                    double y = h - (s.Value - min) / range * h;
                    if (first)
                    {
                        ctx.BeginFigure(new Point(x, y), false);
                        first = false;
                    }
                    else
                    {
                        ctx.LineTo(new Point(x, y));
                    }
                }

                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        // Abnormal frame markers: dashed vertical + red dot, clickable.
        foreach (var (time, _, frame) in chart.ErrorMarkers)
        {
            if (time < tMin || time > tMax) continue;
            double x = (time - tMin).TotalSeconds / timeSpanSec * w;
            var dashPen = new Pen(redBrush, 1) { DashStyle = new DashStyle([3, 3], 0), };
            context.DrawLine(dashPen, new Point(x, 4), new Point(x, h - 4));
            var dotCenter = new Point(x, h * 0.6);
            context.DrawEllipse(redBrush, null, dotCenter, 4, 4);
            _markerHits.Add((new Rect(x - 8, 0, 16, h), frame));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        foreach (var (hit, frame) in _markerHits)
        {
            if (hit.Contains(pos))
            {
                MarkerClicked?.Invoke(frame);
                e.Handled = true;
                return;
            }
        }
    }
}
