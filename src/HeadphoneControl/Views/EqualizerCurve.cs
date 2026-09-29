using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HeadphoneControl.ViewModels;

namespace HeadphoneControl.Views;

// Preview of the band curve; each band sits at the centre of an equal-width column so it lines up with the sliders below.
internal sealed class EqualizerCurve : Control
{
    public static readonly StyledProperty<IReadOnlyList<EqualizerBandViewModel>?> BandsProperty =
        AvaloniaProperty.Register<EqualizerCurve, IReadOnlyList<EqualizerBandViewModel>?>(nameof(Bands));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<EqualizerCurve, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> BaselineBrushProperty =
        AvaloniaProperty.Register<EqualizerCurve, IBrush?>(nameof(BaselineBrush));

    private const double Range = 10;
    private const double Inset = 3;

    private IReadOnlyList<EqualizerBandViewModel>? _subscribed;
    private bool _isAttached;

    static EqualizerCurve()
    {
        AffectsRender<EqualizerCurve>(BandsProperty, StrokeProperty, BaselineBrushProperty);
    }

    public IReadOnlyList<EqualizerBandViewModel>? Bands
    {
        get => GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? BaselineBrush
    {
        get => GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var middle = Bounds.Height / 2;
        if (BaselineBrush is { } baseline)
        {
            context.DrawLine(new Pen(baseline, 1, new DashStyle([3, 4], 0)), new Point(0, middle), new Point(width, middle));
        }

        if (Bands is not { Count: > 0 } bands || Stroke is not { } stroke)
        {
            return;
        }

        var step = width / bands.Count;
        var points = bands
            .Select((band, i) => new Point(step * (i + 0.5), middle - (band.Value / Range * (middle - Inset))))
            .ToArray();

        context.DrawGeometry(FillBrush(stroke), null, BuildCurve(points, width, closeAt: middle));
        context.DrawGeometry(null, new Pen(stroke, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), BuildCurve(points, width, closeAt: null));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BandsProperty && _isAttached)
        {
            Resubscribe(Bands);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Resubscribe(Bands);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        Resubscribe(null);
    }

    private static IBrush FillBrush(IBrush stroke) =>
        stroke is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.12) : Brushes.Transparent;

    private static StreamGeometry BuildCurve(Point[] points, double width, double? closeAt)
    {
        var geometry = new StreamGeometry();
        using var figure = geometry.Open();
        figure.BeginFigure(new Point(0, points[0].Y), isFilled: closeAt is not null);
        figure.LineTo(points[0]);
        for (var i = 1; i < points.Length; i++)
        {
            var (previous, current) = (points[i - 1], points[i]);
            var midX = (previous.X + current.X) / 2;
            figure.CubicBezierTo(new Point(midX, previous.Y), new Point(midX, current.Y), current);
        }

        figure.LineTo(new Point(width, points[^1].Y));
        if (closeAt is { } baselineY)
        {
            figure.LineTo(new Point(width, baselineY));
            figure.LineTo(new Point(0, baselineY));
        }

        figure.EndFigure(isClosed: closeAt is not null);
        return geometry;
    }

    private void Resubscribe(IReadOnlyList<EqualizerBandViewModel>? bands)
    {
        foreach (var band in _subscribed ?? [])
        {
            band.PropertyChanged -= OnBandChanged;
        }

        _subscribed = bands;
        foreach (var band in _subscribed ?? [])
        {
            band.PropertyChanged += OnBandChanged;
        }
    }

    private void OnBandChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
}
