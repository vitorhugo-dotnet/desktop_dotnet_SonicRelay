using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SonicRelay.Windows.Desktop.Controls;

/// <summary>
/// A lightweight horizontal meter (issue #32 component). Draws a rounded track with a fill
/// proportional to <see cref="Value"/> (0..1). It renders a single primitive, avoiding the
/// heavy templated <see cref="ProgressBar"/> and any per-frame animation that could
/// interfere with capture — the desktop-UX constraint from issue #32. Reused by the audio
/// monitor, the quality tiles and the bandwidth gauge.
/// </summary>
public sealed class MetricProgressBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<MetricProgressBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<MetricProgressBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<MetricProgressBar, IBrush?>(nameof(TrackBrush));

    static MetricProgressBar()
    {
        AffectsRender<MetricProgressBar>(ValueProperty, FillProperty, TrackBrushProperty);
    }

    public MetricProgressBar()
    {
        Height = 8;
        MinWidth = 40;
    }

    /// <summary>Fill fraction, clamped to 0..1 at render time.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var radius = bounds.Height / 2;
        var track = new Rect(0, 0, bounds.Width, bounds.Height);
        if (TrackBrush is { } trackBrush)
            context.DrawRectangle(trackBrush, null, track, radius, radius);

        var fraction = Math.Clamp(Value, 0d, 1d);
        if (fraction <= 0 || Fill is not { } fill) return;

        // Never let the fill collapse below its own corner diameter, or the rounded cap
        // would render as a sliver for tiny-but-nonzero values.
        var fillWidth = Math.Max(bounds.Height, bounds.Width * fraction);
        var filled = new Rect(0, 0, Math.Min(fillWidth, bounds.Width), bounds.Height);
        context.DrawRectangle(fill, null, filled, radius, radius);
    }
}
