using System.Windows;
using System.Windows.Media;

namespace SmartDock.App.Controls;

/// <summary>
/// Minimal retained-mode line graph for 0..100 samples. Draws once per data change (no timers,
/// no per-frame work), so an idle dock costs nothing to render.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(
        nameof(FillOpacity), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.22, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double FillOpacity
    {
        get => (double)GetValue(FillOpacityProperty);
        set => SetValue(FillOpacityProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is null || values.Count < 2 || w <= 0 || h <= 0)
        {
            return;
        }

        const int capacity = 30;
        var step = w / (capacity - 1);
        var startX = w - ((values.Count - 1) * step);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var x = startX + (i * step);
                var y = h - (Math.Clamp(values[i], 0, 100) / 100.0 * (h - 1.5)) - 0.75;
                var p = new Point(x, y);
                if (i == 0)
                {
                    lc.BeginFigure(p, false, false);
                    ac.BeginFigure(new Point(x, h), true, true);
                    ac.LineTo(p, false, false);
                }
                else
                {
                    lc.LineTo(p, true, true);
                    ac.LineTo(p, false, false);
                }
            }

            ac.LineTo(new Point(w, h), false, false);
        }

        line.Freeze();
        area.Freeze();

        var fill = Stroke.CloneCurrentValue();
        fill.Opacity = FillOpacity;
        fill.Freeze();

        var pen = new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();

        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>Thin rounded progress meter for 0..100 values.</summary>
public sealed class Meter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Meter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Meter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(Meter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), r, r);
        var fw = Math.Clamp(Value, 0, 100) / 100.0 * w;
        if (fw > 0.5)
        {
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(fw, h), h), r, r);
        }
    }
}
