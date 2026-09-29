using Avalonia.Media;

namespace HeadphoneControl.Views;

// Icons on a 24x24 grid, drawn by StrokeIcon.
internal static class Icons
{
    public static Geometry Headphones { get; } = StreamGeometry.Parse(
        "M4 14 V12 A8 8 0 0 1 20 12 V14 " +
        "M4.5 14 H5.5 A1.5 1.5 0 0 1 7 15.5 V18.5 A1.5 1.5 0 0 1 5.5 20 H4.5 A1.5 1.5 0 0 1 3 18.5 V15.5 A1.5 1.5 0 0 1 4.5 14 Z " +
        "M18.5 14 H19.5 A1.5 1.5 0 0 1 21 15.5 V18.5 A1.5 1.5 0 0 1 19.5 20 H18.5 A1.5 1.5 0 0 1 17 18.5 V15.5 A1.5 1.5 0 0 1 18.5 14 Z");

    public static Geometry NoiseCancelling { get; } = StreamGeometry.Parse(
        "M4 14 V12 A8 8 0 0 1 20 12 V14 " +
        "M4.5 14 H5.5 A1.5 1.5 0 0 1 7 15.5 V18.5 A1.5 1.5 0 0 1 5.5 20 H4.5 A1.5 1.5 0 0 1 3 18.5 V15.5 A1.5 1.5 0 0 1 4.5 14 Z " +
        "M18.5 14 H19.5 A1.5 1.5 0 0 1 21 15.5 V18.5 A1.5 1.5 0 0 1 19.5 20 H18.5 A1.5 1.5 0 0 1 17 18.5 V15.5 A1.5 1.5 0 0 1 18.5 14 Z " +
        "M10 12 H14");

    public static Geometry Ambient { get; } = StreamGeometry.Parse(
        "M8.5 8.5 A5 5 0 0 0 8.5 15.5 M5.5 5.5 A9 9 0 0 0 5.5 18.5 " +
        "M15.5 8.5 A5 5 0 0 1 15.5 15.5 M18.5 5.5 A9 9 0 0 1 18.5 18.5 " +
        "M13.4 12 A1.4 1.4 0 1 1 10.6 12 A1.4 1.4 0 1 1 13.4 12 Z");

    public static Geometry Off { get; } = StreamGeometry.Parse(
        "M20 12 A8 8 0 1 1 4 12 A8 8 0 1 1 20 12 Z M6.5 17.5 L17.5 6.5");

    public static Geometry Chevron { get; } = StreamGeometry.Parse("M6 9 L12 15 L18 9");

    public static Geometry Reset { get; } = StreamGeometry.Parse("M4 12 A8 8 0 1 0 6.3 6.4 M4 4 V8 H8");

    public static Geometry Alert { get; } = StreamGeometry.Parse(
        "M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M12 7.5 V13 M12 16.5 V16.6");

    public static Geometry Close { get; } = StreamGeometry.Parse("M7 7 L17 17 M17 7 L7 17");

    public static Geometry More { get; } = StreamGeometry.Parse(
        "M5 11 A1 1 0 1 1 5 13 A1 1 0 1 1 5 11 Z M12 11 A1 1 0 1 1 12 13 A1 1 0 1 1 12 11 Z M19 11 A1 1 0 1 1 19 13 A1 1 0 1 1 19 11 Z");

    public static Geometry Bolt { get; } = StreamGeometry.Parse("M13 2 L4 14 H11 L10 22 L19 10 H12 Z");
}
