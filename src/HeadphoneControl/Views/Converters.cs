using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Views;

internal static class Converters
{
    public static FuncValueConverter<NoiseControlMode, Geometry> NoiseModeIcon { get; } = new(mode => mode switch
    {
        NoiseControlMode.NoiseCancelling => Icons.NoiseCancelling,
        NoiseControlMode.Ambient => Icons.Ambient,
        _ => Icons.Off,
    });

    public static FuncValueConverter<int, double> PercentToSweep { get; } = new(percent => Math.Clamp(percent, 0, 100) * 3.6);

    public static FuncValueConverter<string?, string?> ToUpper { get; } = new(text => text?.ToUpper(CultureInfo.CurrentCulture));
}
