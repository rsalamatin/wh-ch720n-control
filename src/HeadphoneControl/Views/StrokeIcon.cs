using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace HeadphoneControl.Views;

// Draws a geometry from the 24x24 icon grid in Icons, scaled to the control's size and stroked with Foreground.
internal sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeThickness), 1.8);

    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<StrokeIcon, bool>(nameof(IsFilled));

    private const double GridSize = 24;

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty, IsFilledProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / GridSize;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            var pen = IsFilled ? null : new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(IsFilled ? brush : null, pen, data);
        }
    }
}
