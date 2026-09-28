using System.Globalization;
using System.Resources;

namespace HeadphoneControl.Resources;

// Hand-written because the project has no resx code generator configured; property names match the resource keys.
public static class Strings
{
    private static readonly ResourceManager Manager = new("HeadphoneControl.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Manager.GetString(key, CultureInfo.CurrentUICulture)
            ?? throw new MissingManifestResourceException($"Missing string resource '{key}'.");
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string AppTitle => Get(nameof(AppTitle));
    public static string Connect => Get(nameof(Connect));
    public static string Disconnect => Get(nameof(Disconnect));
    public static string Refresh => Get(nameof(Refresh));
    public static string Cancel => Get(nameof(Cancel));
    public static string Clear => Get(nameof(Clear));
    public static string Unknown => Get(nameof(Unknown));
    public static string UnknownValue => Get(nameof(UnknownValue));

    public static string Label_Battery => Get(nameof(Label_Battery));
    public static string Label_Firmware => Get(nameof(Label_Firmware));
    public static string Label_Codec => Get(nameof(Label_Codec));
    public static string Label_Charging => Get(nameof(Label_Charging));

    public static string Section_NoiseControl => Get(nameof(Section_NoiseControl));
    public static string Section_Equalizer => Get(nameof(Section_Equalizer));
    public static string Section_Sound => Get(nameof(Section_Sound));
    public static string Section_Diagnostics => Get(nameof(Section_Diagnostics));

    public static string Label_FocusOnVoice => Get(nameof(Label_FocusOnVoice));
    public static string Label_AmbientLevel => Get(nameof(Label_AmbientLevel));
    public static string Label_Preset => Get(nameof(Label_Preset));
    public static string Label_Dsee => Get(nameof(Label_Dsee));
}
