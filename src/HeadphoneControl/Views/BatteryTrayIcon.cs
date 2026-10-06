using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace HeadphoneControl.Views;

internal static class BatteryTrayIcon
{
    // Windows shows tray icons at 16 px (100 % scaling) to 32 px (200 %) and scales this one down.
    private const int Size = 32;
    private const double GaugeHeight = 9;
    private const double GaugeInset = 2;

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15));
    private static readonly IBrush LevelBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64));
    private static readonly IBrush LowLevelBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x36));

    // Must run on the UI thread.
    public static WindowIcon Render(Bitmap appIcon, int level, bool isLow)
    {
        ArgumentNullException.ThrowIfNull(appIcon);
        using var target = new RenderTargetBitmap(new PixelSize(Size, Size));
        using (var context = target.CreateDrawingContext())
        {
            var gauge = new Rect(0, Size - GaugeHeight, Size, GaugeHeight);
            var brush = isLow ? LowLevelBrush : LevelBrush;
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            {
                // Squeezed into the space above the gauge, so the gauge hides none of the icon.
                var iconSize = Size - GaugeHeight;
                context.DrawImage(appIcon, new Rect((Size - iconSize) / 2, 0, iconSize, iconSize));
            }

            context.DrawRectangle(TrackBrush, new Pen(brush, 1), gauge.Deflate(0.5), 2, 2);
            var full = gauge.Deflate(GaugeInset);
            var width = full.Width * Math.Clamp(level, 0, 100) / 100;
            if (width > 0)
            {
                // At least one tray pixel wide, so 1 % doesn't look empty.
                context.FillRectangle(brush, full.WithWidth(Math.Max(width, 2)));
            }
        }

        // WindowIcon copies the pixels, so the render target can go.
        return new WindowIcon(target);
    }
}
