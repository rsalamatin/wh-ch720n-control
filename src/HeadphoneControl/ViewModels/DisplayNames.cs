using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Resources;

namespace HeadphoneControl.ViewModels;

// Resource keys are <Kind>_<EnumMember>, e.g. NoiseMode_Ambient.
internal static class DisplayNames
{
    public static string Of(ConnectionStatus value) => Strings.Get($"ConnectionStatus_{value}");

    public static string Of(NoiseControlMode value) => Strings.Get($"NoiseMode_{value}");

    public static string Of(EqualizerPreset value) => Strings.Get($"EqPreset_{value}");

    public static string Of(AudioCodec value) => Strings.Get($"Codec_{value}");

    public static IReadOnlyList<Choice<T>> ChoicesOf<T>(Func<T, string> label)
        where T : struct, Enum =>
        [.. Enum.GetValues<T>().Select(v => new Choice<T>(v, label(v)))];
}
